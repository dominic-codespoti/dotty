using System;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    /// <summary>
    /// Zero-copy view of one scrollback row's occupied cells (<paramref name="scrollbackIndex"/> 0 = newest, i.e. logical
    /// row -1). The spans cover columns 0..end column (the row's end column, else its max column) and are valid only until
    /// the next mutation of this screen, so callers copy or format them under the buffer lock. Returns false (with empty
    /// spans) when the row holds no content.
    /// </summary>
    /// <remarks>
    /// This is the one place text extraction and snapshot capture read historical rows from, so a change of scrollback
    /// storage only has to re-implement this accessor.
    /// </remarks>
    internal bool TryReadScrollbackRow(int scrollbackIndex, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold)
    {
        int total = _scrollbackCapacity + Rows;
        int physicalRow = (_head - 1 - scrollbackIndex + total * 2) % total;
        int endCol = _rowEndCol[physicalRow] >= 0 ? _rowEndCol[physicalRow] : _rowMaxCol[physicalRow];
        if (endCol < 0)
        {
            hot = default;
            cold = default;
            return false;
        }

        int count = Math.Min(endCol, Columns - 1) + 1;
        hot = new ReadOnlySpan<CellHot>(
            (void*)(_cellsPtr + (nint)physicalRow * Columns * Unsafe.SizeOf<CellHot>()), count);
        cold = new ReadOnlySpan<ColdCell>(
            (void*)(_coldCellsPtr + (nint)physicalRow * Columns * Unsafe.SizeOf<ColdCell>()), count);
        return true;
    }
}
