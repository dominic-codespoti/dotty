namespace Dotty.Terminal.Adapter;

/// <summary>
/// Erase helpers: line clearing and backspace behavior across grapheme widths.
/// </summary>
internal sealed class BufferEraser
{
    public void EraseLine(Screen buffer, CursorController cursor, int columns, int mode)
    {
        if (mode == 2)
            buffer.ClearColumns(cursor.Row, 0, columns);
        else if (mode == 0)
            buffer.ClearColumns(cursor.Row, cursor.Col, columns);
        else if (mode == 1)
            buffer.ClearColumns(cursor.Row, 0, cursor.Col + 1);
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
            buffer.ClearColumns(cursor.Row, cursor.Col, columns);
            for (int r = cursor.Row + 1; r < rows; r++)
                buffer.ClearColumns(r, 0, columns);
            return false;
        }

        if (mode == 1)
        {
            for (int r = 0; r < cursor.Row; r++)
                buffer.ClearColumns(r, 0, columns);
            buffer.ClearColumns(cursor.Row, 0, cursor.Col + 1);
        }

        return false;
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
                buffer.ClearCell(cursor.Row, cursor.Col);
                cursor.MoveBackward(rows, columns);
                break;
            }

            if (!cell.IsEmpty)
            {
                buffer.ClearCell(cursor.Row, cursor.Col);
            }
            break;
        }
    }
}
