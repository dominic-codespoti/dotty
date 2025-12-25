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

    public TerminalAdapter(int rows = 24, int columns = 80)
    {
        _buffer = new TerminalBuffer(rows, columns);
    }

    public event Action<string>? RenderRequested;
    public TerminalBuffer Buffer => _buffer;
    object? ITerminalHandler.Buffer => _buffer;

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
        try
        {
            var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_ADAPTER");
            if (!string.IsNullOrEmpty(dbg) && dbg != "0")
            {
                try
                {
                    var printable = AnsiUtilities.StripAnsi(text.ToString()).Replace("\n", "\\n");
                    Console.Error.WriteLine("[ADAPTER_PRINT] '" + printable + "'");
                }
                catch { }
            }
        }
        catch { }

        _buffer.WriteText(text, _currentAttributes);
        RequestRender();
    }

    public void OnOperatingSystemCommand(ReadOnlySpan<char> payload)
    {
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
        RenderRequested?.Invoke(_buffer.GetCurrentDisplay(showCursor: false, promptPrefix: null));
    }

    public void RequestRenderExtern()
    {
        RequestRender();
    }
}
