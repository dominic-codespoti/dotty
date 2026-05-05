namespace Dotty.Terminal.Adapter;

/// <summary>
/// A simple 2D cell surface with basic operations like clear, scroll, and resize.
/// </summary>
public sealed class CellGrid
{
    private CellHot[,] _cells;
    private ColdCell[,] _coldCells;

    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public CellGrid(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _cells = new CellHot[rows, columns];
        _coldCells = new ColdCell[rows, columns];
        ClearAll();
    }

    public ref CellHot GetRef(int row, int col) => ref _cells[row, col];

    public CellHot GetValue(int row, int col) => _cells[row, col];

    public ColdCell GetColdValue(int row, int col) => _coldCells[row, col];

    public ref ColdCell GetColdRef(int row, int col) => ref _coldCells[row, col];

    public void ClearAll()
    {
        int rows = _cells.GetLength(0);
        int cols = _cells.GetLength(1);
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            _cells[r, c].Reset();
            _coldCells[r, c].Reset();
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
        _coldCells[row, col].Reset();

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
                _coldCells[row, col + i].Reset();
            }
        }
    }

    public void ScrollUp(int lines)
    {
        for (int i = 0; i < Rows - lines; i++)
        for (int j = 0; j < Columns; j++)
        {
            _cells[i, j] = _cells[i + lines, j];
            _coldCells[i, j] = _coldCells[i + lines, j];
        }

        for (int i = Rows - lines; i < Rows; i++)
        for (int j = 0; j < Columns; j++)
        {
            _cells[i, j].Reset();
            _coldCells[i, j].Reset();
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

        var newCells = new CellHot[rows, columns];
        var newCold = new ColdCell[rows, columns];
        int copyRows = Math.Min(rows, Rows);
        int copyCols = Math.Min(columns, Columns);
        for (int r = 0; r < copyRows; r++)
        for (int c = 0; c < copyCols; c++)
        {
            newCells[r, c] = _cells[r, c];
            newCold[r, c] = _coldCells[r, c];
        }
        for (int r = copyRows; r < rows; r++)
        for (int c = 0; c < columns; c++)
            newCold[r, c].GraphemeIndex = -1;

        _cells = newCells;
        _coldCells = newCold;
        Rows = rows;
        Columns = columns;
    }

    private bool InBounds(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Columns;
}
