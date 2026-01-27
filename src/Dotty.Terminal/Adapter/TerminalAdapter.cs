using System;
using System.Threading;
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
    private static int s_printSeq = 0;
    private CellAttributes _savedAttributes = CellAttributes.Default;
    private bool _hasSavedAttributes = false;
    private string? _windowTitle;

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
        // (adapter debug logging removed)

        // Debug logging removed.
        _buffer.WriteText(text, _currentAttributes);
        RequestRender();
    }

    public void OnOperatingSystemCommand(ReadOnlySpan<char> payload)
    {
        try
        {
            // Payloads are typically of the form "0;title" or "2;title".
            var s = payload.ToString();
            if (string.IsNullOrEmpty(s)) return;
            int semi = s.IndexOf(';');
            if (semi <= 0) return;
            var codePart = s.Substring(0, semi);
            var rest = s.Substring(semi + 1);
            if (int.TryParse(codePart, out var code))
            {
                if (code == 0 || code == 2)
                {
                    _windowTitle = rest;
                    RequestRender();
                }
            }
        }
        catch { }
    }

    public void OnSaveCursor()
    {
        try
        {
            _buffer.SaveCursor();
            _savedAttributes = _currentAttributes;
            _hasSavedAttributes = true;
        }
        catch { }
    }

    public void OnRestoreCursor()
    {
        try
        {
            _buffer.RestoreCursor();
            if (_hasSavedAttributes)
            {
                _currentAttributes = _savedAttributes;
                _hasSavedAttributes = false;
            }
            RequestRender();
        }
        catch { }
    }

    public void OnSetAutoWrap(bool enabled)
    {
        try { _buffer.SetAutoWrap(enabled); } catch { }
    }

    public void OnSetTabStop()
    {
        try { _buffer.SetTabStopAt(_buffer.CursorCol); } catch { }
    }

    public void OnClearTabStop()
    {
        try { _buffer.ClearTabStopAt(_buffer.CursorCol); } catch { }
    }

    public void OnClearAllTabStops()
    {
        try { _buffer.ClearAllTabStops(); } catch { }
    }

    public void OnReverseIndex()
    {
        try
        {
            _buffer.ReverseIndex();
            RequestRender();
        }
        catch { }
    }

    public void OnSetBracketedPasteMode(bool enabled)
    {
        try
        {
            _buffer.SetBracketedPasteMode(enabled);
        }
        catch { }
    }

    public void OnDeviceStatusReport(int code)
    {
        try
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
        catch { }
    }

    public void OnCursorPositionReport()
    {
        // CPR: caller expects a response with current cursor position.
        // We don't have a direct way to write to the device from here
        // (the parser/IO layer should implement the reply), so expose a
        // RenderRequested callback that consumers can use to query state.
        try
        {
            // Reply with CSI {row+1};{col+1}R
            var r = _buffer.CursorRow + 1;
            var c = _buffer.CursorCol + 1;
            var resp = $"\u001b[{r};{c}R";
            ReplyRequested?.Invoke(resp);
        }
        catch { }
    }

    public void OnInsertChars(int n)
    {
        try
        {
            _buffer.InsertChars(n);
            RequestRender();
        }
        catch { }
    }

    public void OnDeleteChars(int n)
    {
        try
        {
            _buffer.DeleteChars(n);
            RequestRender();
        }
        catch { }
    }

    public void OnInsertLines(int n)
    {
        try
        {
            _buffer.InsertLines(n);
            RequestRender();
        }
        catch { }
    }

    public void OnDeleteLines(int n)
    {
        try
        {
            _buffer.DeleteLines(n);
            RequestRender();
        }
        catch { }
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
        try
        {
            _buffer.SetOriginMode(enabled);
            RequestRender();
        }
        catch { }
    }

    public void OnSetScrollRegion(int top1Based, int bottom1Based)
    {
        try
        {
            // If bottom omitted (0), treat as full screen bottom
            if (bottom1Based == 0) bottom1Based = _buffer.Rows;
            _buffer.SetScrollRegion(top1Based, bottom1Based);
            RequestRender();
        }
        catch { }
    }

    public void OnSetCursorVisibility(bool visible)
    {
        _buffer.SetCursorVisible(visible);
        RequestRender();
    }

    public void OnBell()
    {
    }

    private void RequestRender()
    {
        // Bump a lightweight sequence counter for diagnostic ordering of
        // render events. Use Interlocked to be safe if adapter is used from
        // multiple threads.
        try
        {
            System.Threading.Interlocked.Increment(ref s_printSeq);
        }
        catch { }

        RenderRequested?.Invoke(_buffer.GetCurrentDisplay(showCursor: _buffer.CursorVisible, promptPrefix: null));
    }

    public void RequestRenderExtern()
    {
        RequestRender();
    }
}
