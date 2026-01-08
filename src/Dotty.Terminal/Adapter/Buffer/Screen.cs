namespace Dotty.Terminal.Adapter;

/// <summary>
/// Lightweight 2D cell storage with helpers for clearing, scrolling, and resizing.
/// </summary>
public class Screen
{
    private Cell[,] _cells;

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

    public void ClearCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            return;
        }

        ref var cell = ref _cells[row, col];
        int width = Math.Max(1, (int)cell.Width);
        bool isContinuation = cell.IsContinuation;
        cell.Reset();

        if (!isContinuation)
        {
            for (int i = 1; i < width && col + i < Columns; i++)
            {
                ref var cont = ref _cells[row, col + i];
                if (!cont.IsContinuation)
                {
                    break;
                }
                cont.Reset();
            }
        }
    }

    public void ScrollUp(int lines)
    {
        for (int i = 0; i < Rows - lines; i++)
        for (int j = 0; j < Columns; j++)
            _cells[i, j] = _cells[i + lines, j];

        for (int i = Rows - lines; i < Rows; i++)
        for (int j = 0; j < Columns; j++)
        {
            ref var cell = ref _cells[i, j];
            cell.Reset();
        }
    }

    public void ScrollUpRegion(int top, int bottom, int lines)
    {
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top > bottom) return;
        int regionHeight = bottom - top + 1;
        if (lines <= 0) return;
        if (lines >= regionHeight)
        {
            // clear region
            for (int r = top; r <= bottom; r++)
            for (int c = 0; c < Columns; c++)
            {
                ref var cell = ref _cells[r, c];
                cell.Reset();
            }
            return;
        }

        for (int r = 0; r < regionHeight - lines; r++)
        for (int c = 0; c < Columns; c++)
            _cells[top + r, c] = _cells[top + r + lines, c];

        for (int r = top + regionHeight - lines; r <= bottom; r++)
        for (int c = 0; c < Columns; c++)
        {
            ref var cell = ref _cells[r, c];
            cell.Reset();
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
}
