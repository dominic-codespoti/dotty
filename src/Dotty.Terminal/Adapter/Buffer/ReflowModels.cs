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

/// <summary>
/// Where the few positions that matter (the cursor, saved cursors, prompt marks) land after a reflow. The reflow
/// resolves only the anchors registered here, before it runs, and never keeps a per-cell table: a reader registers
/// each anchor with <see cref="AddAnchor"/> (which returns its slot), the reflow resolves every slot, and
/// <see cref="TryMap(int, out ReflowPosition)"/> then reads it back by slot. The arrays are pooled, so a steady
/// resize sequence allocates nothing.
/// </summary>
internal sealed class ReflowMapping
{
    private ReflowCursorAnchor[] _anchors = Array.Empty<ReflowCursorAnchor>();
    private ReflowPosition[] _positions = Array.Empty<ReflowPosition>();
    private bool[] _mapped = Array.Empty<bool>();
    private int _anchorCount;

    public int NewScrollbackRows { get; internal set; }
    public int RetainedStart { get; internal set; }
    public int NewRows { get; internal set; }
    public int NewColumns { get; internal set; }

    /// <summary>Number of registered anchors (slots <c>0 .. AnchorCount - 1</c>).</summary>
    internal int AnchorCount => _anchorCount;

    /// <summary>Forgets every registered anchor and makes room for <paramref name="capacity"/> of them.</summary>
    internal void BeginAnchors(int capacity)
    {
        _anchorCount = 0;
        if (capacity > _anchors.Length)
            Grow(capacity);
    }

    /// <summary>Registers an anchor to be resolved by the next reflow and returns its slot.</summary>
    internal int AddAnchor(ReflowCursorAnchor anchor)
    {
        if (_anchorCount == _anchors.Length)
            Grow(Math.Max(4, _anchorCount * 2));

        _anchors[_anchorCount] = anchor;
        _mapped[_anchorCount] = false;
        return _anchorCount++;
    }

    internal ReflowCursorAnchor GetAnchor(int slot) => _anchors[slot];

    /// <summary>Records the resolved position of <paramref name="slot"/>; called by the reflow.</summary>
    internal void SetPosition(int slot, in ReflowPosition position)
    {
        _positions[slot] = position;
        _mapped[slot] = true;
    }

    /// <summary>
    /// The position the anchor registered as <paramref name="slot"/> landed on, or false when its logical line is
    /// unknown or every output row it could land on was trimmed out of the retained window.
    /// </summary>
    internal bool TryMap(int slot, out ReflowPosition position)
    {
        if ((uint)slot >= (uint)_anchorCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        if (!_mapped[slot])
        {
            position = default;
            return false;
        }

        position = _positions[slot];
        return true;
    }

    /// <summary>Starts a reflow into a <paramref name="rows"/> x <paramref name="columns"/> screen; keeps the registered anchors.</summary>
    internal void Reset(int rows, int columns)
    {
        NewScrollbackRows = 0;
        RetainedStart = 0;
        NewRows = rows;
        NewColumns = columns;
        Array.Clear(_mapped, 0, _anchorCount);
    }

    private void Grow(int capacity)
    {
        Array.Resize(ref _anchors, capacity);
        Array.Resize(ref _positions, capacity);
        Array.Resize(ref _mapped, capacity);
    }
}
