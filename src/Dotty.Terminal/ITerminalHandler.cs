using System;

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

        // Called when the parser detects a clear-screen (CSI 2 J + cursor home or equivalent).
        void OnClearScreen();

        // Called when the parser detects a clear-scrollback (CSI 3 J).
        void OnClearScrollback();

        // Called for SGR (color/attribute) sequences. Argument is the raw parameter string, like "31;1".
        void OnSetGraphicsRendition(ReadOnlySpan<char> parameters);

        // Bell
        void OnBell();
    }
}
