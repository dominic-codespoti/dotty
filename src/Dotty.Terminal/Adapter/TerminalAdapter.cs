using System;
using Dotty.Abstractions.Adapter;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Adapter that connects the parser callbacks to a TerminalBuffer and exposes a render event.
/// Keeps responsibilities minimal: buffer management and render notification.
/// </summary>
public class TerminalAdapter : ITerminalHandler
{
    private readonly TerminalBuffer _buffer;
    private CellAttributes _currentAttributes = CellAttributes.Default;
    private CellAttributes _savedAttributes = CellAttributes.Default;
    private bool _hasSavedAttributes;
    private string? _windowTitle;
    private char _lastPrintedChar;

    public TerminalAdapter(int rows = 24, int columns = 80)
    {
        _buffer = new TerminalBuffer(rows, columns);
    }

    public event Action<string>? RenderRequested;
    public event Action<string>? ReplyRequested;
    public TerminalBuffer Buffer => _buffer;
    object? ITerminalHandler.Buffer => _buffer;

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
        // Track last printed character for REP support
        if (!text.IsEmpty)
        {
            _lastPrintedChar = text[text.Length - 1];
        }
        RequestRender();
    }

    public void OnOperatingSystemCommand(ReadOnlySpan<char> payload)
    {
        // Payloads are typically of the form "0;title" or "2;title".
        if (payload.IsEmpty) return;

        int semi = payload.IndexOf(';');
        if (semi <= 0) return;

        var codePart = payload.Slice(0, semi);
        if (int.TryParse(codePart, out var code) && (code == 0 || code == 2))
        {
            _windowTitle = payload.Slice(semi + 1).ToString();
            RequestRender();
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
        // CPR: delegate to the DSR code=6 handler which already implements this.
        OnDeviceStatusReport(6);
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

    public void OnInsertLines(int n)
    {
        _buffer.InsertLines(n);
        RequestRender();
    }

    public void OnDeleteLines(int n)
    {
        _buffer.DeleteLines(n);
        RequestRender();
    }

    public void OnClearScreen()
    {
        _buffer.EraseDisplay(2);
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
        RequestRender();
    }

    public void OnSetGraphicsRendition(ReadOnlySpan<char> parameters)
    {
        _currentAttributes = SgrParser.Apply(parameters, _currentAttributes);
    }

    public void OnMoveCursor(int row, int col)
    {
        _buffer.SetCursor(Math.Max(0, row - 1), Math.Max(0, col - 1));
        RequestRender();
    }

    public void OnCursorUp(int n)
    {
        _buffer.MoveCursorBy(-Math.Max(1, n), 0);
        RequestRender();
    }

    public void OnCursorDown(int n)
    {
        _buffer.MoveCursorBy(Math.Max(1, n), 0);
        RequestRender();
    }

    public void OnCursorForward(int n)
    {
        _buffer.MoveCursorBy(0, Math.Max(1, n));
        RequestRender();
    }

    public void OnCursorBack(int n)
    {
        _buffer.MoveCursorBy(0, -Math.Max(1, n));
        RequestRender();
    }

    public void OnEraseLine(int mode)
    {
        _buffer.EraseLine(mode);
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
        RequestRender();
    }

    public void OnSetAlternateScreen(bool enabled)
    {
        _buffer.SetAlternateScreen(enabled);
        RequestRender();
    }

    public void OnSetOriginMode(bool enabled)
    {
        _buffer.SetOriginMode(enabled);
        RequestRender();
    }

    public void OnSetScrollRegion(int top1Based, int bottom1Based)
    {
        // If bottom omitted (0), treat as full screen bottom
        if (bottom1Based == 0) bottom1Based = _buffer.Rows;
        _buffer.SetScrollRegion(top1Based, bottom1Based);
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
        _buffer.SetCursor(_buffer.CursorRow, Math.Max(0, col - 1));
        RequestRender();
    }

    public void OnCursorVerticalAbsolute(int row)
    {
        // VPA - CSI n d - move cursor to row n (1-based)
        _buffer.SetCursor(Math.Max(0, row - 1), _buffer.CursorCol);
        RequestRender();
    }

    public void OnCursorNextLine(int n)
    {
        // CNL - CSI n E - move cursor down n lines, to column 1
        _buffer.MoveCursorBy(Math.Max(1, n), 0);
        _buffer.SetCursor(_buffer.CursorRow, 0);
        RequestRender();
    }

    public void OnCursorPreviousLine(int n)
    {
        // CPL - CSI n F - move cursor up n lines, to column 1
        _buffer.MoveCursorBy(-Math.Max(1, n), 0);
        _buffer.SetCursor(_buffer.CursorRow, 0);
        RequestRender();
    }

    public void OnScrollUp(int n)
    {
        // SU - CSI n S - scroll up n lines within scroll region
        _buffer.ScrollUpLines(Math.Max(1, n));
        RequestRender();
    }

    public void OnScrollDown(int n)
    {
        // SD - CSI n T - scroll down n lines within scroll region
        _buffer.ScrollDownLines(Math.Max(1, n));
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
        _lastPrintedChar = '\0';
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
        _buffer.SetCursor(_buffer.CursorRow, nextStop);
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
        _buffer.SetCursor(_buffer.CursorRow, col);
        RequestRender();
    }

    public void OnSetCursorShape(int shape)
    {
        // TODO: track cursor shape if needed
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
    }

    private bool _renderDirty;

    public void FlushRender()
    {
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
