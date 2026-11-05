using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// The (row, column, high-byte) indices a Unicode-placeholder cell carries as combining diacritics after
/// U+10EEEE. Each value is an index into the 297-entry diacritic table, or <see cref="Absent"/> when the
/// cell did not carry that mark (the cell then inherits from the placeholder to its left).
/// </summary>
public readonly record struct PlaceholderIndices(ushort Row, ushort Col, ushort Msb)
{
    /// <summary>Marker for a mark that was not present.</summary>
    public const ushort Absent = 0xFFFF;

    public static PlaceholderIndices None => new(Absent, Absent, Absent);

    public bool HasRow => Row != Absent;
    public bool HasCol => Col != Absent;
    public bool HasMsb => Msb != Absent;
}

/// <summary>
/// Bounded, interning table of <see cref="PlaceholderIndices"/> triples. A slot is referenced from a cell by the
/// negative code <c>(short)(-2 - slot)</c> kept in <see cref="Dotty.Terminal.Adapter.ColdCell.GraphemeIndex"/>;
/// slot 0 is always the all-absent triple. Reads are lock-free; adding a new triple takes a lock. When the
/// table is full a new triple degrades to the all-absent code (the cell then inherits everything), never to
/// another cell's data. Entries are never evicted, so a code held by any cell (including scrollback) stays valid.
/// </summary>
internal sealed class PlaceholderCodeTable
{
    /// <summary>Most slots a table may hold; the codes <c>-2 .. -32766</c> never reach <see cref="short.MinValue"/>.</summary>
    public const int MaxCapacity = 32765;

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<ulong, short> _codes = new();
    private readonly int _capacity;
    private ulong[] _entries = new ulong[64];
    private int _count = 1;

    public PlaceholderCodeTable(int capacity = MaxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        _capacity = capacity;
        _entries[0] = Pack(PlaceholderIndices.Absent, PlaceholderIndices.Absent, PlaceholderIndices.Absent);
    }

    /// <summary>The code of the all-absent triple (slot 0).</summary>
    public const short AbsentCode = -2;

    /// <summary>Number of slots in use, including the always-present all-absent slot.</summary>
    public int Count => Volatile.Read(ref _count);

    private static ulong Pack(ushort row, ushort col, ushort msb) => ((ulong)row << 32) | ((ulong)col << 16) | msb;

    private static ushort Sanitize(ushort value) => value < Diacritics.Count ? value : PlaceholderIndices.Absent;

    /// <summary>
    /// Returns the code for the triple, adding it if new. Values outside the diacritic table count as absent.
    /// Returns <see cref="AbsentCode"/> when the table is full and the triple is not already present.
    /// </summary>
    public short Intern(ushort rowIdx, ushort colIdx, ushort msbIdx)
    {
        ulong key = Pack(Sanitize(rowIdx), Sanitize(colIdx), Sanitize(msbIdx));
        if (_codes.TryGetValue(key, out short existing)) return existing;
        if (key == _entries[0]) return AbsentCode;

        lock (_lock)
        {
            if (_codes.TryGetValue(key, out existing)) return existing;
            int slot = _count;
            if (slot >= _capacity) return AbsentCode;

            ulong[] entries = _entries;
            if (slot >= entries.Length)
            {
                var bigger = new ulong[Math.Min(entries.Length * 2, _capacity)];
                Array.Copy(entries, bigger, entries.Length);
                Volatile.Write(ref _entries, bigger);
                entries = bigger;
            }

            entries[slot] = key;
            Volatile.Write(ref _count, slot + 1); // before the code is visible through _codes, so TryGet never rejects an issued code
            short code = (short)(-2 - slot);
            _codes[key] = code;
            return code;
        }
    }

    /// <summary>Looks up a code produced by <see cref="Intern"/>; false for -1, non-negative values, or unknown codes.</summary>
    public bool TryGet(short code, out PlaceholderIndices indices)
    {
        int slot = -2 - code;
        if (slot < 0 || slot >= Volatile.Read(ref _count))
        {
            indices = PlaceholderIndices.None;
            return false;
        }

        // _count is published after the entry is written and the array is only replaced by a larger copy.
        ulong key = Volatile.Read(ref _entries)[slot];
        indices = new PlaceholderIndices((ushort)(key >> 32), (ushort)(key >> 16), (ushort)key);
        return true;
    }
}

/// <summary>
/// Process-wide placeholder encoding used by the buffer writer and the placeholder resolver.
/// </summary>
public static class PlaceholderTable
{
    /// <summary>The Unicode private-use scalar that marks a placeholder cell.</summary>
    public const int PlaceholderRune = 0x10EEEE;

    /// <summary>Code of the all-absent triple.</summary>
    public const short AbsentCode = PlaceholderCodeTable.AbsentCode;

    private static readonly PlaceholderCodeTable s_shared = new();

    /// <summary>Number of slots in use in the process-wide table (diagnostics).</summary>
    internal static int Count => s_shared.Count;

    /// <summary>Interns a triple (0xFFFF = absent) in the process-wide table and returns its cell code.</summary>
    public static short Intern(ushort rowIdx, ushort colIdx, ushort msbIdx) => s_shared.Intern(rowIdx, colIdx, msbIdx);

    /// <summary>Resolves a cell code; false for -1 (no cold data) or a code that was never issued.</summary>
    public static bool TryGet(short code, out PlaceholderIndices indices) => s_shared.TryGet(code, out indices);

    /// <summary>True when <paramref name="cluster"/> begins with U+10EEEE (a surrogate pair).</summary>
    public static bool IsPlaceholderCluster(ReadOnlySpan<char> cluster) =>
        cluster.Length >= 2 && cluster[0] == '\uDBFB' && cluster[1] == '\uDEEE';

    /// <summary>
    /// Encodes the combining marks that follow U+10EEEE in a freshly written cluster into a cell code: the
    /// first three diacritics from the table become row, column and high byte; every other mark is dropped.
    /// Returns -1 (the "no cold data" value) when no diacritic is present.
    /// </summary>
    public static short Encode(ReadOnlySpan<char> marks) => Append(-1, marks);

    /// <summary>
    /// Adds <paramref name="marks"/> to the triple behind <paramref name="code"/> (-1 for none): diacritics fill
    /// the first absent of row, column, high byte in that order; non-diacritics and surplus diacritics are dropped.
    /// Returns <paramref name="code"/> unchanged when nothing was added.
    /// </summary>
    public static short Append(short code, ReadOnlySpan<char> marks)
    {
        PlaceholderIndices current = PlaceholderIndices.None;
        if (code != -1) s_shared.TryGet(code, out current);

        ushort row = current.Row, col = current.Col, msb = current.Msb;
        bool changed = false;
        while (!marks.IsEmpty && msb == PlaceholderIndices.Absent)
        {
            if (Rune.DecodeFromUtf16(marks, out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                marks = marks[Math.Max(consumed, 1)..];
                continue;
            }
            marks = marks[consumed..];

            int index = Diacritics.IndexOf(rune.Value);
            if (index < 0) continue;

            if (row == PlaceholderIndices.Absent) row = (ushort)index;
            else if (col == PlaceholderIndices.Absent) col = (ushort)index;
            else msb = (ushort)index;
            changed = true;
        }

        return changed ? s_shared.Intern(row, col, msb) : code;
    }
}
