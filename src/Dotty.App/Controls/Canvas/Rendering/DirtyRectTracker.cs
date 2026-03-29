namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// Encapsulates dirty rectangle tracking for terminal cell updates.
/// Tracks the bounding box of changed cells and provides reset functionality.
/// </summary>
public struct DirtyRectTracker
{
    private int _minRow;
    private int _maxRow;
    private int _minCol;
    private int _maxCol;
    private bool _hasDirtyCells;

    /// <summary>
    /// Initializes a new instance with default values (no dirty cells).
    /// </summary>
    public DirtyRectTracker()
    {
        Reset();
    }

    /// <summary>
    /// Gets whether any dirty cells have been tracked.
    /// </summary>
    public bool HasDirtyCells => _hasDirtyCells;

    /// <summary>
    /// Gets the minimum dirty row (inclusive).
    /// </summary>
    public int MinRow => _minRow;

    /// <summary>
    /// Gets the maximum dirty row (inclusive).
    /// </summary>
    public int MaxRow => _maxRow;

    /// <summary>
    /// Gets the minimum dirty column (inclusive).
    /// </summary>
    public int MinCol => _minCol;

    /// <summary>
    /// Gets the maximum dirty column (inclusive).
    /// </summary>
    public int MaxCol => _maxCol;

    /// <summary>
    /// Expands the dirty rectangle to include the specified cell region.
    /// </summary>
    public void ExpandToInclude(int startRow, int endRow, int startCol, int endCol)
    {
        if (!_hasDirtyCells)
        {
            _minRow = startRow;
            _maxRow = endRow;
            _minCol = startCol;
            _maxCol = endCol;
            _hasDirtyCells = true;
        }
        else
        {
            if (startRow < _minRow) _minRow = startRow;
            if (endRow > _maxRow) _maxRow = endRow;
            if (startCol < _minCol) _minCol = startCol;
            if (endCol > _maxCol) _maxCol = endCol;
        }
    }

    /// <summary>
    /// Resets the tracker to its initial state (no dirty cells).
    /// </summary>
    public void Reset()
    {
        _hasDirtyCells = false;
        _minRow = int.MaxValue;
        _maxRow = int.MinValue;
        _minCol = int.MaxValue;
        _maxCol = int.MinValue;
    }
}
