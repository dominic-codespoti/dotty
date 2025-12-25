namespace Dotty.Terminal.Adapter;

/// <summary>
/// A simple 2D cell surface with basic operations like clear, scroll, and resize.
/// </summary>
public sealed class CellGrid
{
    private Cell[,] _cells;

    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public CellGrid(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _cells = new Cell[rows, columns];
        ClearAll();
    }

    public ref Cell GetRef(int row, int col) => ref _cells[row, col];

    public Cell GetValue(int row, int col) => _cells[row, col];

    public void ClearAll()
    {
        int rows = _cells.GetLength(0);
        int cols = _cells.GetLength(1);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            _cells[r, c].Reset();
        }
    }

    public void ClearCell(int row, int col)
    {
        if (!InBounds(row, col))
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
            _cells[i, j].Reset();
        }
    }

    public void Resize(int rows, int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        if (rows == Rows && columns == Columns)
        {
            return;
        }

        var newCells = new Cell[rows, columns];
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(columns, Columns);
        for (int r = 0; r < copyRows; r++)
        for (int c = 0; c < copyCols; c++)
        {
            newCells[r, c] = _cells[r, c];
        }

        _cells = newCells;
        Rows = rows;
        Columns = columns;
    }

    private bool InBounds(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Columns;
}
