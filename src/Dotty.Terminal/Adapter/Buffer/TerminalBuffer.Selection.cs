using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

/// <summary>Which end of a selection an anchor point is; decides where a point past a row's content lands after a reflow.</summary>
public enum SelectionAnchorEdge : byte
{
    /// <summary>Inclusive first cell of the selection (or of a gesture unit).</summary>
    Start,

    /// <summary>Inclusive last cell of the selection (or of a gesture unit).</summary>
    End,
}

/// <summary>One content-anchored position: an absolute row (the <c>_totalScrolled</c> numbering of prompt marks) and a column.</summary>
public struct SelectionAnchorPoint
{
    public long Row;
    public int Column;
    public SelectionAnchorEdge Edge;

    public SelectionAnchorPoint(long row, int column, SelectionAnchorEdge edge)
    {
        Row = row;
        Column = column;
        Edge = edge;
    }
}

/// <summary>
/// The anchor points of one selection, registered with <see cref="TerminalBuffer.SelectionAnchors"/> so that a resize
/// reflow re-bases them like prompt marks. <see cref="Epoch"/> is the buffer's <see cref="TerminalBuffer.CoordinateEpoch"/>
/// the points are expressed in; a later epoch means the numbering they refer to was discarded (CSI 3 J, ED 2 on the
/// main screen, RIS, alternate-screen switch) or a point did not survive a reflow. Every access holds <see cref="Gate"/>.
/// </summary>
public sealed class SelectionAnchorSet
{
    public const int Capacity = 4;

    /// <summary>Guards <see cref="Points"/>, <see cref="Count"/> and <see cref="Epoch"/>.</summary>
    public readonly object Gate = new();

    public readonly SelectionAnchorPoint[] Points = new SelectionAnchorPoint[Capacity];
    public int Count;
    public long Epoch = -1;

    /// <summary>Block selections keep their columns across a reflow; only the rows follow the content.</summary>
    public bool PreserveColumns;
}

// Coordinate space for content-anchored selections (plan-12 §5.6, option C). A "logical row" is the view-relative row
// used by the mouse and the composer (0 = top of the live screen, negative = history, -1 the newest history line); an
// "absolute row" is logicalRow + _totalScrolled and stays attached to its line while output scrolls.
public partial class TerminalBuffer
{
    private long _coordinateEpoch;
    private SelectionAnchorSet? _selectionAnchors;

    /// <summary>
    /// Incremented whenever the absolute row numbering stops describing the same content: CSI 3 J, ED 2 and RIS / clear
    /// screen on the main screen, and every alternate-screen switch. Ring-wrap eviction does not bump it (it is detected
    /// with <see cref="OldestRetainedAbsoluteRow"/>); a reflow re-bases the registered <see cref="SelectionAnchors"/> instead.
    /// </summary>
    public long CoordinateEpoch => Interlocked.Read(ref _coordinateEpoch);

    /// <summary>Absolute row of logical row 0 (the top of the live screen).</summary>
    public long ViewTopAbsoluteRow => Volatile.Read(ref _totalScrolled);

    /// <summary>Oldest absolute row the active screen still holds (history included); older rows were evicted.</summary>
    public long OldestRetainedAbsoluteRow => (long)Volatile.Read(ref _totalScrolled) - ScrollbackCount;

    public long ToAbsoluteRow(int logicalRow) => (long)Volatile.Read(ref _totalScrolled) + logicalRow;

    public int ToLogicalRow(long absoluteRow) =>
        (int)Math.Clamp(absoluteRow - Volatile.Read(ref _totalScrolled), int.MinValue, int.MaxValue);

    /// <summary>True when <paramref name="absoluteRow"/> is still held (not evicted, not below the screen bottom).</summary>
    public bool IsAbsoluteRowRetained(long absoluteRow)
    {
        long top = Volatile.Read(ref _totalScrolled);
        return absoluteRow >= top - ScrollbackCount && absoluteRow < top + Rows;
    }

    /// <summary>The selection whose points a resize re-bases; set by the owning selection service, null when none.</summary>
    public SelectionAnchorSet? SelectionAnchors
    {
        get => Volatile.Read(ref _selectionAnchors);
        set => Volatile.Write(ref _selectionAnchors, value);
    }

    private void BumpCoordinateEpoch() => Interlocked.Increment(ref _coordinateEpoch);

    /// <summary>Last content column of a logical row (history included), or -1 for a blank or missing row.</summary>
    public int GetLogicalRowEnd(int logicalRow)
    {
        if (logicalRow >= 0)
        {
            if (logicalRow >= Rows) return -1;
            int end = ActiveBuffer.GetRowEndCol(logicalRow);
            return end >= 0 ? end : ActiveBuffer.GetRowMaxCol(logicalRow);
        }
        return TryGetHistoryRow(logicalRow, out var hot, out _) ? hot.Length - 1 : -1;
    }

