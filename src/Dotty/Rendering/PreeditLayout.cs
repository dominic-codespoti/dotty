using System;
using System.Collections.Generic;
using System.Globalization;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk.Rendering;

/// <summary>How a preedit cell is styled: by its clause and whether that clause is the focused (converting) one.</summary>
internal enum PreeditCellStyle : byte
{
    /// <summary>Unconverted text or a clause that is not focused: underlined.</summary>
    Clause,
    /// <summary>The focused clause: drawn with the selection colours.</summary>
    Focused,
}

/// <summary>One placed grapheme of a preedit string. Column/row are grid cells; width is 1 or 2.</summary>
internal readonly record struct PreeditCell(int Utf16Start, int Utf16Length, int Column, int Row, int Width, int Clause, PreeditCellStyle Style);

/// <summary>Grid box the preedit may occupy: columns [Left, Right) and rows [Top, Bottom), all exclusive ends.</summary>
internal readonly record struct PreeditBox(int Left, int Right, int Top, int Bottom)
{
    public int Columns => Right - Left;
    public int Rows => Bottom - Top;
}

/// <summary>Result of <see cref="PreeditLayout.Compute"/>: the caret cell (insertion point before that cell).</summary>
internal readonly record struct PreeditLayoutResult(int CaretColumn, int CaretRow, bool CaretVisible, int RowsUsed, int FirstRow);

/// <summary>
/// Places preedit graphemes on the grid instead of clipping them at the row end. Text starts at the cursor
/// and wraps onto following rows at the box's left edge (wide graphemes never straddle the right edge). When
/// the wrapped text would pass the box bottom it is shifted up (drawn over previous rows); when it is taller
/// than the whole box, the rows scroll so that the caret row stays visible and earlier rows are dropped. A
/// one-row box (search input) therefore shows the caret's segment: a left shift.
/// </summary>
internal static class PreeditLayout
{
    public static PreeditLayoutResult Compute(in ImePreeditState state, int startColumn, int startRow, PreeditBox box, List<PreeditCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        cells.Clear();
        string text = state.Text ?? string.Empty;
        if (text.Length == 0 || box.Columns <= 0 || box.Rows <= 0
            || startColumn < box.Left || startColumn >= box.Right || startRow < box.Top || startRow >= box.Bottom)
            return new PreeditLayoutResult(startColumn, startRow, false, 0, startRow);

        int caretUtf16 = Math.Clamp(state.CaretIndex, 0, text.Length);
        int selectionStart = Math.Clamp(state.SelectionStart, 0, text.Length);
        int selectionEnd = selectionStart + Math.Clamp(state.SelectionLength, 0, text.Length - selectionStart);

        // Pass 1: wrap into layout rows (row 0 is the cursor row).
        int column = startColumn;
        int row = 0;
        int caretColumn = -1, caretRow = -1;
        int utf16 = 0;
        while (utf16 < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text, utf16);
            if (length <= 0)
                break;
            int width = Math.Min(UnicodeWidth.GetWidth(text.AsSpan(utf16, length)), 2);
            if (width > 0 && width <= box.Columns && column + width > box.Right)
            {
                column = box.Left;
                row++;
            }
            if (caretRow < 0 && caretUtf16 < utf16 + length)
            {
                caretColumn = column;
                caretRow = row;
            }
            if (width > 0 && width <= box.Columns)
            {
                int clause = state.Clauses.IndexAt(utf16);
                bool focused = state.SelectionLength > 0 && selectionStart < utf16 + length && selectionEnd > utf16;
                cells.Add(new PreeditCell(utf16, length, column, row, width, clause,
                    focused ? PreeditCellStyle.Focused : PreeditCellStyle.Clause));
                column += width;
            }
            utf16 += length;
        }
        if (caretRow < 0)
        {
            // Caret after the last grapheme: an insertion point past the right edge moves to the next row.
            if (column >= box.Right)
            {
                column = box.Left;
                row++;
            }
            caretColumn = column;
            caretRow = row;
        }
        int rowsUsed = Math.Max(row, caretRow) + 1;

        // Pass 2: shift up into the box, then scroll so the caret row is visible.
        int shift = Math.Clamp(startRow + rowsUsed - box.Bottom, 0, startRow - box.Top);
        int caretScreenRow = startRow + caretRow - shift;
        if (caretScreenRow >= box.Bottom)
            shift += caretScreenRow - (box.Bottom - 1);
        int written = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            int screenRow = startRow + cell.Row - shift;
            if (screenRow < box.Top || screenRow >= box.Bottom)
                continue;
            cells[written++] = cell with { Row = screenRow };
        }
        cells.RemoveRange(written, cells.Count - written);
        int firstRow = Math.Max(box.Top, startRow - shift);
        return new PreeditLayoutResult(caretColumn, startRow + caretRow - shift, true, rowsUsed, firstRow);
    }
}
