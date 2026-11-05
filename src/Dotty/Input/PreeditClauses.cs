using System;

namespace Dotty.Silk;

/// <summary>
/// Clause (IME block) boundaries of a preedit string as UTF-16 end offsets, with value equality so that
/// <see cref="ImePreeditState"/> snapshots compare by content. Default is "no clauses" (unconverted text).
/// </summary>
public readonly struct PreeditClauses : IEquatable<PreeditClauses>
{
    private readonly int[]? _ends;

    /// <param name="ends">Strictly increasing UTF-16 end offsets; the first clause starts at 0. Copied.</param>
    public PreeditClauses(ReadOnlySpan<int> ends)
    {
        for (int i = 0; i < ends.Length; i++)
        {
            if (ends[i] < (i == 0 ? 0 : ends[i - 1]))
                throw new ArgumentException("Clause ends must be non-decreasing and non-negative.", nameof(ends));
        }
        _ends = ends.IsEmpty ? null : ends.ToArray();
    }

    public int Count => _ends?.Length ?? 0;

    public int Start(int clause) => clause == 0 ? 0 : _ends![clause - 1];

    public int End(int clause) => _ends![clause];

    /// <summary>Clause containing the UTF-16 index, or -1 outside every clause (or when there are none).</summary>
    public int IndexAt(int utf16)
    {
        if (_ends is null || utf16 < 0)
            return -1;
        for (int i = 0; i < _ends.Length; i++)
        {
            if (utf16 < _ends[i])
                return i;
        }
        return -1;
    }

    public bool Equals(PreeditClauses other) =>
        (_ends ?? []).AsSpan().SequenceEqual(other._ends ?? []);

    public override bool Equals(object? obj) => obj is PreeditClauses other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (_ends is not null)
            foreach (int end in _ends)
                hash.Add(end);
        return hash.ToHashCode();
    }

    public static bool operator ==(PreeditClauses left, PreeditClauses right) => left.Equals(right);

    public static bool operator !=(PreeditClauses left, PreeditClauses right) => !left.Equals(right);
}
