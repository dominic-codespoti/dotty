using System;
using System.Threading;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Config;

namespace Dotty.Terminal.Adapter;

public delegate void TerminalReplyHandler(ReadOnlySpan<char> reply);

/// <summary>
/// Adapter that connects the parser callbacks to a TerminalBuffer and exposes a render event.
/// Keeps responsibilities minimal: buffer management and render notification.
/// </summary>
public partial class TerminalAdapter : ITerminalHandler, IDisposable
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
    private string _defaultFgHex = "#CCCCCC";
    private string _defaultBgHex = "#1E1E1E";
    private string _da2Response = "\u001b[>1;0;0c";
    private int _windowPixelWidth = 800;
    private int _windowPixelHeight = 600;
    private bool _hasSavedAttributes;
    private string? _windowTitle;
    private char _lastPrintedChar;

    public int CursorShape { get; private set; }
    public bool KeypadApplicationMode { get; private set; }
    public bool ApplicationCursorKeysEnabled { get; private set; }
    public MouseMode CurrentMouseMode { get; private set; } = MouseMode.None;
    public MouseEncoding CurrentMouseEncoding { get; private set; } = MouseEncoding.Default;
    public bool MouseReportingEnabled => CurrentMouseMode != MouseMode.None;
    public int ModifyOtherKeysLevel { get; private set; }

    public TerminalAdapter(int rows = 24, int columns = 80, int scrollbackCapacity = 10000, TimeProvider? timeProvider = null)
    {
        _buffer = new TerminalBuffer(rows, columns, scrollbackCapacity);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<string>? RenderRequested;
    public event Action<string>? ClipboardWriteRequested;
    public event Action<string>? TitleChanged;
    public event Action? PaletteChanged;
    public event Action? Bell;

    public void OnHyperlink(string uri) => _currentAttributes.HyperlinkId = _buffer.GetOrCreateHyperlinkId(uri);

    public event TerminalReplyHandler? ReplyRequested;
    private void SendTwoNumberReply(ReadOnlySpan<char> prefix, int first, int second, char suffix)
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        Span<char> reply = stackalloc char[48];
        prefix.CopyTo(reply);
        int length = prefix.Length;
        first.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        second.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = suffix;
        handler(reply[..length]);
    }

    private void SendNumberReply(ReadOnlySpan<char> prefix, int value, char suffix)
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        Span<char> reply = stackalloc char[32];
        prefix.CopyTo(reply);
        int length = prefix.Length;
        value.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = suffix;
        handler(reply[..length]);
    }

    private void SendColorReply(int code, ReadOnlySpan<char> hex)
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        if (hex.Length <= 64)
        {
            Span<char> reply = stackalloc char[72];
            WriteColorReply(handler, code, hex, reply);
        }
        else
        {
            char[] reply = new char[hex.Length + 8];
            WriteColorReply(handler, code, hex, reply);
        }
    }

    private static void WriteColorReply(
        TerminalReplyHandler handler,
        int code,
        ReadOnlySpan<char> hex,
        Span<char> reply)
    {
        "\x1b]".AsSpan().CopyTo(reply);
        int length = 2;
        code.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        hex.CopyTo(reply[length..]);
        length += hex.Length;
        reply[length++] = '\a';
        handler(reply[..length]);
    }

    public TerminalBuffer Buffer => _buffer;
    object? ITerminalHandler.Buffer => _buffer;
    public Buffer.StyleSet StyleSet => _buffer.StyleSet;

    /// <summary>
    /// Optional trace hook for diagnostics. Subscribe to receive snapshot events
    /// at key buffer-modifying operations. String parameter is the reason/event name.
    /// </summary>
    public Action<string, TerminalBuffer>? Trace { get; set; }

    public string? WindowTitle => _windowTitle;

    /// <summary>
    /// Sets the default foreground and background hex colors (without #) for
    /// OSC 10/11 queries. Called by the app layer on startup and theme change.
    /// </summary>
    public void SetDefaultColors(string fgHex, string bgHex)
    {
        if (!string.IsNullOrWhiteSpace(fgHex)) _defaultFgHex = fgHex.StartsWith('#') ? fgHex : "#" + fgHex;
        if (!string.IsNullOrWhiteSpace(bgHex)) _defaultBgHex = bgHex.StartsWith('#') ? bgHex : "#" + bgHex;
    }

    /// <summary>
    /// Sets the DA2 (Secondary Device Attributes) response string, e.g.
    /// "\x1b[>1;300;0c" for Dotty 0.3.0. Called by the app layer on startup.
    /// </summary>
    public void SetTerminalIdentity(string da2Response)
    {
        if (!string.IsNullOrWhiteSpace(da2Response)) _da2Response = da2Response;
    }

    /// <summary>
    /// Sets the window pixel dimensions for CSI 14 t queries.
    /// Called by the app layer when the window is resized.
    /// </summary>
    public void SetWindowPixelSize(int width, int height)
    {
        _windowPixelWidth = Math.Max(1, width);
        _windowPixelHeight = Math.Max(1, height);
    }

    public void OnWindowReport(int command)
    {
        switch (command)
        {
            case 14:
                // CSI 14 t → report window pixel size: CSI 4 ; height ; width t
                SendTwoNumberReply("\x1b[4;".AsSpan(), _windowPixelHeight, _windowPixelWidth, 't');
                break;
            case 18:
                // CSI 18 t → report window cell size: CSI 8 ; rows ; cols t
                SendTwoNumberReply("\x1b[8;".AsSpan(), _buffer.Rows, _buffer.Columns, 't');
                break;
            case 20:
            case 21:
                // Icon title (20) / window title (21) — respond with empty for now.
                ReplyRequested?.Invoke("\x1b]0;\x1b\\".AsSpan());
                break;
        }
    }

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
        if (TryHandleShellIntegration(code, payload)) return;
        if (code == 0 || code == 2)
        {
            if (_windowTitle is null || !payload.SequenceEqual(_windowTitle.AsSpan()))
                _windowTitle = payload.ToString();
            TitleChanged?.Invoke(_windowTitle);
            RequestRender();
        }
        else if (code == 4)
        {
            HandleOscPalette(payload);
        }
        else if (code == 8)
        {
            int semiIdx = payload.IndexOf(';');
            _currentAttributes.HyperlinkId = semiIdx >= 0
                ? _buffer.GetOrCreateHyperlinkId(payload[(semiIdx + 1)..])
                : (ushort)0;
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
                    try
                    {
                        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Part));
                        ClipboardWriteRequested?.Invoke(decoded);
                    }
                    catch { }
                }
            }
        }
        else if (code == 10 || code == 11 || code == 12)
        {
            HandleOscDynamicColor(code, payload);
        }
    }

    // OSC 4 ; index ; spec pairs: set entries (remapping existing styles and
    // firing AnsiPaletteChanged) or query with '?' (replying the live entry).
    // Indices 0-15 target the ANSI palette; 16-255 target the xterm ramp.
    private void HandleOscPalette(ReadOnlySpan<char> payload)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            int semi = payload[pos..].IndexOf(';');
            ReadOnlySpan<char> indexField = semi < 0 ? payload[pos..] : payload.Slice(pos, semi);
            pos = semi < 0 ? payload.Length : pos + semi + 1;
            int colorSemi = payload[pos..].IndexOf(';');
            ReadOnlySpan<char> colorField = colorSemi < 0 ? payload[pos..] : payload.Slice(pos, colorSemi);
            pos = colorSemi < 0 ? payload.Length : pos + colorSemi + 1;
            if (!TryParsePaletteIndex(indexField, out int index))
                return;
            if (colorField.Length == 1 && colorField[0] == '?')
            {
                Span<char> query = stackalloc char[8];
                WritePaletteQuery(index, query);
                SendColorReply(4, query[..7]);
                continue;
            }
            if (!TryParseColorSpec(colorField, out uint argb))
                continue;
            SetPaletteEntry(index, argb);
        }
    }

    private void SetPaletteEntry(int index, uint argb)
    {
        if (index is >= 0 and < 16)
        {
            uint[] palette = SgrColorArgb.GetAnsiPaletteSnapshot();
            if (palette[index] == argb)
                return;
            palette[index] = argb;
            ApplyAnsiPalette(palette);
            return;
        }
        SgrColorArgb.SetExtendedPalette(index, argb);
        _buffer.InvalidateRowsForPaletteChange();
        PaletteChanged?.Invoke();
        RequestRender();
    }
    private static void WritePaletteQuery(int index, Span<char> destination)
    {
        uint argb = index is >= 0 and < 16
            ? SgrColorArgb.GetAnsiPaletteSnapshot()[index]
            : SgrColorArgb.From256(index).Argb;
        const string digits = "0123456789ABCDEF";
        destination[0] = '#';
        for (int i = 0; i < 6; i++)
            destination[1 + i] = digits[(int)((argb >> (20 - 4 * i)) & 0xF)];
    }

    private static bool TryParsePaletteIndex(ReadOnlySpan<char> field, out int index)
    {
        index = 0;
        if (field.IsEmpty || field.Length > 3)
            return false;
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] < '0' || field[i] > '9')
                return false;
            index = index * 10 + (field[i] - '0');
        }
        return index is >= 0 and < 256;
    }

    // Parses rgb:RR/GG/BB, #RGB, #RRGGBB. Rejects anything else so a
    // malformed spec skips one entry instead of corrupting the palette.
    private static bool TryParseColorSpec(ReadOnlySpan<char> spec, out uint argb)
    {
        argb = 0;
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> body = spec.Slice(4);
            int first = body.IndexOf('/');
            int second = body[(first + 1)..].IndexOf('/');
            if (first <= 0 || second <= 0)
                return false;
            second += first + 1;
            if (body[(second + 1)..].IndexOf('/') >= 0)
                return false;
            if (!TryParseHexComponent(body[..first], out byte r) ||
                !TryParseHexComponent(body.Slice(first + 1, second - first - 1), out byte g) ||
                !TryParseHexComponent(body[(second + 1)..], out byte b))
                return false;
            argb = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            return true;
        }
        ReadOnlySpan<char> hex = spec;
        if (hex.Length > 0 && hex[0] == '#')
            hex = hex[1..];
        if (hex.Length is not (3 or 6))
            return false;
        foreach (char c in hex)
        {
            if (!IsHexChar(c))
                return false;
        }
        int stride = hex.Length / 3;
        byte rByte = ExpandHex(hex[..stride]);
        byte gByte = ExpandHex(hex.Slice(stride, stride));
        byte bByte = ExpandHex(hex.Slice(2 * stride, stride));
        argb = 0xFF000000u | ((uint)rByte << 16) | ((uint)gByte << 8) | bByte;
        return true;
    }

    private static bool TryParseHexComponent(ReadOnlySpan<char> component, out byte value)
    {
        value = 0;
        if (component.IsEmpty || component.Length > 4)
            return false;
        foreach (char c in component)
        {
            if (!IsHexChar(c))
                return false;
        }
        // Scale 1-4 hex digits to 8 bits (xterm: replicate top digit down).
        uint parsed = 0;
        for (int i = 0; i < component.Length; i++)
            parsed = (parsed << 4) | (uint)HexNibble(component[i]);
        uint scaled = component.Length switch
        {
            1 => (parsed << 4) | parsed,
            2 => parsed,
            3 => parsed >> 4,
            _ => parsed >> 8,
        };
        value = (byte)(scaled & 0xFF);
        return true;
    }

    private static byte ExpandHex(ReadOnlySpan<char> digits)
    {
        uint parsed = 0;
        for (int i = 0; i < digits.Length; i++)
            parsed = (parsed << 4) | (uint)HexNibble(digits[i]);
        if (digits.Length == 1)
            return (byte)((parsed << 4) | parsed);
        if (digits.Length == 2)
            return (byte)parsed;
        return (byte)(parsed >> 4);
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // OSC 10/11/12: a '?' payload queries the live color; any other payload
    // sets the default and reports it back, matching xterm behavior.
    private void HandleOscDynamicColor(int code, ReadOnlySpan<char> payload)
    {
        if (payload.Length == 1 && payload[0] == '?')
        {
            SendColorReply(code, CurrentDynamicColor(code).AsSpan());
            return;
        }
        if (!TryParseColorSpec(payload, out uint argb))
        {
            SendColorReply(code, CurrentDynamicColor(code).AsSpan());
            return;
        }
        string hex = $"#{argb & 0xFFFFFF:X6}";
        if (code == 10)
            _defaultFgHex = hex;
        else if (code == 11)
            _defaultBgHex = hex;
        SendColorReply(code, hex.AsSpan());
        if (code is 10 or 11)
        {
            _buffer.InvalidateRowsForPaletteChange();
            PaletteChanged?.Invoke();
            RequestRender();
        }
    }

    private string CurrentDynamicColor(int code) => code switch
    {
        10 => _defaultFgHex,
        11 => _defaultBgHex,
        _ => "#FFFFFF",
    };

    // Applies a full 16-entry palette through the theme path so existing
    // styles remap and the change event fires for host invalidation.
    private void ApplyAnsiPalette(uint[] palette)
    {
        uint[] previous = SgrColorArgb.GetAnsiPaletteSnapshot();
        SgrColorArgb.SetAnsiPalette(palette);
        _buffer.StyleSet.RemapAnsiPalette(previous, palette);
        _buffer.InvalidateRowsForPaletteChange();
        PaletteChanged?.Invoke();
        RequestRender();
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
                SendTwoNumberReply("\x1b[".AsSpan(), _buffer.CursorRow + 1, _buffer.CursorCol + 1, 'R');
                break;
            case 5:
            case 0:
                // Terminal status OK
                ReplyRequested?.Invoke("\x1b[0n".AsSpan());
                break;
            default:
                // Unknown/unsupported: return failure
                ReplyRequested?.Invoke("\x1b[3n".AsSpan());
                break;
        }
    }

    public void OnCursorPositionReport()
    {
        // DEC private CPR response for CSI ? 6 n requests.
        SendTwoNumberReply("\x1b[?".AsSpan(), _buffer.CursorRow + 1, _buffer.CursorCol + 1, 'R');
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
        if (Environment.GetEnvironmentVariable("DOTTY_DIAG") != null)
            Console.Error.WriteLine($"[DIAG] ED({mode}) cursor=({_buffer.CursorRow},{_buffer.CursorCol})");
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
        SetKittyAlternateScreen(enabled);
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
        Bell?.Invoke();
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
        ApplicationCursorKeysEnabled = false;
        ModifyOtherKeysLevel = 0;
        SgrColorArgb.ResetExtendedPalette();
        ResetKittyKeyboardState();
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
        var (cursorShape, blinking) = shape switch
        {
            0 or 1 => (TerminalCursorShape.Block, true),
            2 => (TerminalCursorShape.Block, false),
            3 => (TerminalCursorShape.Underline, true),
            4 => (TerminalCursorShape.Underline, false),
            5 => (TerminalCursorShape.Beam, true),
            6 => (TerminalCursorShape.Beam, false),
            _ => (TerminalCursorShape.Block, true),
        };
        _buffer.SetCursorStyle(cursorShape, blinking);
        RequestRender();
    }

    public void OnSetKeypadApplicationMode(bool enabled)
    {
        KeypadApplicationMode = enabled;
    }

    public void OnSetApplicationCursorKeys(bool enabled)
    {
        ApplicationCursorKeysEnabled = enabled;
    }

    public void OnSendDeviceAttributes(int daType)
    {
        switch (daType)
        {
            case 0:
            case 1:
                // DA1: report the conservative VT100 capability set supported here.
                ReplyRequested?.Invoke("\x1b[?1;0c".AsSpan());
                break;
            case 2:
                ReplyRequested?.Invoke(_da2Response.AsSpan());
                break;
                // DA3 (CSI = c) is not implemented; do not claim an identity/capability.
        }
    }

    public void OnRequestMode(int mode, bool isPrivate)
    {
        // DECRQM: 1=set, 2=reset, 3=permanently set, 4=permanently reset, 0=not recognized.
        // Only modes Dotty actually tracks report set/reset; everything else is 0.
        int state = (mode, isPrivate) switch
        {
            (1, true) => ApplicationCursorKeysEnabled ? 1 : 2, // DECCKM
            (6, true) => _buffer.OriginMode ? 1 : 2, // DECOM
            (7, false) => _buffer.AutoWrap ? 1 : 2, // DECAWM (ANSI form)
            (25, true) => _buffer.CursorVisible ? 1 : 2, // DECTCEM
            (1000, true) => CurrentMouseMode == MouseMode.Normal ? 1 : 2,
            (1002, true) => CurrentMouseMode == MouseMode.ButtonEvent ? 1 : 2,
            (1003, true) => CurrentMouseMode == MouseMode.AnyEvent ? 1 : 2,
            (1004, true) => FocusReportingEnabled ? 1 : 2,
            (1006, true) => CurrentMouseEncoding == MouseEncoding.SGR ? 1 : 2,
            (1049, true) => _buffer.IsAlternateScreenActive ? 1 : 2,
            (2004, true) => _buffer.BracketedPasteMode ? 1 : 2,
            (2026, true) => SynchronizedUpdateActive ? 1 : 2,
            _ => 0,
        };
        var handler = ReplyRequested;
        if (handler is null)
            return;
        Span<char> reply = stackalloc char[32];
        int length = 0;
        if (isPrivate)
        {
            "\x1b[?".AsSpan().CopyTo(reply);
            length = 3;
        }
        else
        {
            "\x1b[".AsSpan().CopyTo(reply);
            length = 2;
        }
        mode.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        state.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = '$';
        reply[length++] = 'y';
        handler(reply[..length]);
    }

    public void OnQueryCapability(string requestHex)
    {
        // XTGETTCAP: answer only capabilities Dotty actually implements.
        // Unknown keys get no reply so applications fall back instead of
        // trusting a claim this terminal cannot honor.
        OnQueryCapability(requestHex.AsSpan());
    }

    internal void OnQueryCapability(ReadOnlySpan<char> requestHex)
    {
        var handler = ReplyRequested;
        if (handler is null || requestHex.IsEmpty)
            return;
        Span<char> valueHex = stackalloc char[64];
        int valueLength = GetCapabilityValueHex(requestHex, valueHex);
        if (valueLength < 0)
            return;
        int total = 5 + requestHex.Length + 1 + valueLength + 2;
        Span<char> reply = total <= 128 ? stackalloc char[128] : new char[total];
        "\x1bP1$r".AsSpan().CopyTo(reply);
        int length = 5;
        requestHex.CopyTo(reply[length..]);
        length += requestHex.Length;
        reply[length++] = '=';
        valueHex[..valueLength].CopyTo(reply[length..]);
        length += valueLength;
        reply[length++] = '\x1b';
        reply[length++] = '\\';
        handler(reply[..length]);
    }

    // Writes the hex-encoded capability value into destination. Returns the
    // length, or -1 for unknown keys / malformed requests (no reply).
    private int GetCapabilityValueHex(ReadOnlySpan<char> requestHex, Span<char> destination)
    {
        if (requestHex.IsEmpty || (requestHex.Length & 1) != 0)
            return -1;
        Span<char> key = stackalloc char[32];
        if (requestHex.Length / 2 > key.Length)
            return -1;
        for (int i = 0; i < requestHex.Length / 2; i++)
        {
            int hi = HexNibble(requestHex[2 * i]);
            int lo = HexNibble(requestHex[2 * i + 1]);
            if (hi < 0 || lo < 0)
                return -1;
            key[i] = (char)((hi << 4) | lo);
        }
        ReadOnlySpan<char> decoded = key[..(requestHex.Length / 2)];
        ReadOnlySpan<char> value = decoded switch
        {
            _ when decoded.SequenceEqual("TN") => "dotty",
            _ when decoded.SequenceEqual("Co") => "256",
            _ when decoded.SequenceEqual("RGB") => "8/8/8",
            _ => ReadOnlySpan<char>.Empty,
        };
        bool isKittyKeyboard = decoded.SequenceEqual("kitty-keyboard");
        if (value.IsEmpty && !isKittyKeyboard)
            return -1;
        if (isKittyKeyboard)
        {
            if (!KittyKeyboardFlags.TryFormat(destination, out int written))
                return -1;
            return written;
        }
        const string digits = "0123456789ABCDEF";
        if (destination.Length < value.Length * 2)
            return -1;
        for (int i = 0; i < value.Length; i++)
        {
            destination[2 * i] = digits[(value[i] >> 4) & 0xF];
            destination[2 * i + 1] = digits[value[i] & 0xF];
        }
        return value.Length * 2;
    }

    public void OnSetModifyOtherKeys(int level)
    {
        ModifyOtherKeysLevel = level is >= 0 and <= 2 ? level : ModifyOtherKeysLevel;
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

    /// <summary>
    /// Longest a CSI 2026 hold may withhold presentation, measured from the
    /// BEGIN that opened it. A repeated BEGIN does not extend it, so a producer
    /// that never sends END (or re-sends BEGIN continuously) cannot freeze the
    /// screen. Matches Ghostty's 1 s failsafe with kitty's non-renewing deadline.
    /// </summary>
    public const int SynchronizedUpdateMaxHoldMs = 1000;

    private readonly TimeProvider _timeProvider;
    private bool _renderDirty;
    private volatile bool _synchronizedUpdateActive;
    private long _synchronizedUpdateStartedTimestamp;

    public void OnSetSynchronizedUpdate(bool enabled)
    {
        if (_synchronizedUpdateActive == enabled)
            return;

        // Publish the start before the flag so a reader that observes the
        // flag never pairs it with a previous hold's start.
        if (enabled)
            Volatile.Write(ref _synchronizedUpdateStartedTimestamp, _timeProvider.GetTimestamp());
        _synchronizedUpdateActive = enabled;
        if (!enabled)
            FlushRender();
    }

    public bool SynchronizedUpdateActive => _synchronizedUpdateActive;

    /// <summary>
    /// True while a CSI 2026 hold is active and younger than
    /// <see cref="SynchronizedUpdateMaxHoldMs"/>.
    /// </summary>
    public bool SynchronizedUpdateHolding =>
        _synchronizedUpdateActive &&
        _timeProvider.GetElapsedTime(Volatile.Read(ref _synchronizedUpdateStartedTimestamp)).TotalMilliseconds
            < SynchronizedUpdateMaxHoldMs;

    private bool _focusReportingEnabled;

    public void OnSetFocusReporting(bool enabled)
    {
        _focusReportingEnabled = enabled;
    }

    public bool FocusReportingEnabled => _focusReportingEnabled;


    public void FlushRender()
    {
        if (SynchronizedUpdateHolding) return;
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
    public void Dispose() => _buffer.Dispose();
}
