namespace Dotty.Terminal.Adapter;

internal readonly record struct ReflowCell(CellHot Hot, ColdCell Cold, int Width);

internal readonly record struct ReflowCursorAnchor(
    int LogicalLine,
    int CellOffset,
    bool WrapPending = false)
{
    public int LogicalLineId => LogicalLine;
    public int CellUnitOffset => CellOffset;
}

internal readonly record struct ReflowPosition(
    int Row,
    int Column,
    bool WrapPending,
    bool InScrollback,
    int OutputIndex)
{
    public int Col => Column;
}

internal sealed class ReflowMapping
{
    private readonly Dictionary<(int LogicalLine, int CellOffset), ReflowPosition> _positions = new();
    private readonly Dictionary<int, int> _lineLengths = new();
    public int NewScrollbackRows { get; internal set; }
    public int RetainedStart { get; internal set; }
    public int NewRows { get; internal set; }
    public int NewColumns { get; internal set; }
    public int Row { get; internal set; } = -1;
    public int Column { get; internal set; } = -1;
    public int Col => Column;
    public bool WrapPending { get; internal set; }
    public bool InScrollback { get; internal set; }
    public bool IsMapped { get; internal set; }


    internal void Reset(int rows, int columns)
    {
        _positions.Clear();
        _lineLengths.Clear();
        NewScrollbackRows = 0;
        RetainedStart = 0;
        NewRows = rows;
        NewColumns = columns;
        Row = -1;
        Column = -1;
        WrapPending = false;
        InScrollback = false;
        IsMapped = false;
    }

    internal void EnsureCapacity(int positionCount, int lineCount)
    {
        _positions.EnsureCapacity(positionCount);
        _lineLengths.EnsureCapacity(lineCount);
    }
    internal void SetLineLength(int logicalLine, int length) => _lineLengths[logicalLine] = length;

    internal void Add(int logicalLine, int cellOffset, ReflowPosition position) =>
        _positions[(logicalLine, cellOffset)] = position;

    internal bool TryMap(ReflowCursorAnchor anchor, out ReflowPosition position)
    {
        int offset = Math.Max(0, anchor.CellOffset);
        if (anchor.WrapPending && _lineLengths.TryGetValue(anchor.LogicalLine, out int length) && length > 0)
            offset = Math.Min(offset, length - 1);

        if (TryMapOffset(anchor.LogicalLine, offset, anchor.WrapPending, out position))
            return true;

        if (!_lineLengths.TryGetValue(anchor.LogicalLine, out length))
        {
            position = default;
            return false;
        }

        for (int candidate = Math.Min(offset, length); candidate >= 0; candidate--)
        {
            if (TryMapOffset(anchor.LogicalLine, candidate, anchor.WrapPending, out position))
                return true;
        }

        position = default;
        return false;
    }

    private bool TryMapOffset(int logicalLine, int offset, bool wrapPending, out ReflowPosition position)
    {
        if (!_positions.TryGetValue((logicalLine, offset), out var stored))
        {
            position = default;
            return false;
        }

        int retainedIndex = stored.OutputIndex - RetainedStart;
        int retainedCount = NewScrollbackRows + NewRows;
        if (retainedIndex < 0 || retainedIndex >= retainedCount)
        {
            position = default;
            return false;
        }

        bool inScrollback = retainedIndex < NewScrollbackRows;
        int row = inScrollback ? -1 : retainedIndex - NewScrollbackRows;
        position = stored with
        {
            Row = row,
            WrapPending = wrapPending || stored.WrapPending,
            InScrollback = inScrollback,
        };
        return true;
    }
}
