namespace Dotty.Terminal.Adapter;

/// <summary>
/// Represents a single search match position in the terminal buffer.
/// </summary>
public readonly struct SearchMatch : IEquatable<SearchMatch>
{
    public static readonly SearchMatch Empty = new SearchMatch(-1, -1, 0);

    /// <summary>Row where the match starts (can be negative for scrollback).</summary>
    public readonly int Row;

    /// <summary>Column where the match starts.</summary>
    public readonly int StartColumn;

    /// <summary>Length of the match in characters.</summary>
    public readonly int Length;

    /// <summary>End column (exclusive).</summary>
    public int EndColumn => StartColumn + Length;

    public SearchMatch(int row, int startColumn, int length)
    {
        Row = row;
        StartColumn = startColumn;
        Length = length;
    }

    public bool IsEmpty => Row < 0;

    public bool Equals(SearchMatch other) =>
        Row == other.Row && StartColumn == other.StartColumn && Length == other.Length;

    public override bool Equals(object? obj) => obj is SearchMatch other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Row, StartColumn, Length);

    public static bool operator ==(SearchMatch left, SearchMatch right) => left.Equals(right);
    public static bool operator !=(SearchMatch left, SearchMatch right) => !left.Equals(right);

    public override string ToString() => $"Match at ({Row}, {StartColumn}-{EndColumn})";
}
