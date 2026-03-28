namespace Dotty.Terminal.Adapter;

/// <summary>
/// Lightweight 2D cell storage with helpers for clearing, scrolling, and resizing.
/// </summary>
public class Screen
{
    public Cell[] Cells => _cells;
    private Cell[] _cells;
    public int[] RowMap => _rowMap;
    private int[] _rowMap;

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
        _cells = new Cell[rows * columns];
        _rowMap = new int[rows];
        for (int i = 0; i < rows; i++) _rowMap[i] = i;
        Clear();
    }

    public ref Cell GetCellRef(int row, int col) => ref _cells[_rowMap[row] * Columns + col];

    public void ReadSnapshot(ref Cell[] cellsSnapshot, ref int[] rowMapSnapshot)
    {
        if (cellsSnapshot == null || cellsSnapshot.Length != _cells.Length)
            cellsSnapshot = new Cell[_cells.Length];
            
        if (rowMapSnapshot == null || rowMapSnapshot.Length != _rowMap.Length)
            rowMapSnapshot = new int[_rowMap.Length];
            
        Array.Copy(_rowMap, rowMapSnapshot, _rowMap.Length);
        Array.Copy(_cells, cellsSnapshot, _cells.Length);
    }

    public Cell[] ExtractRow(int row)
    {
        var result = new Cell[Columns];
        if (row >= 0 && row < Rows)
        {
            Array.Copy(_cells, _rowMap[row] * Columns, result, 0, Columns);
            // We can skip deep inspecting continuations here because we never write continuation cells with non-null graphemes internally anyway. 
            // It was just a safety feature taking too much time on yes command 500k lines.
        }
        else
        {
            for (int i = 0; i < Columns; i++)
                result[i] = new Cell { Grapheme = " ", Width = 1 };
        }
        return result;
    }

    public Cell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            var c = new Cell { Width = 1 };
            c.Rune = 32; // space
            return c;
        }

        ref var cell = ref _cells[_rowMap[row] * Columns + col];
        if (cell.IsContinuation && cell.Rune != 0)
        {
            cell.Rune = 0;
            cell.Width = 0;
        }
        if (!cell.IsContinuation && cell.Rune != 0 && cell.Width > 1)
        {
            int w = Math.Max(1, (int)cell.Width);
            bool missing = false;
            for (int i = 1; i < w; i++)
            {
                int cc = col + i;
                if (cc >= Columns) { missing = true; break; }
                var cont = _cells[_rowMap[row] * Columns + cc];
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
                    ref var cont = ref _cells[_rowMap[row] * Columns + cc];
                    cont.Reset();
                    cont.IsContinuation = true;
                    cont.Width = 0;
                }
            }
        }

        return _cells[_rowMap[row] * Columns + col];
    }

    public void Clear()
    {
        Array.Clear(_cells, 0, _cells.Length);
    }

    // Expose internal cells array for tests. This is intentionally marked
    // `internal` so production code is unaffected; tests can access this via
    // `InternalsVisibleTo` from the test project.
    internal Cell[,] GetCellsForTests()
    {
        var result = new Cell[Rows, Columns];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                result[r, c] = _cells[_rowMap[r] * Columns + c];
            }
        }
        return result;
    }

    public void ClearCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            return;
        }

        // Find the base cell that covers the addressed column.
        int baseCol = col;
        if (_cells[_rowMap[row] * Columns + baseCol].IsContinuation)
        {
            while (baseCol > 0 && _cells[_rowMap[row] * Columns + baseCol].IsContinuation)
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
                ref var cand = ref _cells[_rowMap[row] * Columns + scan];
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
        ref var cell = ref _cells[_rowMap[row] * Columns + baseCol];
        cell.Reset();

        int c = baseCol + 1;
        while (c < Columns)
        {
            ref var nxt = ref _cells[_rowMap[row] * Columns + c];
            if (!nxt.IsContinuation) break;
            nxt.Reset();
            c++;
        }

    }

        public void ScrollUp(int lines)
    {
        if (lines <= 0) return;
        if (lines >= Rows) { Clear(); return; }

        for (int i = 0; i < lines; i++)
        {
            int oldRow = _rowMap[0];
            Array.Copy(_rowMap, 1, _rowMap, 0, Rows - 1);
            _rowMap[Rows - 1] = oldRow;
            int offset = oldRow * Columns;
            Array.Clear(_cells, offset, Columns);
        }
    }

    public void ScrollUpRegion(int top, int bottom, int lines)
    {
        if (lines <= 0) return;
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top >= bottom) return;

        int regionHeight = bottom - top + 1;
        if (lines >= regionHeight)
        {
            for (int r = top; r <= bottom; r++)
            {
                int offset = _rowMap[r] * Columns;
                Array.Clear(_cells, offset, Columns);
            }
            return;
        }

        // Extremely fast path for full screen single line scroll, very common
        if (lines == 1 && top == 0 && bottom == Rows - 1)
        {
            int oldRow = _rowMap[0];
            _rowMap.AsSpan(1, Rows - 1).CopyTo(_rowMap.AsSpan(0, Rows - 1));
            _rowMap[Rows - 1] = oldRow;
            Array.Clear(_cells, oldRow * Columns, Columns);
            return;
        }

        if (lines == 1)
        {
            int oldRow = _rowMap[top];
            Array.Copy(_rowMap, top + 1, _rowMap, top, regionHeight - 1);
            _rowMap[bottom] = oldRow;
            Array.Clear(_cells, oldRow * Columns, Columns);
            return;
        }

        // Save the lost rows
        int[] oldRows = new int[lines];
        Array.Copy(_rowMap, top, oldRows, 0, lines);
        
        // Shift the remaning rows up
        Array.Copy(_rowMap, top + lines, _rowMap, top, regionHeight - lines);
        
        // Move lost rows to the bottom and clear them
        for (int l = 0; l < lines; l++)
        {
            int oldRow = oldRows[l];
            _rowMap[bottom - lines + 1 + l] = oldRow;
            Array.Clear(_cells, oldRow * Columns, Columns);
        }
    }

    public void ScrollDown(int lines)
    {
        if (lines <= 0) return;
        if (lines >= Rows) { Clear(); return; }

        for (int i = 0; i < lines; i++)
        {
            int oldRow = _rowMap[Rows - 1];
            Array.Copy(_rowMap, 0, _rowMap, 1, Rows - 1);
            _rowMap[0] = oldRow;
            int offset = oldRow * Columns;
            Array.Clear(_cells, offset, Columns);
        }
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
                int offset = _rowMap[r] * Columns;
                Array.Clear(_cells, offset, Columns);
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
                    _cells[_rowMap[r] * Columns + c] = _cells[_rowMap[r - lines] * Columns + c];
                }
            }

            for (int r = top; r < top + lines; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    ref var cell = ref _cells[_rowMap[r] * Columns + c];
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
                ref var cell = ref _cells[_rowMap[r] * Columns + c];
                if (!cell.IsContinuation && cell.Rune != 0)
                {
                    int w = Math.Max(1, (int)cell.Width);
                    bool missing = false;
                    for (int i = 1; i < w && c + i <= colEnd; i++)
                    {
                        var cont = _cells[_rowMap[r] * Columns + c + i];
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
                            ref var cont = ref _cells[_rowMap[r] * Columns + c + i];
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
            destination._cells[_rowMap[r] * Columns + c] = _cells[_rowMap[r] * Columns + c];
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
