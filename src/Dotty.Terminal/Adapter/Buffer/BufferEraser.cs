namespace Dotty.Terminal.Adapter;

/// <summary>
/// Erase helpers: line clearing and backspace behavior across grapheme widths.
/// </summary>
internal sealed class BufferEraser
{
    public void EraseLine(Screen buffer, CursorController cursor, int columns, int mode)
    {
        if (mode == 2)
        {
            for (int j = 0; j < columns; j++) buffer.ClearCell(cursor.Row, j);
        }
        else if (mode == 0)
        {
            for (int j = cursor.Col; j < columns; j++) buffer.ClearCell(cursor.Row, j);
        }
        else if (mode == 1)
        {
            for (int j = 0; j <= cursor.Col; j++) buffer.ClearCell(cursor.Row, j);
        }
    }

    /// <returns>True if the cursor should be reset to 0,0 (mode 2).</returns>
    public bool EraseDisplay(Screen buffer, CursorController cursor, int rows, int columns, int mode)
    {
        if (mode == 2)
        {
            buffer.Clear();
            return true;
        }

        if (mode == 0)
        {
            for (int j = cursor.Col; j < columns; j++) buffer.ClearCell(cursor.Row, j);
            for (int r = cursor.Row + 1; r < rows; r++)
                for (int c = 0; c < columns; c++) buffer.ClearCell(r, c);
            return false;
        }

        if (mode == 1)
        {
            for (int r = 0; r < cursor.Row; r++)
                for (int c = 0; c < columns; c++) buffer.ClearCell(r, c);
            for (int j = 0; j <= cursor.Col; j++) buffer.ClearCell(cursor.Row, j);
        }

        return false;
    }

    public void ClearLineFromCursor(Screen buffer, CursorController cursor, int columns)
    {
        for (int j = cursor.Col; j < columns; j++)
        {
            buffer.ClearCell(cursor.Row, j);
        }
    }

    public void ErasePreviousGlyph(Screen buffer, CursorController cursor, int rows, int columns)
    {
        if (cursor.Row == 0 && cursor.Col == 0)
        {
            return;
        }

        cursor.MoveBackward(rows, columns);

        while (cursor.Row >= 0)
        {
            ref var cell = ref buffer.GetCellRef(cursor.Row, cursor.Col);
            if (cell.IsContinuation)
            {
                cell.Reset();
                cursor.MoveBackward(rows, columns);
                continue;
            }

            if (!cell.IsEmpty)
            {
                int width = Math.Max(1, (int)cell.Width);
                cell.Reset();
                for (int i = 1; i < width && cursor.Col + i < columns; i++)
                {
                    ref var cont = ref buffer.GetCellRef(cursor.Row, cursor.Col + i);
                    if (!cont.IsContinuation)
                    {
                        break;
                    }
                    cont.Reset();
                }
            }
            break;
        }
    }
}
