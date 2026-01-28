using System;

namespace Dotty.App.Controls.Canvas;

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

    public bool IsEmpty => StartRow < 0 || StartColumn < 0 || EndRow < 0 || EndColumn < 0;

    public static TerminalSelectionRange From(int aRow, int aColumn, int bRow, int bColumn)
    {
        if (aRow < bRow || (aRow == bRow && aColumn <= bColumn))
        {
            return new TerminalSelectionRange(aRow, aColumn, bRow, bColumn);
        }

        return new TerminalSelectionRange(bRow, bColumn, aRow, aColumn);
    }

    public bool Equals(TerminalSelectionRange other) =>
        StartRow == other.StartRow && StartColumn == other.StartColumn &&
        EndRow == other.EndRow && EndColumn == other.EndColumn;

    public override bool Equals(object? obj) => obj is TerminalSelectionRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(StartRow, StartColumn, EndRow, EndColumn);

    public static bool operator ==(TerminalSelectionRange left, TerminalSelectionRange right) => left.Equals(right);
    public static bool operator !=(TerminalSelectionRange left, TerminalSelectionRange right) => !left.Equals(right);
}
