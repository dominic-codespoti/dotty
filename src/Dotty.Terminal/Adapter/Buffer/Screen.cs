namespace Dotty.Terminal.Adapter;

/// <summary>
/// Lightweight 2D cell storage with helpers for clearing, scrolling, and resizing.
/// </summary>
public class Screen
{
    private Cell[,] _cells;

    public void MarkRender()
    {
        // No per-render cache tracked anymore; method retained for compatibility.
    }

    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public Screen(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _cells = new Cell[rows, columns];
        Clear();
    }

    public ref Cell GetCellRef(int row, int col) => ref _cells[row, col];

    public Cell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            return new Cell { Grapheme = " ", Width = 1, Bold = false };
        }

        ref var cell = ref _cells[row, col];
        if (cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
        {
            cell.Grapheme = null;
            cell.Width = 0;
        }
        if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme) && cell.Width > 1)
        {
            int w = Math.Max(1, (int)cell.Width);
            bool missing = false;
            for (int i = 1; i < w; i++)
            {
                int cc = col + i;
                if (cc >= Columns) { missing = true; break; }
                var cont = _cells[row, cc];
                if (!cont.IsContinuation)
                {
                    missing = true;
                    break;
                }
            }

            if (missing)
            {
                for (int i = 1; i < w; i++)
                {
                    int cc = col + i;
                    if (cc >= Columns) break;
                    ref var cont = ref _cells[row, cc];
                    cont.Reset();
                    cont.IsContinuation = true;
                    cont.Width = 0;
                }
            }
        }

        return _cells[row, col];
    }

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
        for (int c = 0; c < Columns; c++)
        {
            ref var cell = ref _cells[r, c];
            cell.Reset();
        }
    }

    // Expose internal cells array for tests. This is intentionally marked
    // `internal` so production code is unaffected; tests can access this via
    // `InternalsVisibleTo` from the test project.
    internal Cell[,] GetCellsForTests() => _cells;

    public void ClearCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            return;
        }

        // Find the base cell that covers the addressed column.
        int baseCol = col;
        if (_cells[row, baseCol].IsContinuation)
        {
            while (baseCol > 0 && _cells[row, baseCol].IsContinuation)
            {
                baseCol--;
            }
        }
        else
        {
            // Scan leftwards for a base whose width spans the column.
            int scan = baseCol - 1;
            while (scan >= 0)
            {
                ref var cand = ref _cells[row, scan];
                // Treat zero/unknown width as 1 for safety.
                int w = Math.Max(1, (int)cand.Width);
                if (!cand.IsContinuation && w > 1 && scan + w > col)
                {
                    baseCol = scan;
                    break;
                }
                scan--;
            }
        }

        // Reset the base cell and any continuation cells that follow.
        ref var cell = ref _cells[row, baseCol];
        cell.Reset();

        int c = baseCol + 1;
        while (c < Columns)
        {
            ref var nxt = ref _cells[row, c];
            if (!nxt.IsContinuation) break;
            nxt.Reset();
            c++;
        }

    }

    public void ScrollUp(int lines)
    {
        bool fastPathDone = false;
        try
        {
            int srcIndex = lines * Columns;
            int destIndex = 0;
            int count = (Rows - lines) * Columns;
            if (count > 0) Array.Copy(_cells, srcIndex, _cells, destIndex, count);

            // Clear trailing lines at the bottom
            int clearStart = (Rows - lines) * Columns;
            int clearCount = lines * Columns;
            if (clearCount > 0) Array.Clear(_cells, clearStart, clearCount);

            fastPathDone = true;
        }
        catch
        {
            fastPathDone = false;
        }

        if (!fastPathDone)
        {
            for (int i = 0; i < Rows - lines; i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    _cells[i, j] = _cells[i + lines, j];
                }
            }

            for (int i = Rows - lines; i < Rows; i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    ref var cell = ref _cells[i, j];
                    cell.Reset();
                }
            }

            try
            {
                int startRow = 0;
                int endRow = Math.Max(0, Rows - lines - 1);
                RepairOrphanedBases(startRow, endRow, 0, Columns - 1);
            }
            catch { }
        }
    }

    public void ScrollUpRegion(int top, int bottom, int lines)
    {
        if (lines <= 0) return;
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top > bottom) return;

        int regionHeight = bottom - top + 1;
        if (lines >= regionHeight)
        {
            for (int r = top; r <= bottom; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    ref var cell = ref _cells[r, c];
                    cell.Reset();
                }
            }
            return;
        }

        bool fastPathDone = false;
        try
        {
            int srcIndex = (top + lines) * Columns;
            int destIndex = top * Columns;
            int count = (regionHeight - lines) * Columns;
            if (count > 0) Array.Copy(_cells, srcIndex, _cells, destIndex, count);

            int clearStart = destIndex + count;
            int clearCount = lines * Columns;
            if (clearCount > 0) Array.Clear(_cells, clearStart, clearCount);

            fastPathDone = true;
        }
        catch
        {
            fastPathDone = false;
        }

        if (!fastPathDone)
        {
            for (int r = 0; r < regionHeight - lines; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    _cells[top + r, c] = _cells[top + r + lines, c];
                }
            }

            for (int r = top + regionHeight - lines; r <= bottom; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    ref var cell = ref _cells[r, c];
                    cell.Reset();
                }
            }

            try
            {
                int startRow = top;
                int endRow = Math.Min(bottom, top + regionHeight - lines - 1);
                RepairOrphanedBases(startRow, endRow, 0, Columns - 1);
            }
            catch { }
        }
    }

    public void ScrollDown(int lines)
    {
        if (lines <= 0) return;

        bool fastPathDone = false;
        try
        {
            int srcIndex = 0;
            int destIndex = lines * Columns;
            int count = (Rows - lines) * Columns;
            if (count > 0) Array.Copy(_cells, srcIndex, _cells, destIndex, count);

            if (lines * Columns > 0) Array.Clear(_cells, 0, lines * Columns);

            fastPathDone = true;
        }
        catch
        {
            fastPathDone = false;
        }

        if (!fastPathDone)
        {
            for (int i = Rows - 1; i >= lines; i--)
            {
                for (int j = 0; j < Columns; j++)
                {
                    _cells[i, j] = _cells[i - lines, j];
                }
            }

            for (int i = 0; i < Math.Min(lines, Rows); i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    ref var cell = ref _cells[i, j];
                    cell.Reset();
                }
            }
        }

        try
        {
            int startRow = Math.Max(0, lines);
            int endRow = Rows - 1;
            RepairOrphanedBases(startRow, endRow, 0, Columns - 1);
        }
        catch { }
    }

    public void ScrollDownRegion(int top, int bottom, int lines)
    {
        if (lines <= 0) return;
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top > bottom) return;

        int regionHeight = bottom - top + 1;
        if (lines >= regionHeight)
        {
            for (int r = top; r <= bottom; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    ref var cell = ref _cells[r, c];
                    cell.Reset();
                }
            }
            return;
        }

        bool fastPathDone = false;
        try
        {
            int srcIndex = top * Columns;
            int destIndex = (top + lines) * Columns;
            int count = (bottom - top + 1 - lines) * Columns;
            if (count > 0) Array.Copy(_cells, srcIndex, _cells, destIndex, count);

            int clearStart = top * Columns;
            int clearCount = lines * Columns;
            if (clearCount > 0) Array.Clear(_cells, clearStart, clearCount);

            fastPathDone = true;
        }
        catch
        {
            fastPathDone = false;
        }

        if (!fastPathDone)
        {
            for (int r = bottom; r >= top + lines; r--)
            {
                for (int c = 0; c < Columns; c++)
                {
                    _cells[r, c] = _cells[r - lines, c];
                }
            }

            for (int r = top; r < top + lines; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    ref var cell = ref _cells[r, c];
                    cell.Reset();
                }
            }
        }

        try
        {
            int startRow = top + lines;
            int endRow = bottom;
            RepairOrphanedBases(startRow, endRow, 0, Columns - 1);
        }
        catch { }
    }

    private void RepairOrphanedBases(int startRow, int endRow, int colStart, int colEnd)
    {
        if (startRow < 0) startRow = 0;
        if (endRow >= Rows) endRow = Rows - 1;
        colStart = Math.Max(0, colStart);
        colEnd = Math.Min(Columns - 1, colEnd);

        for (int r = startRow; r <= endRow; r++)
        {
            for (int c = colStart; c <= colEnd; c++)
            {
                ref var cell = ref _cells[r, c];
                if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
                {
                    int w = Math.Max(1, (int)cell.Width);
                    bool missing = false;
                    for (int i = 1; i < w; i++)
                    {
                        if (c + i > colEnd) { missing = true; break; }
                        var cont = _cells[r, c + i];
                        if (!cont.IsContinuation)
                        {
                            missing = true;
                            break;
                        }
                    }

                    if (missing)
                    {
                        cell.Reset();
                        for (int i = 1; i < w && c + i < Columns; i++)
                        {
                            ref var cont = ref _cells[r, c + i];
                            if (!cont.IsContinuation) break;
                            cont.Reset();
                        }
                    }
                }
            }
        }
    }

    public Screen Resize(int rows, int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        if (rows == Rows && columns == Columns)
        {
            return this;
        }

        var resized = new Screen(rows, columns);
        CopyTo(resized);
        return resized;
    }

    public void CopyTo(Screen destination)
    {
        int rows = Math.Min(Rows, destination.Rows);
        int cols = Math.Min(Columns, destination.Columns);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            destination._cells[r, c] = _cells[r, c];
        }
    }

    /// <summary>
    /// Clear cells from the given start column to the end of the row.
    /// Uses ClearCell so continuation cells are handled correctly.
    /// </summary>
    public void ClearFromColumn(int row, int startCol)
    {
        if (row < 0 || row >= Rows) return;
        if (startCol < 0) startCol = 0;
        if (startCol >= Columns) return;

        for (int c = startCol; c < Columns; c++)
        {
            ClearCell(row, c);
        }
    }

}
