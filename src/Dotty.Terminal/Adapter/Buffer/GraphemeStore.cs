using System;
using System.Collections.Generic;
using System.Threading;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Append-only, de-duplicating table of multi-codepoint grapheme clusters addressed by the <c>short</c>
/// index kept in <see cref="ColdCell.GraphemeIndex"/>. Index 0 is reserved (empty); valid indices are
/// <c>1..maxEntries</c> and never exceed <see cref="short.MaxValue"/>, so an index can neither go negative nor
/// alias another entry. When the table is full <see cref="Store"/> returns <c>-1</c> and callers degrade the
/// cell to its base code point. Entries are never evicted: a cell (live or in scrollback) may still hold
/// any index, so eviction needs the owner of those cells to cooperate.
/// </summary>
internal sealed class GraphemeStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, short> _index = new(StringComparer.Ordinal);
    private readonly int _maxIndex;
    private string?[] _entries;
    private int _next = 1;

    public GraphemeStore(int maxEntries = short.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEntries, (int)short.MaxValue);
        _maxIndex = maxEntries;
        _entries = new string?[Math.Min(256, maxEntries + 1)];
        _entries[0] = string.Empty;
    }

    /// <summary>Number of distinct clusters stored (the reserved slot 0 is not counted).</summary>
    public int Count => Volatile.Read(ref _next) - 1;

    /// <summary>
    /// Returns the index of <paramref name="value"/>, storing it first if it is new. Returns <c>-1</c> for an
    /// empty value or when the table is full and the value is not already present. Allocates only when a new
    /// entry is added.
    /// </summary>
    public short Store(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return -1;
        lock (_lock)
        {
            if (_index.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(value, out short existing)) return existing;
            if (_next > _maxIndex) return -1;

            string?[] entries = Grow();
            string text = value.ToString();
            int index = _next;
            entries[index] = text;
            _index.Add(text, (short)index);
            Volatile.Write(ref _next, index + 1);
            return (short)index;
        }
    }

    /// <summary>
    /// Returns the cluster stored at <paramref name="index"/>, or <c>null</c> when none is. Lock-free: the
    /// backing array is only ever replaced by a larger copy, and an index is handed out only after its
    /// entry is written.
    /// </summary>
    public string? Get(int index)
    {
        string?[] entries = Volatile.Read(ref _entries);
        return (uint)index < (uint)entries.Length ? entries[index] : null;
    }

    private string?[] Grow()
    {
        string?[] entries = _entries;
        if (_next < entries.Length) return entries;
        string?[] bigger = new string?[Math.Min(entries.Length * 2, _maxIndex + 1)];
        Array.Copy(entries, bigger, entries.Length);
        Volatile.Write(ref _entries, bigger);
        return bigger;
    }
}
