using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    private sealed class LogicalLine
    {
        public LogicalLine(int identity) => Identity = identity;
        public int Identity { get; }
        public List<ReflowCell> Cells { get; } = new();
    }

    private sealed class SourceRow
    {
        public int LogicalLine = -1;
        public int UnitStart;
        public List<SourceUnit> Units { get; } = new();
    }

    private readonly record struct SourceUnit(int Column, int Width, int Offset);

    private sealed class SourceLayout
    {
        public List<LogicalLine> Lines { get; } = new();
        public List<SourceRow> Rows { get; } = new();
    }

    private sealed class EmittedRow
    {
        public EmittedRow(bool continuesPrevious) => ContinuesPrevious = continuesPrevious;
        public bool ContinuesPrevious { get; }
        public List<ReflowCell> Cells { get; } = new();
    }

    internal ReflowCursorAnchor GetReflowAnchor(
        int logicalRow,
        int column,
        bool wrapPending,
        int scrollbackRows = -1)
    {
        var layout = BuildSourceLayout(scrollbackRows);
        int sb = NormalizeScrollbackRows(scrollbackRows);
        int rowIndex = sb + Math.Clamp(logicalRow, -sb, Rows - 1);
        if (rowIndex < 0 || rowIndex >= layout.Rows.Count)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        var row = layout.Rows[rowIndex];
        if (row.LogicalLine < 0)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        int col = Math.Clamp(column, 0, Columns - 1);
        if (row.Units.Count == 0)
            return new ReflowCursorAnchor(row.LogicalLine, col, wrapPending);

        int offset = row.UnitStart;
        foreach (var unit in row.Units)
        {
            if (col <= unit.Column)
                break;
            if (col < unit.Column + unit.Width)
            {
                offset = unit.Offset;
                break;
            }
            offset = unit.Offset + 1;
        }

        return new ReflowCursorAnchor(row.LogicalLine, offset, wrapPending);
    }

    internal Screen Reflow(
        int rows,
        int columns,
        ReflowCursorAnchor anchor,
        out ReflowMapping mapping)
    {
        return ReflowWithOptions(
            rows,
            columns,
            anchor,
            out mapping,
            scrollbackRows: -1,
            includeScrollback: true);
    }

    internal Screen ReflowWithOptions(
        int rows,
        int columns,
        ReflowCursorAnchor anchor,
        out ReflowMapping mapping,
        int scrollbackRows,
        bool includeScrollback)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        int retainedScrollbackRows = NormalizeScrollbackRows(scrollbackRows);
        var layout = BuildSourceLayout(retainedScrollbackRows);
        var emitted = new List<EmittedRow>(layout.Lines.Count);
        mapping = new ReflowMapping
        {
            NewRows = rows,
            NewColumns = columns,
        };

        foreach (var line in layout.Lines)
        {
            int mappingLength = line.Cells.Count == 0 ? Columns : line.Cells.Count;
            mapping.SetLineLength(line.Identity, mappingLength);
            EmitLogicalLine(line, columns, Columns, emitted, mapping);
        }

        // Blank rows at the end of a viewport are padding, not scrollback.
        // Remove them before selecting the retained chronological stream so a
        // row-only resize cannot manufacture history from unused viewport rows.
        if (retainedScrollbackRows == 0)
        {
            while (emitted.Count > 1 && emitted[^1].Cells.Count == 0)
                emitted.RemoveAt(emitted.Count - 1);
        }

        while (emitted.Count < rows)
            emitted.Add(new EmittedRow(continuesPrevious: false));

        bool hasData = false;
        foreach (var emittedRow in emitted)
        {
            if (emittedRow.Cells.Count > 0)
            {
                hasData = true;
                break;
            }
        }

        int targetTotal = (includeScrollback ? _scrollbackCapacity : 0) + rows;
        if (!hasData && retainedScrollbackRows == 0)
            targetTotal = rows;
        int retainedStart = Math.Max(0, emitted.Count - targetTotal);
        int retainedCount = emitted.Count - retainedStart;
        int newScrollbackRows = includeScrollback
            ? Math.Min(_scrollbackCapacity, Math.Max(0, retainedCount - rows))
            : 0;
        mapping.RetainedStart = retainedStart;
        mapping.NewScrollbackRows = newScrollbackRows;

        var resized = new Screen(rows, columns, _scrollbackCapacity);
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

        if (mapping.TryMap(anchor, out var anchorPosition))
        {
            mapping.Row = anchorPosition.Row;
            mapping.Column = anchorPosition.Column;
            mapping.WrapPending = anchorPosition.WrapPending;
            mapping.InScrollback = anchorPosition.InScrollback;
            mapping.IsMapped = true;
        }
        return resized;
    }

    private int NormalizeScrollbackRows(int scrollbackRows) =>
        Math.Clamp(scrollbackRows < 0 ? _scrollbackCapacity : scrollbackRows, 0, _scrollbackCapacity);

    private SourceLayout BuildSourceLayout(int scrollbackRows)
    {
        int retainedScrollbackRows = NormalizeScrollbackRows(scrollbackRows);
        var layout = new SourceLayout();
        LogicalLine? currentLine = null;
        SourceRow? previousSourceRow = null;

        for (int index = 0; index < retainedScrollbackRows + Rows; index++)
        {
            int logicalRow = index - retainedScrollbackRows;
            int physicalRow = GetPhysicalRow(logicalRow);
            var sourceRow = new SourceRow();
            bool continuesPrevious = RowContinuesPrevious[physicalRow];
            if (!continuesPrevious)
            {
                currentLine = new LogicalLine(layout.Lines.Count);
                layout.Lines.Add(currentLine);
            }
            else if (currentLine is null || previousSourceRow is null)
            {
                // Metadata can be introduced after a pre-existing ring state;
                // an orphan continuation cannot be attached deterministically.
                layout.Rows.Add(sourceRow);
                previousSourceRow = sourceRow;
                continue;
            }

            sourceRow.LogicalLine = currentLine.Identity;
            sourceRow.UnitStart = currentLine.Cells.Count;
            int rowEnd = RowEndCol[physicalRow];
            if (rowEnd < 0)
                rowEnd = FindFallbackRowEnd(physicalRow);
            if (rowEnd >= 0)
            {
                rowEnd = Math.Min(rowEnd, Columns - 1);
                for (int column = 0; column <= rowEnd; column++)
                {
                    ref var hot = ref UnsafeAsRef<CellHot>(
                        _cellsPtr,
                        physicalRow * Columns + column);
                    if (hot.IsContinuation)
                    {
                        if (sourceRow.Units.Count > 0)
                        {
                            var previous = sourceRow.Units[^1];
                            if (previous.Column + previous.Width > column)
                                continue;
                        }
                        // A continuation without a base is an orphan cell.
                        continue;
                    }

                    int width = 1;
                    if (hot.Width > 1 && column + 1 <= rowEnd && column + 1 < Columns)
                    {
                        ref var continuation = ref UnsafeAsRef<CellHot>(
                            _cellsPtr,
                            physicalRow * Columns + column + 1);
                        if (continuation.IsContinuation)
                            width = 2;
                    }

                    var normalizedHot = hot;
                    normalizedHot.Width = (byte)width;
                    var cold = UnsafeAsRef<ColdCell>(
                        _coldCellsPtr,
                        physicalRow * Columns + column);
                    int offset = currentLine.Cells.Count;
                    currentLine.Cells.Add(new ReflowCell(normalizedHot, cold, width));
                    sourceRow.Units.Add(new SourceUnit(column, width, offset));
                }
            }

            layout.Rows.Add(sourceRow);
            previousSourceRow = sourceRow;
        }

        return layout;
    }

    private int FindFallbackRowEnd(int physicalRow)
    {
        int offset = physicalRow * Columns;
        for (int column = Columns - 1; column >= 0; column--)
        {
            var hot = UnsafeAsRef<CellHot>(_cellsPtr, offset + column);
            var cold = UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + column);
            if (hot.Rune != 0 || hot.PackedFlags != 0 || hot.StyleId != 0
                || cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
            {
                return column;
            }
        }
        return -1;
    }

    private static void EmitLogicalLine(
        LogicalLine line,
        int columns,
        int sourceColumns,
        List<EmittedRow> emitted,
        ReflowMapping mapping)
    {
        if (line.Cells.Count == 0)
        {
            int emptyOutputIndex = emitted.Count;
            emitted.Add(new EmittedRow(continuesPrevious: false));
            for (int offset = 0; offset < sourceColumns; offset++)
            {
                mapping.Add(
                    line.Identity,
                    offset,
                    new ReflowPosition(
                        -1,
                        Math.Min(offset, columns - 1),
                        false,
                        false,
                        emptyOutputIndex));
            }
            return;
        }

        var row = new EmittedRow(continuesPrevious: false);
        emitted.Add(row);
        int outputIndex = emitted.Count - 1;
        int usedColumns = 0;

        for (int offset = 0; offset < line.Cells.Count; offset++)
        {
            var cell = line.Cells[offset];
            int width = cell.Width == 2 && columns >= 2 ? 2 : 1;
            if (usedColumns + width > columns)
            {
                row = new EmittedRow(continuesPrevious: true);
                emitted.Add(row);
                outputIndex = emitted.Count - 1;
                usedColumns = 0;
            }

            mapping.Add(
                line.Identity,
                offset,
                new ReflowPosition(-1, usedColumns, false, false, outputIndex));
            row.Cells.Add(cell with { Width = width });
            usedColumns += width;
        }

        int endColumn = Math.Max(0, Math.Min(columns - 1, usedColumns));
        mapping.Add(
            line.Identity,
            line.Cells.Count,
            new ReflowPosition(-1, endColumn, false, false, outputIndex));
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
            if (cell.Cold.HyperlinkId != 0 || cell.Cold.GraphemeIndex >= 0)
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
                };
                UnsafeAsRef<CellHot>(destination._cellsPtr, offset + usedColumns + 1) = continuation;
                UnsafeAsRef<ColdCell>(destination._coldCellsPtr, offset + usedColumns + 1) = cell.Cold;
                if (cell.Cold.HyperlinkId != 0 || cell.Cold.GraphemeIndex >= 0)
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
