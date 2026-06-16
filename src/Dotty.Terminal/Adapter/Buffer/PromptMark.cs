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
    public readonly PromptKind Kind;

    public PromptMark(int absoluteRow, PromptKind kind)
    {
        AbsoluteRow = absoluteRow;
        Kind = kind;
    }

    public int CompareTo(PromptMark other) => AbsoluteRow.CompareTo(other.AbsoluteRow);
}
