using System;
using Dotty.Abstractions.Adapter;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Adapter that connects the parser callbacks to a TerminalBuffer and exposes a render event.
/// Keeps responsibilities minimal: buffer management and render notification.
/// </summary>
public class TerminalAdapter : ITerminalHandler
{
    public enum MouseMode
    {
        None = 0,
        X10 = 9,
        Normal = 1000,
        ButtonEvent = 1002,
        AnyEvent = 1003
    }

    public enum MouseEncoding
    {
        Default = 0,
        UTF8 = 1005,
        SGR = 1006,
        URXVT = 1015
    }

    private readonly TerminalBuffer _buffer;
    private CellAttributes _currentAttributes = CellAttributes.Default;
    private CellAttributes _savedAttributes = CellAttributes.Default;
    private bool _hasSavedAttributes;
    private string? _windowTitle;
    private char _lastPrintedChar;

    public int CursorShape { get; private set; }
    public bool KeypadApplicationMode { get; private set; }

    public MouseMode CurrentMouseMode { get; private set; } = MouseMode.None;
    public MouseEncoding CurrentMouseEncoding { get; private set; } = MouseEncoding.Default;
    public bool MouseReportingEnabled => CurrentMouseMode != MouseMode.None;

    public TerminalAdapter(int rows = 24, int columns = 80)
    {
        _buffer = new TerminalBuffer(rows, columns);
    }

    public event Action<string>? RenderRequested;
    public event Action<string>? ClipboardWriteRequested;
    public event Action<string>? TitleChanged;
#pragma warning disable CS0067 // Event is never used, but required by interface
    public event Action<string>? LinkOpened;
#pragma warning restore CS0067

    public void OnHyperlink(string uri) { _currentAttributes.HyperlinkId = _buffer.GetOrCreateHyperlinkId(uri); }

    public event Action<string>? ReplyRequested;
    public TerminalBuffer Buffer => _buffer;
    object? ITerminalHandler.Buffer => _buffer;
    public Buffer.StyleSet StyleSet => _buffer.StyleSet;

    /// <summary>
    /// Optional trace hook for diagnostics. Subscribe to receive snapshot events
    /// at key buffer-modifying operations. String parameter is the reason/event name.
    /// </summary>
    public Action<string, TerminalBuffer>? Trace { get; set; }

    public string? WindowTitle => _windowTitle;

    public void ResizeBuffer(int rows, int columns)
    {
        try
        {
            _buffer.Resize(rows, columns);
            RequestRender();
        }
        catch { }
    }

    public void OnPrint(ReadOnlySpan<char> text)
    {
        _buffer.WriteText(text, _currentAttributes);
        if (!text.IsEmpty)
        {
            _lastPrintedChar = text[text.Length - 1];
        }
        if (text.Length > 40)
            Trace?.Invoke($"Print({text.Length}chars)", _buffer);
        RequestRender();
    }

    /// <summary>
    /// Fast path: writes ASCII bytes directly to the buffer, bypassing the
    /// byte→char conversion that the public ITerminalHandler interface requires.
    /// Called from BasicAnsiParser when it detects a pure-ASCII run.
    /// </summary>
    internal void OnPrintAscii(ReadOnlySpan<byte> text)
    {
        _buffer.WriteAscii(text, _currentAttributes);
        if (!text.IsEmpty)
            _lastPrintedChar = (char)text[text.Length - 1];
        RequestRender();
    }


