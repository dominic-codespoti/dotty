namespace Dotty.Terminal
{
    /// <summary>
    /// Receiver interface called by the parser when terminal actions occur.
    /// Keep methods minimal and high-performance (use spans where appropriate).
    /// </summary>
    public interface ITerminalHandler
    {
        // Called for printable text. The implementation should be fast and avoid allocations.
        void OnPrint(ReadOnlySpan<char> text);

    // Called when the parser detects an erase-display (CSI n J):
    // mode 0 = erase from cursor to end of screen
    // mode 1 = erase from start of screen to cursor
    // mode 2 = erase entire screen
    void OnEraseDisplay(int mode);

    // Called when the parser detects a clear-scrollback (CSI 3 J).
    void OnClearScrollback();

        // Called for SGR (color/attribute) sequences. Argument is the raw parameter string, like "31;1".
        void OnSetGraphicsRendition(ReadOnlySpan<char> parameters);

        // Bell
        void OnBell();

        // Operating System Command (OSC) payload (text inside ESC ] ... BEL or ESC \)
        // Implementations may choose to parse application-specific payloads.
        void OnOperatingSystemCommand(ReadOnlySpan<char> payload);

        // Cursor and screen control
        void OnMoveCursor(int row, int col); // 1-based
        void OnCursorUp(int n);
        void OnCursorDown(int n);
        void OnCursorForward(int n);
        void OnCursorBack(int n);
        void OnEraseLine(int mode); // 0=from cursor to end,1=from start to cursor,2=entire line
        void OnCarriageReturn();
        void OnLineFeed();

        // Alternate screen buffer (DECSCNM / ?1049)
        void OnSetAlternateScreen(bool enabled);

    // Show or hide the cursor (DEC ?25)
    void OnSetCursorVisibility(bool visible);
    }
}
