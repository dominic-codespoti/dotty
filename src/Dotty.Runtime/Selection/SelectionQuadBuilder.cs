using System;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Selection;

/// <summary>
/// Paints a pane's selection into its composed cell instances in O(instances + selected visible cells):
/// instances on selected cells get the selection background, and every selected visible cell that has no
/// instance gets an appended background-only instance. Allocation-free; the caller owns the coverage scratch.
/// </summary>
public static class SelectionQuadBuilder
{
    /// <summary>
    /// Selected columns of <paramref name="logicalRow"/> as an inclusive, unclipped interval (a full linear row is
    /// <c>[int.MinValue, int.MaxValue]</c>). Matches <see cref="TextSelectionService.IsCellSelected"/>: linear
    /// selections run from the start cell to the end cell through whole rows, block selections are rectangles.
    /// </summary>
    public static bool TryGetRowSpan(TerminalSelectionRange range, bool block, int logicalRow, out int first, out int last)
    {
        first = 0;
        last = -1;
        if (logicalRow < range.StartRow || logicalRow > range.EndRow)
            return false;
        if (!block && range.IsEmpty)
            return false;

        if (block || range.StartRow == range.EndRow)
        {
            first = range.StartColumn;
            last = range.EndColumn;
        }
        else
        {
            first = logicalRow == range.StartRow ? range.StartColumn : int.MinValue;
            last = logicalRow == range.EndRow ? range.EndColumn : int.MaxValue;
        }
        return first <= last;
    }

    /// <summary>
    /// Visible logical rows the selection touches in a pane of <paramref name="paneRows"/> rows scrolled back by
    /// <paramref name="scrollOffset"/>; returns the count (0 when none is visible).
    /// </summary>
    public static int GetVisibleRows(TerminalSelectionRange range, int paneRows, int scrollOffset, out int firstLogicalRow, out int lastLogicalRow)
    {
        firstLogicalRow = Math.Max(range.StartRow, -scrollOffset);
        lastLogicalRow = Math.Min(range.EndRow, paneRows - 1 - scrollOffset);
        return Math.Max(0, lastLogicalRow - firstLogicalRow + 1);
    }

    /// <summary>Coverage scratch length <see cref="Apply"/> needs.</summary>
    public static int CoverageLength(TerminalSelectionRange range, int paneRows, int paneColumns, int scrollOffset)
        => checked(GetVisibleRows(range, paneRows, scrollOffset, out _, out _) * Math.Max(0, paneColumns));

    /// <summary>
    /// Recolours the selected instances among <c>instances[..paneInstanceCount]</c> (global grid coordinates: a pane
    /// cell (view row, column) sits at (<paramref name="rowOffset"/> + row, <paramref name="columnOffset"/> + column))
    /// and appends one instance per uncovered selected visible cell at <c>instances[paneInstanceCount..]</c>, in row
    /// then column order. Returns the number appended. <paramref name="instances"/> must have room for
    /// <see cref="CoverageLength"/> appended instances; <paramref name="coverage"/> must be at least that long.
    /// </summary>
    public static int Apply(
        Span<CellInstance> instances,
        int paneInstanceCount,
        TerminalSelectionRange range,
        bool block,
        int paneRows,
        int paneColumns,
        int scrollOffset,
        int columnOffset,
        int rowOffset,
        SgrColorArgb selectionColor,
        byte selectionAlpha,
        SgrColorArgb foreground,
        Span<bool> coverage)
    {
        int visibleRows = GetVisibleRows(range, paneRows, scrollOffset, out int firstLogicalRow, out int lastLogicalRow);
        if (visibleRows == 0 || paneColumns <= 0)
            return 0;

        int coverageLength = checked(visibleRows * paneColumns);
        coverage = coverage[..coverageLength];
        coverage.Clear();
        int firstViewRow = firstLogicalRow + scrollOffset;

        // One pass over the pane's instances: recolour selected cells and remember which ones are covered.
        for (int i = 0; i < paneInstanceCount; i++)
        {
            ref var instance = ref instances[i];
            // Decoration sprites carry no background; tinting them would double the selection colour.
            if ((instance.Flags & CellFlags.Decoration) != 0)
                continue;
            int column = instance.Col - columnOffset;
            int viewRow = instance.Row - rowOffset;
            if (!TryGetRowSpan(range, block, viewRow - scrollOffset, out int first, out int last)
                || column < first || column > last)
                continue;

            instance.BgR = selectionColor.R;
            instance.BgG = selectionColor.G;
            instance.BgB = selectionColor.B;
            instance.BgA = selectionAlpha;
            int coverageRow = viewRow - firstViewRow;
            if ((uint)coverageRow < (uint)visibleRows && (uint)column < (uint)paneColumns)
                coverage[coverageRow * paneColumns + column] = true;
        }

        int appended = 0;
        for (int logicalRow = firstLogicalRow; logicalRow <= lastLogicalRow; logicalRow++)
        {
            if (!TryGetRowSpan(range, block, logicalRow, out int first, out int last))
                continue;

            int minColumn = Math.Max(0, first);
            int maxColumn = Math.Min(paneColumns - 1, last);
            int viewRow = logicalRow + scrollOffset;
            Span<bool> rowCoverage = coverage.Slice((viewRow - firstViewRow) * paneColumns, paneColumns);
            for (int column = minColumn; column <= maxColumn; column++)
            {
                if (rowCoverage[column])
                    continue;

                instances[paneInstanceCount + appended++] = new CellInstance
                {
                    Col = (ushort)(column + columnOffset),
                    Row = (ushort)(viewRow + rowOffset),
                    FgR = foreground.R,
                    FgG = foreground.G,
                    FgB = foreground.B,
                    FgA = 255,
                    BgR = selectionColor.R,
                    BgG = selectionColor.G,
                    BgB = selectionColor.B,
                    BgA = selectionAlpha,
                    Flags = 0
                };
            }
        }
        return appended;
    }
}
