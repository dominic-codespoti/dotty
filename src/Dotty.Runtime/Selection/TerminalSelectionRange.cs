using System;

namespace Dotty.Runtime.Selection;

public readonly struct TerminalSelectionRange : IEquatable<TerminalSelectionRange>
{
    public static readonly TerminalSelectionRange Empty = new(-1, -1, -1, -1);

    public TerminalSelectionRange(int startRow, int startColumn, int endRow, int endColumn)
    {
        StartRow = startRow;
        StartColumn = startColumn;
        EndRow = endRow;
        EndColumn = endColumn;
    }

    public int StartRow { get; }
    public int StartColumn { get; }
    public int EndRow { get; }
    public int EndColumn { get; }

    public bool IsEmpty => StartRow == -1 && StartColumn == -1 && EndRow == -1 && EndColumn == -1;

    public static TerminalSelectionRange From(int r1, int c1, int r2, int c2)
    {
        if (r1 < r2 || (r1 == r2 && c1 <= c2))
        {
            return new TerminalSelectionRange(r1, c1, r2, c2);
        }

        return new TerminalSelectionRange(r2, c2, r1, c1);
    }

    public bool Contains(int row, int col)
    {
        if (IsEmpty)
        {
            return false;
        }

        if (row < StartRow || row > EndRow)
        {
            return false;
        }

        if (StartRow == EndRow)
        {
            return col >= StartColumn && col <= EndColumn;
        }

        if (row == StartRow)
        {
            return col >= StartColumn;
        }

        if (row == EndRow)
        {
            return col <= EndColumn;
        }

        return true;
    }

    public bool Equals(TerminalSelectionRange other) =>
        StartRow == other.StartRow && StartColumn == other.StartColumn &&
        EndRow == other.EndRow && EndColumn == other.EndColumn;

    public override bool Equals(object? obj) => obj is TerminalSelectionRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(StartRow, StartColumn, EndRow, EndColumn);

    public static bool operator ==(TerminalSelectionRange left, TerminalSelectionRange right) => left.Equals(right);
    public static bool operator !=(TerminalSelectionRange left, TerminalSelectionRange right) => !left.Equals(right);
}