    /// <summary>True when a logical row (history included) is the soft-wrapped continuation of the row above it.</summary>
    public bool LogicalRowContinuesPrevious(int logicalRow)
    {
        if (logicalRow >= 0) return logicalRow < Rows && ActiveBuffer.GetRowContinuesPrevious(logicalRow);
        if (-logicalRow > ScrollbackCount) return false;
        return ActiveBuffer.RowContinuesPrevious[ActiveBuffer.GetPhysicalRow(logicalRow)];
    }

    /// <summary>
    /// Cells of a logical row (history included). Visible rows span all columns; history rows span their content only.
    /// The spans are valid until the next mutation, so callers hold <see cref="SyncRoot"/>.
    /// </summary>
    public bool TryGetLogicalRowCells(int logicalRow, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold)
    {
        if (logicalRow >= 0)
        {
            if (logicalRow < Rows)
            {
                hot = ActiveBuffer.GetRowCells(logicalRow);
                cold = ActiveBuffer.GetRowColdCells(logicalRow);
                return true;
            }
            hot = default;
            cold = default;
            return false;
        }
        return TryGetHistoryRow(logicalRow, out hot, out cold);
    }

    private bool TryGetHistoryRow(int logicalRow, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold)
    {
        if (logicalRow < 0 && -logicalRow <= ScrollbackCount)
            return ActiveBuffer.TryReadScrollbackRow(-logicalRow - 1, out hot, out cold);
        hot = default;
        cold = default;
        return false;
    }

    private readonly record struct SelectionAnchorSlot(int Index, int Slot, bool PastContent);
    private SelectionAnchorSlot[] _selectionSlotScratch = new SelectionAnchorSlot[SelectionAnchorSet.Capacity];

    /// <summary>
    /// Registers the points of <see cref="SelectionAnchors"/> on the active screen's mapping (next to the prompt marks).
    /// A point past its row's content registers on the last content cell and remembers that, so a reflow never moves it
    /// onto the following line. Returns the number of registered slots, or -1 when the set is stale (nothing to re-base).
    /// </summary>
    private int CaptureSelectionAnchors(
        SelectionAnchorSet? set, Screen screen, Screen.SourceLayout layout, ReflowMapping mapping, int scrollbackRows, int rows, int totalScrolled)
    {
        if (set is null) return -1;
        lock (set.Gate)
        {
            if (set.Count == 0 || set.Epoch != _coordinateEpoch) return -1;
            for (int i = 0; i < set.Count; i++)
            {
                ref var point = ref set.Points[i];
                long chronological = point.Row - totalScrolled + scrollbackRows;
                if (chronological < 0 || chronological >= scrollbackRows + rows)
                {
                    _selectionSlotScratch[i] = new SelectionAnchorSlot(i, -1, false);
                    continue;
                }
                int logicalRow = (int)(chronological - scrollbackRows);
                int rowEnd = GetLogicalRowEnd(logicalRow);
                bool past = !set.PreserveColumns && point.Column > rowEnd;
                int column = set.PreserveColumns ? 0 : past ? Math.Max(0, rowEnd) : point.Column;
                var anchor = screen.GetReflowAnchor(logicalRow, column, wrapPending: false, scrollbackRows, layout);
                _selectionSlotScratch[i] = new SelectionAnchorSlot(
                    i, mapping.AddAnchor(anchor), past && (rowEnd >= 0 || point.Edge == SelectionAnchorEdge.End));
            }
            return set.Count;
        }
    }

    /// <summary>
    /// Re-bases the points captured by <see cref="CaptureSelectionAnchors"/> to the new numbering (same arithmetic as
    /// <see cref="RebasePromptMarks"/>). A point that did not survive (trimmed history) makes the whole set stale.
    /// </summary>
    private void RebaseSelectionAnchors(SelectionAnchorSet? set, int captured, ReflowMapping mapping, int newRows, int newColumns)
    {
        if (set is null || captured < 0) return;
        lock (set.Gate)
        {
            for (int i = 0; i < captured; i++)
            {
                var entry = _selectionSlotScratch[i];
                if (entry.Slot < 0 || !mapping.TryMap(entry.Slot, out var position))
                {
                    set.Count = 0;
                    set.Epoch = -1;
                    return;
                }
                int retained = position.OutputIndex - mapping.RetainedStart;
                if (retained < 0 || retained >= mapping.NewScrollbackRows + newRows)
                {
                    set.Count = 0;
                    set.Epoch = -1;
                    return;
                }
                ref var point = ref set.Points[entry.Index];
                point.Row = retained;
                if (set.PreserveColumns)
                {
                    point.Column = Math.Clamp(point.Column, 0, Math.Max(0, newColumns - 1));
                    continue;
                }
                int column = position.Column;
                if (entry.PastContent)
                    column = point.Edge == SelectionAnchorEdge.End ? newColumns - 1 : column + 1;
                point.Column = Math.Clamp(column, 0, newColumns);
            }
        }
    }
}