    public void OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload)
    {
        if (code == 0 || code == 2)
        {
            _windowTitle = payload.ToString();
            TitleChanged?.Invoke(_windowTitle);
            RequestRender();
        }
        else if (code == 8)
        {
            var payloadStr = payload.ToString();
            int semiIdx = payloadStr.IndexOf(';');
            if (semiIdx >= 0)
            {
                var uri = payloadStr.Substring(semiIdx + 1);
                OnHyperlink(uri);
            }
            else
            {
                OnHyperlink(string.Empty);
            }
        }
        else if (code == 52)
        {
            var payloadStr = payload.ToString();
            int semiIdx = payloadStr.IndexOf(';');
            if (semiIdx >= 0)
            {
                var base64Part = payloadStr.Substring(semiIdx + 1);
                if (base64Part != "?")
                {
                    try {
                        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Part));
                        ClipboardWriteRequested?.Invoke(decoded);
                    } catch { }
                }
            }
        }
        else if (code == 133)
        {
            // Shell Integration (OSC 133) — FinalTerm / Suzi protocol
            if (payload.Length == 0) return;
            var subcmd = payload[0];
            switch (subcmd)
            {
                case 'A': // Prompt start
                    _buffer.AddPromptMark(PromptKind.Prompt);
                    break;
                case 'B': // Command start
                    _buffer.AddPromptMark(PromptKind.Command);
                    break;
                case 'C': // Output start
                    _buffer.AddPromptMark(PromptKind.Output);
                    break;
                case 'D': // Command end / output done
                    _buffer.AddPromptMark(PromptKind.CommandEnd);
                    break;
            }
        }
    }

    public void OnSaveCursor()
    {
        _buffer.SaveCursor();
        _savedAttributes = _currentAttributes;
        _hasSavedAttributes = true;
    }

    public void OnRestoreCursor()
    {
        _buffer.RestoreCursor();
        if (_hasSavedAttributes)
        {
            _currentAttributes = _savedAttributes;
            _hasSavedAttributes = false;
        }
        RequestRender();
    }

    public void OnSetAutoWrap(bool enabled)
    {
        _buffer.SetAutoWrap(enabled);
    }

    public void OnSetTabStop()
    {
        _buffer.SetTabStopAt(_buffer.CursorCol);
    }

    public void OnClearTabStop()
    {
        _buffer.ClearTabStopAt(_buffer.CursorCol);
    }

    public void OnClearAllTabStops()
    {
        _buffer.ClearAllTabStops();
    }

    public void OnReverseIndex()
    {
        _buffer.ReverseIndex();
        Trace?.Invoke($"RI cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnSetBracketedPasteMode(bool enabled)
    {
        _buffer.SetBracketedPasteMode(enabled);
    }

    public void OnDeviceStatusReport(int code)
    {
        switch (code)
        {
            case 6:
                // Cursor Position Report (CPR) requested via DSR variant:
                var r = _buffer.CursorRow + 1;
                var c = _buffer.CursorCol + 1;
                ReplyRequested?.Invoke($"\u001b[{r};{c}R");
                break;
            case 5:
            case 0:
                // Terminal status OK
                ReplyRequested?.Invoke("\u001b[0n");
                break;
            default:
                // Unknown/unsupported: return failure
                ReplyRequested?.Invoke("\u001b[3n");
                break;
        }
    }

    public void OnCursorPositionReport()
    {
        // DEC private CPR response for CSI ? 6 n requests.
        var r = _buffer.CursorRow + 1;
        var c = _buffer.CursorCol + 1;
        ReplyRequested?.Invoke($"\u001b[?{r};{c}R");
    }

    public void OnInsertChars(int n)
    {
        _buffer.InsertChars(n);
        RequestRender();
    }

    public void OnDeleteChars(int n)
    {
        _buffer.DeleteChars(n);
        RequestRender();
    }

    public void OnEraseCharacters(int n)
    {
        _buffer.EraseCharacters(n);
        Trace?.Invoke($"ECH({n})", _buffer);
        RequestRender();
    }

    public void OnInsertLines(int n)
    {
        _buffer.InsertLines(n);
        Trace?.Invoke($"IL({n})", _buffer);
        RequestRender();
    }

    public void OnDeleteLines(int n)
    {
        _buffer.DeleteLines(n);
        Trace?.Invoke($"DL({n})", _buffer);
        RequestRender();
    }

    public void OnClearScreen()
    {
        _buffer.EraseDisplay(2);
        Trace?.Invoke("ED(2)", _buffer);
        RequestRender();
    }

    public void OnClearScrollback()
    {
        _buffer.ClearScrollback();
        RequestRender();
    }

    public void OnEraseDisplay(int mode)
    {
        _buffer.EraseDisplay(mode);
        Trace?.Invoke($"ED({mode})", _buffer);
        RequestRender();
    }

    public void OnSetGraphicsRendition(ReadOnlySpan<char> parameters)
    {
        _currentAttributes = SgrParserArgb.Apply(parameters, _currentAttributes);
    }

    public void OnMoveCursor(int row, int col)
    {
        _buffer.SetCursor(Math.Max(0, row - 1), Math.Max(0, col - 1));
        Trace?.Invoke($"CUP({row},{col})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorUp(int n)
    {
        _buffer.MoveCursorBy(-Math.Max(1, n), 0);
        Trace?.Invoke($"CUU({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorDown(int n)
    {
        _buffer.MoveCursorBy(Math.Max(1, n), 0);
        Trace?.Invoke($"CUD({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorForward(int n)
    {
        _buffer.MoveCursorBy(0, Math.Max(1, n));
        Trace?.Invoke($"CUF({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorBack(int n)
    {
        _buffer.MoveCursorBy(0, -Math.Max(1, n));
        Trace?.Invoke($"CUB({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnEraseLine(int mode)
    {
        _buffer.EraseLine(mode);
        Trace?.Invoke($"EL({mode}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCarriageReturn()
    {
        _buffer.CarriageReturn();
        RequestRender();
    }

    public void OnLineFeed()
    {
        _buffer.LineFeed();
        Trace?.Invoke($"LF cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnSetAlternateScreen(bool enabled)
    {
        _buffer.SetAlternateScreen(enabled);
        Trace?.Invoke($"AltScreen({enabled})", _buffer);
        RequestRender();
    }

    public void OnSetOriginMode(bool enabled)
    {
        _buffer.SetOriginMode(enabled);
        Trace?.Invoke($"DECOM({enabled})", _buffer);
        RequestRender();
    }

    public void OnSetScrollRegion(int top1Based, int bottom1Based)
    {
        // If bottom omitted (0), treat as full screen bottom
        if (bottom1Based == 0) bottom1Based = _buffer.Rows;
        _buffer.SetScrollRegion(top1Based, bottom1Based);
        Trace?.Invoke($"DECSTBM({top1Based},{bottom1Based})", _buffer);
        RequestRender();
    }

    public void OnSetCursorVisibility(bool visible)
    {
        _buffer.SetCursorVisible(visible);
        RequestRender();
    }

    public void OnBell()
    {
    }

    public void OnCursorHorizontalAbsolute(int col)
    {
        // CHA - CSI n G - move cursor to column n (1-based)
        int targetCol = Math.Max(0, col - 1);
        _buffer.MoveCursorBy(0, targetCol - _buffer.CursorCol);
        Trace?.Invoke($"CHA({col})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorVerticalAbsolute(int row)
    {
        // VPA - CSI n d - move cursor to row n (1-based)
        _buffer.MoveCursorTo(Math.Max(0, row - 1), _buffer.CursorCol);
        Trace?.Invoke($"VPA({row})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorNextLine(int n)
    {
        // CNL - CSI n E - move cursor down n lines, to column 1
        _buffer.MoveCursorBy(Math.Max(1, n), -_buffer.CursorCol);
        Trace?.Invoke($"CNL({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorPreviousLine(int n)
    {
        // CPL - CSI n F - move cursor up n lines, to column 1
        _buffer.MoveCursorBy(-Math.Max(1, n), -_buffer.CursorCol);
        Trace?.Invoke($"CPL({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnScrollUp(int n)
    {
        // SU - CSI n S - scroll up n lines within scroll region
        _buffer.ScrollUpLines(Math.Max(1, n));
        Trace?.Invoke($"SU({n})", _buffer);
        RequestRender();
    }

    public void OnScrollDown(int n)
    {
        // SD - CSI n T - scroll down n lines within scroll region
        _buffer.ScrollDownLines(Math.Max(1, n));
        Trace?.Invoke($"SD({n})", _buffer);
        RequestRender();
    }

    public void OnFullReset()
    {
        // RIS - ESC c - full terminal reset
        _buffer.FullReset();
        _currentAttributes = CellAttributes.Default;
        _savedAttributes = CellAttributes.Default;
        _hasSavedAttributes = false;
        _windowTitle = null;
        CursorShape = 0;
        KeypadApplicationMode = false;
        RequestRender();
    }

    public void OnRepeatCharacter(int n)
    {
        // REP - CSI n b - repeat previous character n times
        if (_lastPrintedChar == '\0' || n <= 0) return;
        Span<char> chars = stackalloc char[Math.Min(n, 256)];
        chars.Fill(_lastPrintedChar);
        int remaining = n;
        while (remaining > 0)
        {
            int batch = Math.Min(remaining, 256);
            _buffer.WriteText(chars.Slice(0, batch), _currentAttributes);
            remaining -= batch;
        }
        RequestRender();
    }

    public void OnTab()
    {
        // HT - horizontal tab
        int nextStop = _buffer.GetNextTabStopFrom(_buffer.CursorCol);
        _buffer.MoveCursorBy(0, nextStop - _buffer.CursorCol);
        Trace?.Invoke($"HT→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnBackTab(int n)
    {
        // CBT - CSI n Z - cursor backward tabulation
        int col = _buffer.CursorCol;
        for (int i = 0; i < Math.Max(1, n); i++)
        {
            col = _buffer.GetPrevTabStopFrom(col);
        }
        _buffer.MoveCursorBy(0, col - _buffer.CursorCol);
        Trace?.Invoke($"CBT({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnSetCursorShape(int shape)
    {
        CursorShape = shape;
        RequestRender();
    }

    public void OnSetKeypadApplicationMode(bool enabled)
    {
        KeypadApplicationMode = enabled;
    }

    public void OnSetApplicationCursorKeys(bool enabled)
    {
        // TODO: track application cursor keys mode if needed
    }

    public void OnSendDeviceAttributes(int daType)
    {
        switch (daType)
        {
            case 0:
            case 1:
                ReplyRequested?.Invoke("\u001b[?1;0c");
                break;
            case 2:
                ReplyRequested?.Invoke("\u001b[>0;0;0c");
                break;
        }
    }

    public void OnMouseEvent(int button, int col, int row, bool isPress)
    {
    }

    public void OnSetMouseMode(int mode, bool enabled)
    {
        if (mode == 9 || mode == 1000 || mode == 1002 || mode == 1003)
        {
            if (enabled)
            {
                CurrentMouseMode = (MouseMode)mode;
            }
            else if (CurrentMouseMode == (MouseMode)mode)
            {
                CurrentMouseMode = MouseMode.None;
            }
        }
        else if (mode == 1005 || mode == 1006 || mode == 1015)
        {
            if (enabled)
            {
                CurrentMouseEncoding = (MouseEncoding)mode;
            }
            else if (CurrentMouseEncoding == (MouseEncoding)mode)
            {
                CurrentMouseEncoding = MouseEncoding.Default;
            }
        }
    }

    private bool _renderDirty;
    private bool _synchronizedUpdateActive;

    public void OnSetSynchronizedUpdate(bool enabled)
    {
        _synchronizedUpdateActive = enabled;
        if (!enabled) FlushRender();
    }

    public int KittyKeyboardMode { get; private set; }

    public void OnSetKittyKeyboardMode(int mode)
    {
        KittyKeyboardMode = mode;
    }

    public void OnQueryKittyKeyboard()
    {
        ReplyRequested?.Invoke($"\x1b[{KittyKeyboardMode}u");
    }

    public void FlushRender()
    {
        if (_synchronizedUpdateActive) return;
        if (_renderDirty)
        {
            _renderDirty = false;
            RenderRequested?.Invoke(string.Empty);
        }
    }

    private void RequestRender()
    {
        _renderDirty = true;
    }

    public void RequestRenderExtern()
    {
        RequestRender();
        FlushRender();
    }
}
