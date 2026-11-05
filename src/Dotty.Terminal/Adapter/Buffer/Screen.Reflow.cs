using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    internal sealed class LogicalLine
    {
        internal LogicalLine(int identity) => Identity = identity;
        internal int Identity { get; private set; }
        internal List<ReflowCell> Cells { get; } = new();

        // Where the line landed in the destination, recorded by EmitLogicalLine: the output index of its first
        // row and how many consecutive output rows it occupies. Anchor resolution reads these instead of a
        // per-cell position table.
        internal int FirstOutputRow;
        internal int OutputRowCount;

        internal void Reset(int identity)
        {
            Identity = identity;
            Cells.Clear();
            FirstOutputRow = 0;
            OutputRowCount = 0;
        }
    }

    internal sealed class SourceRow
    {
        internal int LogicalLine = -1;
        internal int UnitStart;
        internal int PhysicalRow = -1;

        internal void Reset()
        {
            LogicalLine = -1;
            UnitStart = 0;
            PhysicalRow = -1;
        }
    }

    internal sealed class SourceLayout
    {
        private readonly List<LogicalLine> _linePool = new();
        private readonly List<SourceRow> _rowPool = new();
        private int _linesUsed;
        private int _rowsUsed;

        internal List<LogicalLine> Lines { get; } = new();
        internal List<SourceRow> Rows { get; } = new();

        internal void Begin()
        {
            Lines.Clear();
            Rows.Clear();
            _linesUsed = 0;
            _rowsUsed = 0;
        }

        internal LogicalLine AddLine()
        {
            LogicalLine line;
            if (_linesUsed < _linePool.Count)
            {
                line = _linePool[_linesUsed];
                line.Reset(_linesUsed);
            }
            else
            {
                line = new LogicalLine(_linesUsed);
                _linePool.Add(line);
            }

            _linesUsed++;
            Lines.Add(line);
            return line;
        }

        internal SourceRow AddRow()
        {
            SourceRow row;
            if (_rowsUsed < _rowPool.Count)
            {
                row = _rowPool[_rowsUsed];
                row.Reset();
            }
            else
            {
                row = new SourceRow();
                _rowPool.Add(row);
            }

            _rowsUsed++;
            Rows.Add(row);
            return row;
        }
    }


    internal sealed class EmittedRow
    {
        public bool ContinuesPrevious { get; private set; }

        /// <summary>Offset, within its logical line, of the first cell this row holds.</summary>
        public int LineOffset { get; private set; }
        public List<ReflowCell> Cells { get; } = new();

        public void Reset(bool continuesPrevious, int lineOffset)
        {
            ContinuesPrevious = continuesPrevious;
            LineOffset = lineOffset;
            Cells.Clear();
        }
    }

    internal sealed class ReflowWorkspace
    {
        private readonly List<EmittedRow> _emittedPool = new();
        private int _emittedUsed;

        internal List<EmittedRow> Emitted { get; } = new();

        internal void BeginEmission(
            int rows,
            int columns,
            SourceLayout layout)
        {
            Emitted.Clear();
            _emittedUsed = 0;

            long emittedRows = 0;
            foreach (var line in layout.Lines)
            {
                if (line.Cells.Count == 0)
                {
                    emittedRows++;
                    continue;
                }

                int lineRows = 1;
                int usedColumns = 0;
                foreach (var cell in line.Cells)
                {
                    int width = cell.Width == 2 && columns >= 2 ? 2 : 1;
                    if (usedColumns + width > columns)
                    {
                        lineRows++;
                        usedColumns = 0;
                    }
                    usedColumns += width;
                }
                emittedRows += lineRows;
            }

            int requiredRows = emittedRows > int.MaxValue
                ? int.MaxValue
                : Math.Max(rows, (int)emittedRows);
            EnsureEmittedCapacity(requiredRows);
        }

        private void EnsureEmittedCapacity(int requiredRows)
        {
            _emittedPool.EnsureCapacity(requiredRows);
            Emitted.EnsureCapacity(requiredRows);
            while (_emittedPool.Count < requiredRows)
                _emittedPool.Add(new EmittedRow());
        }


        internal EmittedRow AddEmittedRow(bool continuesPrevious, int lineOffset = 0)
        {
            EmittedRow row;
            if (_emittedUsed < _emittedPool.Count)
            {
                row = _emittedPool[_emittedUsed];
                row.Reset(continuesPrevious, lineOffset);
            }
            else
            {
                row = new EmittedRow();
                row.Reset(continuesPrevious, lineOffset);
                _emittedPool.Add(row);
            }

            _emittedUsed++;
            Emitted.Add(row);
            return row;
        }
    }

    internal SourceLayout BuildSourceLayout(int scrollbackRows, SourceLayout layout) =>
        BuildSourceLayoutCore(NormalizeScrollbackRows(scrollbackRows), layout);

    private SourceLayout BuildSourceLayoutCore(int retainedScrollbackRows, SourceLayout layout)
    {
        layout.Begin();

        LogicalLine? currentLine = null;
        SourceRow? previousSourceRow = null;

        for (int index = 0; index < retainedScrollbackRows + Rows; index++)
        {
            int logicalRow = index - retainedScrollbackRows;
            int physicalRow = GetPhysicalRow(logicalRow);
            var sourceRow = layout.AddRow();
            sourceRow.PhysicalRow = physicalRow;
            bool continuesPrevious = RowContinuesPrevious[physicalRow];
            if (!continuesPrevious)
            {
                currentLine = layout.AddLine();
            }
            else if (currentLine is null || previousSourceRow is null)
            {
                // Metadata can be introduced after a pre-existing ring state;
                // an orphan continuation cannot be attached deterministically.
                previousSourceRow = sourceRow;
                continue;
            }

            sourceRow.LogicalLine = currentLine.Identity;
            sourceRow.UnitStart = currentLine.Cells.Count;
            int rowEnd = SourceRowEnd(physicalRow);
            for (int column = 0; column <= rowEnd; column++)
            {
                int width = SourceUnitWidth(physicalRow, column, rowEnd);
                if (width == 0)
                    continue; // a continuation cell belongs to the unit before it (an orphan one is dropped)

                var normalizedHot = UnsafeAsRef<CellHot>(_cellsPtr, physicalRow * Columns + column);
                normalizedHot.Width = (byte)width;
                var cold = UnsafeAsRef<ColdCell>(
                    _coldCellsPtr,
                    physicalRow * Columns + column);
                currentLine.Cells.Add(new ReflowCell(normalizedHot, cold, width));
            }

            previousSourceRow = sourceRow;
        }

        return layout;
    }

    // The raw reads of a source row's cells used to build the layout and to locate an anchor in it. Both go
    // through these two helpers, so a change of how historical rows are stored touches only them.

    /// <summary>Last column of the source row that takes part in a reflow, or -1 for a row without content.</summary>
    private int SourceRowEnd(int physicalRow)
    {
        int rowEnd = RowEndCol[physicalRow];
        if (rowEnd < 0)
            rowEnd = FindFallbackRowEnd(physicalRow);
        return rowEnd < 0 ? -1 : Math.Min(rowEnd, Columns - 1);
    }

    /// <summary>
    /// Width (1 or 2) of the layout unit that starts at <paramref name="column"/>, or 0 when the cell is a
    /// continuation cell and therefore not a unit of its own.
    /// </summary>
    private int SourceUnitWidth(int physicalRow, int column, int rowEnd)
    {
        int index = physicalRow * Columns + column;
        ref var hot = ref UnsafeAsRef<CellHot>(_cellsPtr, index);
        if (hot.IsContinuation)
            return 0;

        if (hot.Width > 1
            && column + 1 <= rowEnd
            && column + 1 < Columns
            && UnsafeAsRef<CellHot>(_cellsPtr, index + 1).IsContinuation)
        {
            return 2;
        }

        return 1;
    }


    internal ReflowCursorAnchor GetReflowAnchor(
        int logicalRow,
        int column,
        bool wrapPending,
        int scrollbackRows,
        SourceLayout layout)
    {
        int sb = NormalizeScrollbackRows(scrollbackRows);
        int rowIndex = sb + Math.Clamp(logicalRow, -sb, Rows - 1);
        if (rowIndex < 0 || rowIndex >= layout.Rows.Count)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        var row = layout.Rows[rowIndex];
        if (row.LogicalLine < 0)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        int col = Math.Clamp(column, 0, Columns - 1);

        // The unit offset is found by scanning the source row up to the column (O(columns), once per anchor)
        // instead of reading a per-unit table built for every row. Unit i of the row has line offset
        // UnitStart + i, because the layout appended the row's units consecutively.
        int rowEnd = SourceRowEnd(row.PhysicalRow);
        int offset = row.UnitStart;
        int unitCount = 0;
        for (int unitColumn = 0; unitColumn <= rowEnd; unitColumn++)
        {
            int width = SourceUnitWidth(row.PhysicalRow, unitColumn, rowEnd);
            if (width == 0)
                continue;

            int unitOffset = row.UnitStart + unitCount;
            unitCount++;
            if (col <= unitColumn)
                break;
            if (col < unitColumn + width)
            {
                offset = unitOffset;
                break;
            }
            offset = unitOffset + 1;
        }

        // A row without units (blank row) is addressed by column.
        if (unitCount == 0)
            offset = col;

        return new ReflowCursorAnchor(row.LogicalLine, offset, wrapPending);
    }



    /// <summary>
    /// Reflows this screen's <paramref name="layout"/> into a <paramref name="rows"/> x <paramref name="columns"/>
    /// screen whose ring keeps up to <paramref name="destinationScrollbackCapacity"/> history rows (the capacity may
    /// differ from this screen's: it follows the column count under a byte cap; when it is smaller than the history
    /// being carried over, the oldest rows are dropped). The anchors registered on <paramref name="mapping"/>
    /// beforehand (<see cref="ReflowMapping.AddAnchor"/>) are resolved against the result; nothing else is tracked
    /// per cell.
    /// </summary>
    internal Screen ReflowWithOptions(
        int rows,
        int columns,
        SourceLayout layout,
        ReflowWorkspace workspace,
        ReflowMapping mapping,
        int scrollbackRows,
        bool includeScrollback,
        int destinationScrollbackCapacity,
        Screen? destination = null)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        int retainedScrollbackRows = NormalizeScrollbackRows(scrollbackRows);
        mapping.Reset(rows, columns);
        workspace.BeginEmission(rows, columns, layout);
        var emitted = workspace.Emitted;

        foreach (var line in layout.Lines)
            EmitLogicalLine(line, columns, workspace);

        // Blank rows at the end of a viewport are padding, not scrollback.
        // Remove them before selecting the retained chronological stream so a
        // row-only resize cannot manufacture history from unused viewport rows.
        if (retainedScrollbackRows == 0)
        {
            while (emitted.Count > 1 && emitted[^1].Cells.Count == 0)
                emitted.RemoveAt(emitted.Count - 1);
        }

        while (emitted.Count < rows)
            workspace.AddEmittedRow(continuesPrevious: false);

        bool hasData = false;
        foreach (var emittedRow in emitted)
        {
            if (emittedRow.Cells.Count > 0)
            {
                hasData = true;
                break;
            }
        }

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
        ResolveAnchors(mapping, layout, emitted, Columns, columns);

        Screen resized;
        if (destination is null)
        {
            resized = new Screen(rows, columns, destinationScrollbackCapacity);
        }
        else
        {
            resized = destination;
            resized.ResetForReflow(rows, columns, destinationScrollbackCapacity);
        }
        // History is written at the end of the destination ring, outside the visible-window extent.
        resized.NoteReflowDestinationHistory(newScrollbackRows);

        for (int index = 0; index < retainedCount; index++)
        {
            int destinationPhysicalRow = index < newScrollbackRows
                ? resized.TotalRows - newScrollbackRows + index
                : index - newScrollbackRows;
            CopyEmittedRow(
                resized,
                destinationPhysicalRow,
                emitted[retainedStart + index],
                startsLogicalLine: index == 0);
        }

        return resized;
    }

    private int NormalizeScrollbackRows(int scrollbackRows) =>
        Math.Clamp(scrollbackRows < 0 ? _scrollbackCapacity : scrollbackRows, 0, _scrollbackCapacity);


    private int FindFallbackRowEnd(int physicalRow)
    {
        int offset = physicalRow * Columns;
        for (int column = Columns - 1; column >= 0; column--)
        {
            var hot = UnsafeAsRef<CellHot>(_cellsPtr, offset + column);
            var cold = UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + column);
            if (hot.Rune != 0 || hot.PackedFlags != 0 || hot.StyleId != 0
                || cold.HyperlinkId != 0 || cold.GraphemeIndex != -1)
            {
                return column;
            }
        }
        return -1;
    }

    private static void EmitLogicalLine(
        LogicalLine line,
        int columns,
        ReflowWorkspace workspace)
    {
        var emitted = workspace.Emitted;
        line.FirstOutputRow = emitted.Count;
        if (line.Cells.Count == 0)
        {
            workspace.AddEmittedRow(continuesPrevious: false);
            line.OutputRowCount = 1;
            return;
        }

        var row = workspace.AddEmittedRow(continuesPrevious: false);
        int usedColumns = 0;

        for (int offset = 0; offset < line.Cells.Count; offset++)
        {
            var cell = line.Cells[offset];
            int width = cell.Width == 2 && columns >= 2 ? 2 : 1;
            if (usedColumns + width > columns)
            {
                row = workspace.AddEmittedRow(continuesPrevious: true, offset);
                usedColumns = 0;
            }

            row.Cells.Add(cell with { Width = width });
            usedColumns += width;
        }

        line.OutputRowCount = emitted.Count - line.FirstOutputRow;
    }

    private static void ResolveAnchors(
        ReflowMapping mapping,
        SourceLayout layout,
        List<EmittedRow> emitted,
        int sourceColumns,
        int columns)
    {
        for (int slot = 0; slot < mapping.AnchorCount; slot++)
        {
            if (TryResolveAnchor(mapping, mapping.GetAnchor(slot), layout, emitted, sourceColumns, columns, out var position))
                mapping.SetPosition(slot, position);
        }
    }

    /// <summary>
    /// Where <paramref name="anchor"/> lands: the position of its (line, offset) cell in the emitted rows, found
    /// from the line's recorded output rows and the row cells, in O(log rows + columns) per anchor.
    /// </summary>
    /// <remarks>
    /// The result equals what the former dense table gave: the anchor's offset is first limited to the last cell
    /// of its line when a wrap is pending and to the line length, and when the output row of that offset was
    /// trimmed out of the retained window the nearest lower offset that is still inside it is used. Output rows
    /// only grow with the offset, so that nearest offset is the target itself when it is inside the window and
    /// nothing when its row precedes the window. A row of a line with cells never follows the window: only the
    /// blank rows of cell-less lines are trimmed from the end. A line without cells occupies a single output row and
    /// every offset below the source column count maps onto it by column.
    /// </remarks>
    private static bool TryResolveAnchor(
        ReflowMapping mapping,
        ReflowCursorAnchor anchor,
        SourceLayout layout,
        List<EmittedRow> emitted,
        int sourceColumns,
        int columns,
        out ReflowPosition position)
    {
        position = default;
        var lines = layout.Lines;
        if ((uint)anchor.LogicalLine >= (uint)lines.Count)
            return false;

        var line = lines[anchor.LogicalLine];
        int cellCount = line.Cells.Count;
        int length = cellCount == 0 ? sourceColumns : cellCount;
        int target = Math.Max(0, anchor.CellOffset);
        if (anchor.WrapPending && length > 0)
            target = Math.Min(target, length - 1);
        target = Math.Min(target, length);

        int windowStart = mapping.RetainedStart;
        int windowEnd = windowStart + mapping.NewScrollbackRows + mapping.NewRows;

        int outputIndex;
        int column;
        if (cellCount == 0)
        {
            outputIndex = line.FirstOutputRow;
            if (outputIndex < windowStart || outputIndex >= windowEnd)
                return false;

            column = Math.Min(Math.Min(target, sourceColumns - 1), columns - 1);
        }
        else
        {
            int firstRow = line.FirstOutputRow;
            int rowIndex = FindEmittedRowIndex(emitted, firstRow, line.OutputRowCount, target);
            outputIndex = firstRow + rowIndex;
            // The window ends at the last emitted row, and only blank rows (never a row of a line with cells) are
            // trimmed from the end, so a row at or after the window end cannot occur here.
            if (outputIndex < windowStart || outputIndex >= windowEnd)
                return false;

            var row = emitted[outputIndex];
            int used = 0;
            for (int i = 0, count = target - row.LineOffset; i < count; i++)
                used += row.Cells[i].Width;

            // The offset one past the last cell is the end of the line's last row (never beyond its last column).
            column = target == cellCount ? Math.Max(0, Math.Min(columns - 1, used)) : used;
        }

        int retainedIndex = outputIndex - windowStart;
        bool inScrollback = retainedIndex < mapping.NewScrollbackRows;
        position = new ReflowPosition(
            inScrollback ? -1 : retainedIndex - mapping.NewScrollbackRows,
            column,
            anchor.WrapPending,
            inScrollback,
            outputIndex);
        return true;
    }

    // Index (within the line's output rows) of the last row whose first cell offset is at most offset.
    private static int FindEmittedRowIndex(List<EmittedRow> emitted, int firstRow, int rowCount, int offset)
    {
        int low = 0;
        int high = rowCount - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (emitted[firstRow + middle].LineOffset <= offset)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }


    private static void CopyEmittedRow(
        Screen destination,
        int physicalRow,
        EmittedRow source,
        bool startsLogicalLine)
    {
        int columns = destination.Columns;
        int offset = physicalRow * columns;
        int usedColumns = 0;
        int rowMaxCol = -1;
        bool hasCold = false;

        foreach (var cell in source.Cells)
        {
            if (usedColumns >= columns)
                break;

            int width = cell.Width == 2 && columns - usedColumns >= 2 ? 2 : 1;
            var hot = cell.Hot;
            hot.Width = (byte)width;
            hot.IsContinuation = false;
            UnsafeAsRef<CellHot>(destination._cellsPtr, offset + usedColumns) = hot;
            UnsafeAsRef<ColdCell>(destination._coldCellsPtr, offset + usedColumns) = cell.Cold;
            if (cell.Cold.HyperlinkId != 0 || cell.Cold.GraphemeIndex != -1)
                hasCold = true;
            if (hot.Rune != 0 && hot.Rune != 32)
                rowMaxCol = usedColumns;

            if (width == 2)
            {
                var continuation = new CellHot
                {
                    StyleId = hot.StyleId,
                    IsContinuation = true,
                    HasHyperlink = hot.HasHyperlink,
                    HasGrapheme = hot.HasGrapheme,
                    IsProtected = hot.IsProtected,
                };
                UnsafeAsRef<CellHot>(destination._cellsPtr, offset + usedColumns + 1) = continuation;
                UnsafeAsRef<ColdCell>(destination._coldCellsPtr, offset + usedColumns + 1) = cell.Cold;
                if (cell.Cold.HyperlinkId != 0 || cell.Cold.GraphemeIndex != -1)
                    hasCold = true;
            }

            usedColumns += width;
        }

        destination.RowContinuesPrevious[physicalRow] =
            startsLogicalLine ? false : source.ContinuesPrevious;
        destination.RowEndCol[physicalRow] = usedColumns == 0 ? -1 : usedColumns - 1;
        destination.RowMaxCol[physicalRow] = rowMaxCol;
        destination.RowColdFlags[physicalRow] = hasCold;
    }
}
