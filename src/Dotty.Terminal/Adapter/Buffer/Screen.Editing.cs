using System;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    internal void InsertColumns(int row, int start, int right, int count)
    {
        if (!TryClampEdit(row, ref start, ref right, ref count)) return;
        int physicalRow = GetPhysicalRow(row);
        int rowOffset = physicalRow * Columns;
        int sourceEnd = right - count;
        RepairWideBoundary(rowOffset, start);
        RepairWideBoundary(rowOffset, sourceEnd + 1);
        RepairWideBoundary(rowOffset, right + 1);

        Span<CellHot> hot = new((void*)(_cellsPtr + (nint)rowOffset * Unsafe.SizeOf<CellHot>()), Columns);
        hot.Slice(start, sourceEnd - start + 1).CopyTo(hot.Slice(start + count));
        if (_rowColdFlags[physicalRow])
        {
            Span<ColdCell> cold = new((void*)(_coldCellsPtr + (nint)rowOffset * Unsafe.SizeOf<ColdCell>()), Columns);
            cold.Slice(start, sourceEnd - start + 1).CopyTo(cold.Slice(start + count));
            cold.Slice(start, count).Clear();
        }
        hot.Slice(start, count).Clear();
        RepairWideBoundary(rowOffset, start);
        RepairWideBoundary(rowOffset, start + count);
        RepairWideBoundary(rowOffset, right + 1);
        RefreshRowAfterEdit(physicalRow);
    }

    internal void DeleteColumns(int row, int start, int right, int count)
    {
        if (!TryClampEdit(row, ref start, ref right, ref count)) return;
        int physicalRow = GetPhysicalRow(row);
        int rowOffset = physicalRow * Columns;
        int sourceStart = start + count;
        RepairWideBoundary(rowOffset, start);
        RepairWideBoundary(rowOffset, sourceStart);
        RepairWideBoundary(rowOffset, right + 1);

        Span<CellHot> hot = new((void*)(_cellsPtr + (nint)rowOffset * Unsafe.SizeOf<CellHot>()), Columns);
        hot.Slice(sourceStart, right - sourceStart + 1).CopyTo(hot.Slice(start));
        if (_rowColdFlags[physicalRow])
        {
            Span<ColdCell> cold = new((void*)(_coldCellsPtr + (nint)rowOffset * Unsafe.SizeOf<ColdCell>()), Columns);
            cold.Slice(sourceStart, right - sourceStart + 1).CopyTo(cold.Slice(start));
            cold.Slice(right - count + 1, count).Clear();
        }
        hot.Slice(right - count + 1, count).Clear();
        RepairWideBoundary(rowOffset, start);
        RepairWideBoundary(rowOffset, right - count + 1);
        RepairWideBoundary(rowOffset, right + 1);
        RefreshRowAfterEdit(physicalRow);
    }

    private bool TryClampEdit(int row, ref int start, ref int right, ref int count)
    {
        if ((uint)row >= (uint)Rows || count <= 0 || start < 0 || right < start || start >= Columns)
            return false;
        right = Math.Min(right, Columns - 1);
        count = (int)Math.Min((long)count, (long)right - start + 1);
        return count > 0;
    }

    // A cut can split a width-two glyph even when its other half lies outside the edit rectangle.
    private void RepairWideBoundary(int rowOffset, int boundary)
    {
        if ((uint)boundary >= (uint)Columns) return;
        ref CellHot edge = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + boundary);
        int baseColumn = edge.IsContinuation ? boundary - 1 : boundary;
        if (baseColumn < 0) return;
        ref CellHot glyph = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + baseColumn);
        if (glyph.IsContinuation || glyph.Rune == 0 || glyph.Width <= 1) return;
        int width = glyph.Width;
        bool complete = baseColumn + width <= Columns;
        for (int i = 1; complete && i < width; i++)
            complete = UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + baseColumn + i).IsContinuation;
        if (complete && !(baseColumn < boundary && baseColumn + width > boundary)) return;
        ClearGlyphAt(rowOffset, baseColumn);
    }

    private void ClearGlyphAt(int rowOffset, int column)
    {
        ref CellHot cell = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + column);
        if (cell.IsContinuation && column > 0)
        {
            ref CellHot previous = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + column - 1);
            if (!previous.IsContinuation && previous.Width > 1) column--;
        }
        ref CellHot glyph = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + column);
        int width = glyph.IsContinuation ? 1 : Math.Max(1, (int)glyph.Width);
        int end = Math.Min(Columns, column + width);
        Span<CellHot> hot = new((void*)(_cellsPtr + (nint)(rowOffset + column) * Unsafe.SizeOf<CellHot>()), end - column);
        hot.Clear();
        if (_rowColdFlags[rowOffset / Columns])
        {
            Span<ColdCell> cold = new((void*)(_coldCellsPtr + (nint)(rowOffset + column) * Unsafe.SizeOf<ColdCell>()), end - column);
            cold.Clear();
        }
    }


    private void RefreshRowAfterEdit(int physicalRow)
    {
        bool hasCold = false;
        int offset = physicalRow * Columns;
        if (_rowColdFlags[physicalRow])
        {
            ReadOnlySpan<ColdCell> cold = new((void*)(_coldCellsPtr + (nint)offset * Unsafe.SizeOf<ColdCell>()), Columns);
            for (int i = 0; i < cold.Length; i++)
                if (cold[i].HyperlinkId != 0 || cold[i].GraphemeIndex != -1) { hasCold = true; break; }
            _rowColdFlags[physicalRow] = hasCold;
        }
        int logicalRow = physicalRow - _head;
        if (logicalRow < 0) logicalRow += TotalRows;
        RecalculateRowMaxCol(logicalRow);
        RecalculateRowEndCol(logicalRow);
        if (RowEndCol[physicalRow] < 0)
        {
            RowContinuesPrevious[physicalRow] = false;
            ClearRowContinuation(logicalRow + 1);
        }
    }
}
