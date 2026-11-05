using System;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    /// <summary>Clears complete unprotected glyphs intersecting [startCol, endExclusive).</summary>
    internal void ClearUnprotectedColumns(int logicalRow, int startCol, int endExclusive)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        startCol = Math.Max(0, startCol);
        endExclusive = Math.Min(Columns, endExclusive);
        if (startCol >= endExclusive) return;

        int physicalRow = GetPhysicalRow(logicalRow);
        int rowOffset = physicalRow * Columns;
        int col = startCol;
        int clearStart = -1;
        int clearEnd = -1;
        bool clearedAny = false;

        while (col < endExclusive)
        {
            int glyphStart = GlyphStart(rowOffset, col);
            ref CellHot baseCell = ref UnsafeAsRef<CellHot>(_cellsPtr, rowOffset + glyphStart);
            int glyphEnd = Math.Min(Columns, glyphStart + baseCell.Width);
            bool protectedGlyph = baseCell.IsProtected;
            if (protectedGlyph)
            {
                if (clearStart >= 0)
                {
                    ClearGlyphSpan(rowOffset, clearStart, clearEnd);
                    clearStart = clearEnd = -1;
                    clearedAny = true;
                }
            }
            else
            {
                if (clearStart < 0) clearStart = glyphStart;
                clearEnd = glyphEnd;
            }

            col = Math.Max(col + 1, glyphEnd);
        }

        if (clearStart >= 0)
        {
            ClearGlyphSpan(rowOffset, clearStart, clearEnd);
            clearedAny = true;
        }
        if (clearedAny)
        {
            RecalculateRowMaxCol(logicalRow);
            RecalculateRowEndCol(logicalRow);
            RowContinuesPrevious[physicalRow] = false;
            if (RowEndCol[physicalRow] < 0)
                ClearRowContinuation(logicalRow + 1);
        }
    }

    private void ClearGlyphSpan(int rowOffset, int startCol, int endCol)
    {
        int count = endCol - startCol;
        int firstCell = rowOffset + startCol;
        new Span<CellHot>((void*)(_cellsPtr + (nint)firstCell * Unsafe.SizeOf<CellHot>()), count).Clear();
        new Span<ColdCell>((void*)(_coldCellsPtr + (nint)firstCell * Unsafe.SizeOf<ColdCell>()), count).Clear();
    }
}
