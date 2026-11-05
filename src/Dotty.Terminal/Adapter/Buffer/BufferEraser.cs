namespace Dotty.Terminal.Adapter;

/// <summary>
/// Erase helpers: line clearing and backspace behavior across grapheme widths.
/// </summary>
internal sealed class BufferEraser
{
    public void EraseLine(Screen buffer, CursorController cursor, int columns, int mode, bool respectProtection)
    {
        if (mode == 2)
            Clear(buffer, cursor.Row, 0, columns, respectProtection);
        else if (mode == 0)
            Clear(buffer, cursor.Row, cursor.Col, columns, respectProtection);
        else if (mode == 1)
            Clear(buffer, cursor.Row, 0, cursor.Col + 1, respectProtection);
    }

    private static void Clear(Screen buffer, int row, int start, int end, bool respectProtection)
    {
        if (respectProtection)
            buffer.ClearUnprotectedColumns(row, start, end);
        else
            buffer.ClearColumns(row, start, end);
    }

    /// <returns>True if the cursor should be reset to 0,0 (mode 2).</returns>
    public bool EraseDisplay(Screen buffer, CursorController cursor, int rows, int columns, int mode, bool respectProtection, bool selective)
    {
        if (mode == 2)
        {
            if (selective)
            {
                for (int row = 0; row < rows; row++)
                    Clear(buffer, row, 0, columns, respectProtection);
                return false;
            }

            if (respectProtection)
            {
                buffer.DiscardHistory();
                for (int row = 0; row < rows; row++)
                    buffer.ClearUnprotectedColumns(row, 0, columns);
            }
            else
            {
                buffer.Clear();
            }
            return true;
        }

        if (mode == 0)
        {
            Clear(buffer, cursor.Row, cursor.Col, columns, respectProtection);
            for (int r = cursor.Row + 1; r < rows; r++)
                Clear(buffer, r, 0, columns, respectProtection);
            return false;
        }

        if (mode == 1)
        {
            for (int r = 0; r < cursor.Row; r++)
                Clear(buffer, r, 0, columns, respectProtection);
            Clear(buffer, cursor.Row, 0, cursor.Col + 1, respectProtection);
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
