using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

/// <summary>
/// Test-only copy of the dense reflow position mapper as it exists before the anchor-only rewrite (plan 08,
/// WI-R2): one dictionary entry per emitted cell plus one per logical line, keyed by
/// <c>(logical line, cell offset)</c>. It is the oracle the rewrite is checked against, so it MUST NOT be
/// "kept in sync" with production code: when production <c>ReflowMapping</c> changes, this file stays as the
/// historical behavior. Copied from <c>ReflowModels.cs</c> (<c>ReflowMapping</c>) at the base of the perf-bench
/// branch: only the type name differs, and the summary properties and <c>EnsureCapacity</c> that no consumer reads are omitted.
/// </summary>
internal sealed class LegacyDenseReflowMapping
{
    private readonly Dictionary<(int LogicalLine, int CellOffset), ReflowPosition> _positions = new();
    private readonly Dictionary<int, int> _lineLengths = new();
    public int NewScrollbackRows { get; internal set; }
    public int RetainedStart { get; internal set; }
    public int NewRows { get; internal set; }
    public int NewColumns { get; internal set; }

    internal void Reset(int rows, int columns)
    {
        _positions.Clear();
        _lineLengths.Clear();
        NewScrollbackRows = 0;
        RetainedStart = 0;
        NewRows = rows;
        NewColumns = columns;
    }

    internal void SetLineLength(int logicalLine, int length) => _lineLengths[logicalLine] = length;

    internal void Add(int logicalLine, int cellOffset, ReflowPosition position) =>
        _positions[(logicalLine, cellOffset)] = position;

    internal bool TryMap(ReflowCursorAnchor anchor, out ReflowPosition position)
    {
        int offset = Math.Max(0, anchor.CellOffset);
        if (anchor.WrapPending && _lineLengths.TryGetValue(anchor.LogicalLine, out int length) && length > 0)
            offset = Math.Min(offset, length - 1);

        if (TryMapOffset(anchor.LogicalLine, offset, anchor.WrapPending, out position))
            return true;

        if (!_lineLengths.TryGetValue(anchor.LogicalLine, out length))
        {
            position = default;
            return false;
        }

        for (int candidate = Math.Min(offset, length); candidate >= 0; candidate--)
        {
            if (TryMapOffset(anchor.LogicalLine, candidate, anchor.WrapPending, out position))
                return true;
        }

        position = default;
        return false;
    }

    private bool TryMapOffset(int logicalLine, int offset, bool wrapPending, out ReflowPosition position)
    {
        if (!_positions.TryGetValue((logicalLine, offset), out var stored))
        {
            position = default;
            return false;
        }

        int retainedIndex = stored.OutputIndex - RetainedStart;
        int retainedCount = NewScrollbackRows + NewRows;
        if (retainedIndex < 0 || retainedIndex >= retainedCount)
        {
            position = default;
            return false;
        }

        bool inScrollback = retainedIndex < NewScrollbackRows;
        int row = inScrollback ? -1 : retainedIndex - NewScrollbackRows;
        position = stored with
        {
            Row = row,
            WrapPending = wrapPending || stored.WrapPending,
            InScrollback = inScrollback,
        };
        return true;
    }
}

/// <summary>
/// Test-only replay of today's <c>Screen.ReflowWithOptions</c> / <c>EmitLogicalLine</c> bookkeeping that feeds a
/// <see cref="LegacyDenseReflowMapping"/>: the same <c>SetLineLength</c> / <c>Add</c> calls in the same order, the same
/// blank-tail trimming, padding and retained-window arithmetic, but no cell copying. Given a source
/// <see cref="Screen.SourceLayout"/> it yields the mapping the pre-rewrite implementation would have produced.
/// Like the mapper this is a frozen copy of the legacy behavior, not a mirror of future production code.
/// </summary>
internal static class LegacyDenseReflowReplay
{
    /// <param name="scrollbackCapacity">Capacity of the source ring: it bounds how many history rows the layout holds.</param>
    /// <param name="destinationScrollbackCapacity">
    /// Capacity of the destination ring, which sets the retained window. The pre-rewrite code used one capacity for both
    /// (every screen had the same); it differs only when the capacity follows the column count under a byte cap.
    /// </param>
    internal static LegacyDenseReflowMapping Replay(
        Screen.SourceLayout layout,
        int sourceColumns,
        int scrollbackCapacity,
        int destinationScrollbackCapacity,
        int rows,
        int columns,
        int scrollbackRows,
        bool includeScrollback)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        int retainedScrollbackRows = Math.Clamp(scrollbackRows < 0 ? scrollbackCapacity : scrollbackRows, 0, scrollbackCapacity);

        var mapping = new LegacyDenseReflowMapping();
        mapping.Reset(rows, columns);

        // Cells emitted per output row; the only per-row fact the retained-window arithmetic needs.
        var emitted = new List<int>();
        foreach (var line in layout.Lines)
        {
            int mappingLength = line.Cells.Count == 0 ? sourceColumns : line.Cells.Count;
            mapping.SetLineLength(line.Identity, mappingLength);

            if (line.Cells.Count == 0)
            {
                int emptyOutputIndex = emitted.Count;
                emitted.Add(0);
                for (int offset = 0; offset < sourceColumns; offset++)
                {
                    mapping.Add(
                        line.Identity,
                        offset,
                        new ReflowPosition(-1, Math.Min(offset, columns - 1), false, false, emptyOutputIndex));
                }
                continue;
            }

            emitted.Add(0);
            int outputIndex = emitted.Count - 1;
            int usedColumns = 0;
            for (int offset = 0; offset < line.Cells.Count; offset++)
            {
                int width = line.Cells[offset].Width == 2 && columns >= 2 ? 2 : 1;
                if (usedColumns + width > columns)
                {
                    emitted.Add(0);
                    outputIndex = emitted.Count - 1;
                    usedColumns = 0;
                }

                mapping.Add(line.Identity, offset, new ReflowPosition(-1, usedColumns, false, false, outputIndex));
                emitted[outputIndex]++;
                usedColumns += width;
            }

            int endColumn = Math.Max(0, Math.Min(columns - 1, usedColumns));
            mapping.Add(line.Identity, line.Cells.Count, new ReflowPosition(-1, endColumn, false, false, outputIndex));
        }

        if (retainedScrollbackRows == 0)
        {
            while (emitted.Count > 1 && emitted[^1] == 0)
                emitted.RemoveAt(emitted.Count - 1);
        }

        while (emitted.Count < rows)
            emitted.Add(0);

        bool hasData = emitted.Exists(cells => cells > 0);
        int targetTotal = (includeScrollback ? destinationScrollbackCapacity : 0) + rows;
        if (!hasData && retainedScrollbackRows == 0)
            targetTotal = rows;
        int retainedStart = Math.Max(0, emitted.Count - targetTotal);
        int retainedCount = emitted.Count - retainedStart;
        int newScrollbackRows = includeScrollback
            ? Math.Min(destinationScrollbackCapacity, Math.Max(0, retainedCount - rows))
            : 0;
        mapping.RetainedStart = retainedStart;
        mapping.NewScrollbackRows = newScrollbackRows;
        return mapping;
    }
}
