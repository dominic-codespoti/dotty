namespace Dotty.Abstractions.Config;

/// <summary>
/// Cursor shapes supported by the terminal.
/// </summary>
public enum TerminalCursorShape
{
    /// <summary>Full cell block cursor.</summary>
    Block = 0,

    /// <summary>Vertical bar (I-beam) cursor.</summary>
    Beam = 1,

    /// <summary>Horizontal line at the bottom of the cell.</summary>
    Underline = 2,
}
