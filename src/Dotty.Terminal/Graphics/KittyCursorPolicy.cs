using System;

namespace Dotty.Terminal.Graphics;

/// <summary>Where the cursor ends up after an image placement, and how far the scroll region must scroll to get there.</summary>
internal readonly record struct CursorAdvance(int Row, int Col, int ScrollRows);

/// <summary>Pure cursor policy for a placed image (<c>a=p</c>/<c>a=T</c> without <c>C=1</c>, not virtual, not relative).</summary>
internal static class KittyCursorPolicy
{
    /// <summary>
    /// <c>col += cols; row += max(rows - 1, 0)</c>; a column at or past the right edge wraps to column 0 of the next
    /// row; a row past <paramref name="scrollBottom"/> scrolls the region up by the overshoot (at most
    /// <paramref name="rows"/>) and stays on the bottom margin. A cursor that started below the scroll region
    /// (outside the margins) never scrolls it: it only clamps to the last screen row.
    /// </summary>
    /// <param name="row">Cursor row at placement time (0-based, screen relative).</param>
    /// <param name="col">Cursor column at placement time.</param>
    /// <param name="columns">Screen width in cells.</param>
    /// <param name="rows">Screen height in cells.</param>
    /// <param name="scrollBottom">Bottom margin row (0-based, inclusive).</param>
    /// <param name="effCols">Placement width in cells.</param>
    /// <param name="effRows">Placement height in cells.</param>
    public static CursorAdvance Advance(int row, int col, int columns, int rows, int scrollBottom, int effCols, int effRows)
    {
        long newCol = (long)col + Math.Max(effCols, 0);
        long newRow = (long)row + Math.Max(effRows - 1, 0);
        if (newCol >= columns)
        {
            newCol = 0;
            newRow++;
        }

        int scroll = 0;
        if (newRow > scrollBottom)
        {
            if (row <= scrollBottom)
            {
                scroll = (int)Math.Min(newRow - scrollBottom, rows);
                newRow = scrollBottom;
            }
            else
            {
                newRow = Math.Min(newRow, rows - 1);
            }
        }
        return new CursorAdvance((int)newRow, (int)newCol, scroll);
    }
}
