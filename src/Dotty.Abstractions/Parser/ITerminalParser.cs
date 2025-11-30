using Dotty.Abstractions.Adapter;

namespace Dotty.Abstractions.Parser
{
    /// <summary>
    /// Minimal streaming ANSI/VT parser API.
    /// Feed bytes (typically from a PTY) and the parser will call the handler.
    /// </summary>
    public interface ITerminalParser
    {
        /// <summary>
        /// Feed a chunk of bytes to the parser. The parser may retain state between calls to handle
        /// split escape sequences. Implementations should avoid allocations and use spans where possible.
        /// </summary>
        void Feed(ReadOnlySpan<byte> bytes);

        /// <summary>
        /// Attach a handler that receives parsed terminal events.
        /// </summary>
        ITerminalHandler? Handler { get; set; }
    }
}
