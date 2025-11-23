namespace Dotty.Terminal.Adapter
{
    /// <summary>
    /// Receiver interface called by the parser when terminal actions occur.
    /// Keep methods minimal and high-performance (use spans where appropriate).
    /// </summary>
    public interface ITerminalHandler
    {
        TerminalBuffer Buffer { get; }
        event Action<string>? RenderRequested;
        void RequestRenderExtern();
        void ResizeBuffer(int rows, int cols);

        void OnPrint(ReadOnlySpan<char> text);
        void OnEraseDisplay(int mode);
        void OnClearScrollback();
        void OnSetGraphicsRendition(ReadOnlySpan<char> parameters);
        void OnBell();
        void OnOperatingSystemCommand(ReadOnlySpan<char> payload);
        void OnMoveCursor(int row, int col); // 1-based
        void OnCursorUp(int n);
        void OnCursorDown(int n);
        void OnCursorForward(int n);
        void OnCursorBack(int n);
        void OnEraseLine(int mode); // 0=from cursor to end,1=from start to cursor,2=entire line
        void OnCarriageReturn();
        void OnLineFeed();
        void OnSetAlternateScreen(bool enabled);
        void OnSetCursorVisibility(bool visible);
    }
}
