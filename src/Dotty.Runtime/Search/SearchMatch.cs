namespace Dotty.Runtime.Search;

/// <summary>
/// Represents a single search match in the terminal grid or scrollback buffer.
/// </summary>
/// <param name="Row">The row index (0-based visible row, or negative for scrollback).</param>
/// <param name="StartCol">The starting column index (0-based, inclusive).</param>
/// <param name="EndCol">The ending column index (0-based, exclusive).</param>
/// <param name="IsActive">Whether this match is the currently selected/active match.</param>
public readonly record struct SearchMatch(int Row, int StartCol, int EndCol, bool IsActive)
{
    /// <summary>Length of the match in columns.</summary>
    public int Length => Math.Max(0, EndCol - StartCol);

    /// <summary>An empty/invalid search match.</summary>
    public static readonly SearchMatch Empty = new(-1, -1, -1, false);

    /// <summary>Whether this search match is empty/invalid.</summary>
    public bool IsEmpty => Row < 0 && StartCol < 0 && EndCol < 0;
}
