using System;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    internal void CopyRectangle(int sourceTop, int sourceLeft, int destinationTop, int destinationLeft, int height, int width)
    {
        if (height <= 0 || width <= 0
            || (uint)sourceTop >= (uint)Rows || (uint)destinationTop >= (uint)Rows
            || (uint)sourceLeft >= (uint)Columns || (uint)destinationLeft >= (uint)Columns)
            return;
        height = Math.Min(height, Math.Min(Rows - sourceTop, Rows - destinationTop));
        width = Math.Min(width, Math.Min(Columns - sourceLeft, Columns - destinationLeft));
        bool downward = destinationTop > sourceTop;
        int first = downward ? height - 1 : 0;
        int end = downward ? -1 : height;
        int step = downward ? -1 : 1;

        for (int row = first; row != end; row += step)
        {
            int sourcePhysical = GetPhysicalRow(sourceTop + row);
            int destinationPhysical = GetPhysicalRow(destinationTop + row);
            int sourceOffset = sourcePhysical * Columns + sourceLeft;
            int rowOffset = destinationPhysical * Columns;
            int destinationOffset = rowOffset + destinationLeft;
            int destinationEnd = destinationLeft + width;

            // Capture cuts before memmove: repairing a destination edge first can
            // destroy a wide glyph that is still part of an overlapping source.
            bool sourceLeftPartial = sourceLeft > 0
                && UnsafeAsRef<CellHot>(_cellsPtr, sourceOffset).IsContinuation;
            bool sourceRightPartial = sourceLeft + width < Columns
                && UnsafeAsRef<CellHot>(_cellsPtr, sourceOffset + width).IsContinuation;
            bool destinationLeftPartial = destinationLeft > 0
                && UnsafeAsRef<CellHot>(_cellsPtr, destinationOffset).IsContinuation;
            bool destinationRightPartial = destinationEnd < Columns
                && UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + destinationEnd).IsContinuation;

            Span<CellHot> destination = new((void*)(_cellsPtr + (nint)destinationOffset * Unsafe.SizeOf<CellHot>()), width);
            new ReadOnlySpan<CellHot>((void*)(_cellsPtr + (nint)sourceOffset * Unsafe.SizeOf<CellHot>()), width).CopyTo(destination);
            // Every copied position is written, including previously uninitialized
            // blanks. Materialize spaces so later edits/reflow retain that extent.
            foreach (ref CellHot cell in destination)
                if (cell.Rune == 0 && !cell.IsContinuation)
                    cell.Rune = 32;
            if (_rowColdFlags[sourcePhysical])
            {
                new ReadOnlySpan<ColdCell>((void*)(_coldCellsPtr + (nint)sourceOffset * Unsafe.SizeOf<ColdCell>()), width)
                    .CopyTo(new Span<ColdCell>((void*)(_coldCellsPtr + (nint)destinationOffset * Unsafe.SizeOf<ColdCell>()), width));
                _rowColdFlags[destinationPhysical] = true;
            }
            else if (_rowColdFlags[destinationPhysical])
            {
                new Span<ColdCell>((void*)(_coldCellsPtr + (nint)destinationOffset * Unsafe.SizeOf<ColdCell>()), width).Clear();
            }

            bool hasCold = _rowColdFlags[destinationPhysical];
            if (destinationLeftPartial) BlankRectangleCell(rowOffset, destinationLeft - 1, hasCold);
            if (destinationRightPartial) BlankRectangleCell(rowOffset, destinationEnd, hasCold);
            if (sourceLeftPartial) BlankRectangleCell(rowOffset, destinationLeft, hasCold);
            if (sourceRightPartial) BlankRectangleCell(rowOffset, destinationEnd - 1, hasCold);
            RefreshRectangleRow(destinationTop + row);
        }
    }

    internal void FillRectangle(int top, int left, int bottom, int right, in CellHot fill)
    {
        for (int row = top; row <= bottom; row++)
        {
            int physicalRow = GetPhysicalRow(row);
            int rowOffset = physicalRow * Columns;
            bool hasCold = _rowColdFlags[physicalRow];
            RepairRectangleEdge(rowOffset, left, hasCold);
            RepairRectangleEdge(rowOffset, right + 1, hasCold);
            int offset = rowOffset + left;
            int width = right - left + 1;
            new Span<CellHot>((void*)(_cellsPtr + (nint)offset * Unsafe.SizeOf<CellHot>()), width).Fill(fill);
            if (hasCold)
                new Span<ColdCell>((void*)(_coldCellsPtr + (nint)offset * Unsafe.SizeOf<ColdCell>()), width).Clear();
            RefreshRectangleRow(row);
        }
    }

    internal void WipeRectangle(int top, int left, int bottom, int right, bool respectProtection)
    {
        for (int row = top; row <= bottom; row++)
        {
            int physicalRow = GetPhysicalRow(row);
            int rowOffset = physicalRow * Columns;
            bool hasCold = _rowColdFlags[physicalRow];
            int column = left;
            while (column <= right)
            {
                int start = GlyphStart(rowOffset, column);
                ref CellHot glyph = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + start);
                int end = Math.Min(Columns, start + glyph.Width);
                if (!respectProtection || !glyph.IsProtected)
                {
                    for (int part = start; part < end; part++)
                        BlankRectangleCell(rowOffset, part, hasCold);
                }
                column = Math.Max(column + 1, end);
            }
            RefreshRectangleRow(row);
        }
    }

    private void RepairRectangleEdge(int rowOffset, int boundary, bool hasCold)
    {
        if (boundary <= 0 || boundary >= Columns) return;
        ref CellHot edge = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + boundary);
        ref CellHot previous = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + boundary - 1);
        if (!edge.IsContinuation || previous.IsContinuation || previous.Width != 2) return;
        BlankRectangleCell(rowOffset, boundary - 1, hasCold);
        BlankRectangleCell(rowOffset, boundary, hasCold);
    }

    private void BlankRectangleCell(int rowOffset, int column, bool hasCold)
    {
        ref CellHot cell = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + column);
        cell.Rune = 32;
        cell.Width = 1;
        cell.IsContinuation = false;
        cell.HasHyperlink = false;
        cell.HasGrapheme = false;
        if (hasCold)
            UnsafeAsRef<ColdCell>(_coldCellsPtr, rowOffset + column).Reset();
    }

    private void RefreshRectangleRow(int logicalRow)
    {
        int physicalRow = GetPhysicalRow(logicalRow);
        int rowOffset = physicalRow * Columns;
        RecalculateRowMaxCol(logicalRow);
        RecalculateRowEndCol(logicalRow);
        if (_rowColdFlags[physicalRow])
        {
            bool hasCold = false;
            for (int column = 0; column < Columns; column++)
            {
                ref ColdCell cold = ref UnsafeAsRef<ColdCell>(_coldCellsPtr, rowOffset + column);
                if (cold.HyperlinkId != 0 || cold.GraphemeIndex != -1)
                {
                    hasCold = true;
                    break;
                }
            }
            _rowColdFlags[physicalRow] = hasCold;
        }
    }

}
