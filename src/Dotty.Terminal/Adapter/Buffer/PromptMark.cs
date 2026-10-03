using System;

namespace Dotty.Terminal.Adapter;

public enum PromptKind
{
    Prompt,
    Command,
    Output,
    CommandEnd
}

public readonly struct PromptMark : IComparable<PromptMark>
{
    public readonly int AbsoluteRow;
    public readonly int AbsoluteColumn;
    public readonly PromptKind Kind;

    public PromptMark(int absoluteRow, PromptKind kind, int absoluteColumn = 0)
    {
        AbsoluteRow = absoluteRow;
        AbsoluteColumn = absoluteColumn;
        Kind = kind;
    }

    public int CompareTo(PromptMark other) => AbsoluteRow.CompareTo(other.AbsoluteRow);
}
