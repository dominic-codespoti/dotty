using System;
using System.Text;
using System.Threading;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;

namespace Dotty.Runtime.Selection;

/// <summary>
/// Local text selection of one pane, anchored to content (plan-12 §5.6 option C). The public surface speaks logical
/// rows (0 = top of the live screen, negative = history, -1 the newest history line); internally every point is an
/// absolute row registered with <see cref="TerminalBuffer.SelectionAnchors"/>, so the selection follows its text while
/// output scrolls and across a resize reflow. It is cleared when its oldest row is evicted from history and whenever
/// the buffer's <see cref="TerminalBuffer.CoordinateEpoch"/> moves (CSI 3 J, ED 2 and RIS on the main screen,
/// alternate-screen switch). One service per buffer: the constructor registers its anchors with the buffer.
/// </summary>
/// <remarks>
/// Gestures carry a granularity (<see cref="Mode"/>): the unit selected first (a cell, word or logical line) stays
/// selected while the other end follows the pointer and snaps to the same unit. Mutations run on the UI thread under
/// the buffer lock; <see cref="TryGetRange"/> may be read by the renderer without it.
/// </remarks>
public sealed class TextSelectionService
{
    private const int SelStart = 0;
    private const int SelEnd = 1;
    private const int UnitStart = 2;
    private const int UnitEnd = 3;

    private readonly TerminalBuffer _buffer;
    private readonly SelectionAnchorSet _anchors = new();
    private SelectionMode _mode = SelectionMode.None;
    private bool _preserveTrailingSpaces;
    private bool _activeIsStart;

    public TextSelectionService(TerminalBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _buffer.SelectionAnchors = _anchors;
    }

    /// <summary>Punctuation kept inside a double-clicked word (<c>selection.wordChars</c>).</summary>
    public string WordChars { get; set; } = SelectionOptions.DefaultWordChars;

    public SelectionMode Mode => HasSelection ? _mode : SelectionMode.None;

    /// <summary>True while a selection exists and its content is still held; a stale selection is cleared here.</summary>
    public bool HasSelection
    {
        get
        {
            if (_mode == SelectionMode.None) return false;
            lock (_anchors.Gate)
            {
                if (IsValidLocked()) return true;
            }
            ClearSelection();
            return false;
        }
    }

    /// <summary>Logical row of the fixed end (the start of the unit selected first).</summary>
    public int AnchorRow => LogicalRowOf(UnitStart);
    public int AnchorColumn => ColumnOf(UnitStart);

    /// <summary>Logical row of the end that follows the pointer.</summary>
    public int ActiveRow => LogicalRowOf(ActiveIndex);
    public int ActiveColumn => ColumnOf(ActiveIndex);

    private int ActiveIndex => _mode == SelectionMode.Block ? SelEnd : _activeIsStart ? SelStart : SelEnd;

    /// <summary>Starts a selection at a logical cell; <paramref name="mode"/> picks the unit (cell, word, logical line, block).</summary>
    public void StartSelection(int row, int col, SelectionMode mode = SelectionMode.Character)
    {
        if (mode == SelectionMode.None)
        {
            ClearSelection();
            return;
        }
        using var gate = EnterBuffer();
        if (!gate.Taken) return;
        var (r, c) = ClampLogical(row, col);
        Span<Pos> unit = stackalloc Pos[2];
        ExpandUnit(mode, r, c, unit);
        SetPoints(mode, unit[0], unit[1], unit[0], unit[1]);
        _activeIsStart = false;
    }

    /// <summary>Double click: the word under the cell (see <see cref="SelectionWordRules"/>).</summary>
    public void SelectWord(int row, int col) => StartSelection(row, col, SelectionMode.Word);

    /// <summary>Triple click: the logical line under the row, across soft-wrapped continuation rows.</summary>
    public void SelectLine(int row) => StartSelection(row, 0, SelectionMode.Line);

    /// <summary>
    /// Ctrl+triple click: the OSC 133 zone (prompt, command or output) around the cell, from its mark to the next one.
    /// Without shell-integration marks around the cell it selects the logical line.
    /// </summary>
    public void SelectZone(int row, int col)
    {
        using (var gate = EnterBuffer())
        {
            if (!gate.Taken) return;
            var (r, c) = ClampLogical(row, col);
            if (TryFindZone(_buffer.ToAbsoluteRow(r), c, out Pos start, out Pos end))
            {
                SetPoints(SelectionMode.Character, start, end, start, end);
                _activeIsStart = false;
                return;
            }
        }
        SelectLine(row);
    }

    /// <summary>Select All: every retained history row and the whole screen.</summary>
    public void SelectAll()
    {
        using var gate = EnterBuffer();
        if (!gate.Taken || _buffer.Rows <= 0 || _buffer.Columns <= 0) return;
        var start = new Pos(_buffer.ToAbsoluteRow(-_buffer.ScrollbackCount), 0);
        var end = new Pos(_buffer.ToAbsoluteRow(_buffer.Rows - 1), _buffer.Columns - 1);
        SetPoints(SelectionMode.Character, start, end, start, start);
        _activeIsStart = false;
    }

    /// <summary>Selects a half-open, reflow-aware logical range while retaining exact trailing spaces.</summary>
    public bool StartRangeExclusive(int startRow, int startColumn, int endRow, int endColumn)
    {
        if (endRow < startRow || (endRow == startRow && endColumn <= startColumn))
        {
            ClearSelection();
            return false;
        }
        using var gate = EnterBuffer();
        if (!gate.Taken) return false;
        var start = new Pos(_buffer.ToAbsoluteRow(startRow), Math.Max(0, startColumn));
        var end = ToInclusiveEnd(new Pos(_buffer.ToAbsoluteRow(endRow), endColumn));
        SetPoints(SelectionMode.Character, start, end, start, start);
        _activeIsStart = false;
        _preserveTrailingSpaces = true;
        return true;
    }

    /// <summary>Moves the following end to a logical cell, snapping to the unit of <see cref="Mode"/>.</summary>
    public void UpdateSelection(int row, int col)
    {
        if (!HasSelection) return;
        using var gate = EnterBuffer();
        if (!gate.Taken) return;
        var (r, c) = ClampLogical(row, col);
        MoveActiveLocked(r, c);
    }

    /// <summary>
    /// Shift+click: extends the selection to a logical cell, keeping its granularity. The end nearer the click moves;
    /// the other end becomes the fixed anchor for a following drag. Starts a character selection when none exists.
    /// </summary>
    public void ExtendSelection(int row, int col)
    {
        if (!HasSelection)
        {
            StartSelection(row, col);
            return;
        }
        using var gate = EnterBuffer();
        if (!gate.Taken) return;
        var (r, c) = ClampLogical(row, col);
        var click = new Pos(_buffer.ToAbsoluteRow(r), c);
        lock (_anchors.Gate)
        {
            var pts = _anchors.Points;
            if (_mode == SelectionMode.Block)
            {
                pts[SelEnd] = Point(click, SelectionAnchorEdge.End);
                return;
            }
            var start = Pos.Of(pts[SelStart]);
            var end = Pos.Of(pts[SelEnd]);
            long columns = Math.Max(1, _buffer.Columns);
            bool moveStart = click < start
                || (click <= end && click.Linear(columns) - start.Linear(columns) < end.Linear(columns) - click.Linear(columns));
            var fixedEnd = moveStart ? end : start;
            pts[UnitStart] = Point(fixedEnd, SelectionAnchorEdge.Start);
            pts[UnitEnd] = Point(fixedEnd, SelectionAnchorEdge.End);
        }
        MoveActiveLocked(r, c);
    }

    public void ClearSelection()
    {
        lock (_anchors.Gate)
        {
            _anchors.Count = 0;
            _anchors.Epoch = -1;
            _anchors.PreserveColumns = false;
        }
        _mode = SelectionMode.None;
        _preserveTrailingSpaces = false;
        _activeIsStart = false;
    }

    /// <summary>The selection in logical rows of the live screen, or <see cref="TerminalSelectionRange.Empty"/>.</summary>
    public TerminalSelectionRange GetNormalizedRange() =>
        HasSelection && TryGetRange(_buffer.ViewTopAbsoluteRow, out var range, out _) ? range : TerminalSelectionRange.Empty;

    /// <summary>
    /// The selection relative to a captured frame whose row 0 is absolute row <paramref name="viewTopAbsoluteRow"/>
    /// (<see cref="RenderSnapshot.ViewTopAbsoluteRow"/>); block selections come back as their rectangle. Read-only:
    /// a stale selection reports false and is cleared by the next <see cref="HasSelection"/>.
    /// </summary>
    public bool TryGetRange(long viewTopAbsoluteRow, out TerminalSelectionRange range, out bool block)
    {
        block = _mode == SelectionMode.Block;
        range = TerminalSelectionRange.Empty;
        if (_mode == SelectionMode.None) return false;
        lock (_anchors.Gate)
        {
            if (!IsValidLocked()) return false;
            var a = _anchors.Points[SelStart];
            var b = _anchors.Points[SelEnd];
            if (block)
            {
                range = new TerminalSelectionRange(
                    ToFrameRow(Math.Min(a.Row, b.Row), viewTopAbsoluteRow), Math.Min(a.Column, b.Column),
                    ToFrameRow(Math.Max(a.Row, b.Row), viewTopAbsoluteRow), Math.Max(a.Column, b.Column));
            }
            else
            {
                range = new TerminalSelectionRange(
                    ToFrameRow(a.Row, viewTopAbsoluteRow), a.Column,
                    ToFrameRow(b.Row, viewTopAbsoluteRow), Math.Min(b.Column, Math.Max(0, _buffer.Columns - 1)));
            }
            return true;
        }
    }

    public bool IsCellSelected(int row, int col)
    {
        var range = GetNormalizedRange();
        if (range.IsEmpty) return false;
        if (_mode == SelectionMode.Block)
            return row >= range.StartRow && row <= range.EndRow && col >= range.StartColumn && col <= range.EndColumn;
        return range.Contains(row, col);
    }

    /// <summary>The selected text at call time (content-anchored, so output arriving meanwhile does not shift it).</summary>
    public string GetSelectedText()
    {
        if (!HasSelection) return string.Empty;
        using var gate = EnterBuffer();
        if (!gate.Taken) return string.Empty;
        Pos start, end;
        lock (_anchors.Gate)
        {
            if (!IsValidLocked()) return string.Empty;
            start = Pos.Of(_anchors.Points[SelStart]);
            end = Pos.Of(_anchors.Points[SelEnd]);
        }
        return _mode == SelectionMode.Block ? ExtractBlock(start, end) : ExtractStream(start, end);
    }

    // ---- state ----

    private readonly record struct Pos(long Row, int Col)
    {
        public static Pos Of(SelectionAnchorPoint p) => new(p.Row, p.Column);
        public long Linear(long columns) => Row * columns + Col;
        public static bool operator <(Pos a, Pos b) => a.Row < b.Row || (a.Row == b.Row && a.Col < b.Col);
        public static bool operator >(Pos a, Pos b) => b < a;
        public static bool operator <=(Pos a, Pos b) => !(b < a);
        public static bool operator >=(Pos a, Pos b) => !(a < b);
    }

    private static SelectionAnchorPoint Point(Pos p, SelectionAnchorEdge edge) => new(p.Row, p.Col, edge);

    private bool IsValidLocked()
    {
        if (_anchors.Count == 0 || _anchors.Epoch != _buffer.CoordinateEpoch) return false;
        long top = Math.Min(_anchors.Points[SelStart].Row, _anchors.Points[SelEnd].Row);
        return top >= _buffer.OldestRetainedAbsoluteRow;
    }

    private void SetPoints(SelectionMode mode, Pos start, Pos end, Pos unitStart, Pos unitEnd)
    {
        lock (_anchors.Gate)
        {
            var pts = _anchors.Points;
            pts[SelStart] = Point(start, SelectionAnchorEdge.Start);
            pts[SelEnd] = Point(end, SelectionAnchorEdge.End);
            pts[UnitStart] = Point(unitStart, SelectionAnchorEdge.Start);
            pts[UnitEnd] = Point(unitEnd, SelectionAnchorEdge.End);
            _anchors.Count = SelectionAnchorSet.Capacity;
            _anchors.PreserveColumns = mode == SelectionMode.Block;
            _anchors.Epoch = _buffer.CoordinateEpoch;
        }
        _mode = mode;
        _preserveTrailingSpaces = false;
    }

    private void MoveActiveLocked(int row, int col)
    {
        if (_mode == SelectionMode.Block)
        {
            lock (_anchors.Gate)
                _anchors.Points[SelEnd] = Point(new Pos(_buffer.ToAbsoluteRow(row), col), SelectionAnchorEdge.End);
            return;
        }
        Span<Pos> unit = stackalloc Pos[2];
        ExpandUnit(_mode, row, col, unit);
        lock (_anchors.Gate)
        {
            var pts = _anchors.Points;
            var unitStart = Pos.Of(pts[UnitStart]);
            var unitEnd = Pos.Of(pts[UnitEnd]);
            var pointer = new Pos(_buffer.ToAbsoluteRow(row), col);
            _activeIsStart = pointer < unitStart;
            var start = _activeIsStart ? unit[0] : unitStart;
            var end = _activeIsStart || unit[1] < unitEnd ? unitEnd : unit[1];
            pts[SelStart] = Point(start, SelectionAnchorEdge.Start);
            pts[SelEnd] = Point(end, SelectionAnchorEdge.End);
        }
        _preserveTrailingSpaces = false;
    }

    private int LogicalRowOf(int index)
    {
        lock (_anchors.Gate)
            return _anchors.Count == 0 ? 0 : _buffer.ToLogicalRow(_anchors.Points[index].Row);
    }

    private int ColumnOf(int index)
    {
        lock (_anchors.Gate)
            return _anchors.Count == 0 ? 0 : _anchors.Points[index].Column;
    }

    private static int ToFrameRow(long absoluteRow, long viewTop) =>
        (int)Math.Clamp(absoluteRow - viewTop, int.MinValue / 2, int.MaxValue / 2);

    /// <summary>Holds the buffer lock for a gesture; <see cref="Taken"/> is false when the wait budget ran out.</summary>
    private readonly struct BufferGate(object? root) : IDisposable
    {
        public bool Taken => root is not null;

        public void Dispose()
        {
            if (root is not null) Monitor.Exit(root);
        }
    }

    /// <summary>
    /// Takes the buffer lock with the user-operation budget of <see cref="TerminalBuffer.WithSyncRoot"/> without
    /// allocating a closure (clicks and drags run per pointer event). When the parser holds the lock past the budget
    /// the gesture is dropped, not half-applied.
    /// </summary>
    private BufferGate EnterBuffer(int timeoutMs = 500)
    {
        object root = _buffer.SyncRoot;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            bool taken = false;
            Monitor.TryEnter(root, 32, ref taken);
            if (taken) return new BufferGate(root);
            if (Environment.TickCount64 >= deadline) return default;
            Thread.Sleep(1);
        }
    }

    private (int Row, int Col) ClampLogical(int row, int col)
    {
        int minRow = -_buffer.ScrollbackCount;
        int maxRow = Math.Max(0, _buffer.Rows - 1);
        return (Math.Clamp(row, minRow, maxRow), Math.Clamp(col, 0, Math.Max(0, _buffer.Columns - 1)));
    }

    /// <summary>Exclusive end → inclusive end (the cell before), wrapping to the previous row's last column.</summary>
    private Pos ToInclusiveEnd(Pos exclusive) =>
        exclusive.Col > 0
            ? new Pos(exclusive.Row, Math.Min(exclusive.Col, _buffer.Columns) - 1)
            : new Pos(exclusive.Row - 1, Math.Max(0, _buffer.Columns - 1));

    // ---- units (buffer lock held) ----

    private void ExpandUnit(SelectionMode mode, int row, int col, Span<Pos> unit)
    {
        switch (mode)
        {
            case SelectionMode.Word:
                FindWord(row, col, out int sr, out int sc, out int er, out int ec);
                unit[0] = new Pos(_buffer.ToAbsoluteRow(sr), sc);
                unit[1] = new Pos(_buffer.ToAbsoluteRow(er), ec);
                break;
            case SelectionMode.Line:
                FindLogicalLine(row, out int first, out int last);
                unit[0] = new Pos(_buffer.ToAbsoluteRow(first), 0);
                unit[1] = new Pos(_buffer.ToAbsoluteRow(last), Math.Max(0, _buffer.Columns - 1));
                break;
            default:
                unit[0] = unit[1] = new Pos(_buffer.ToAbsoluteRow(row), col);
                break;
        }
    }

    private void FindLogicalLine(int row, out int first, out int last)
    {
        int minRow = -_buffer.ScrollbackCount;
        first = row;
        while (first > minRow && _buffer.LogicalRowContinuesPrevious(first)) first--;
        last = row;
        while (last + 1 < _buffer.Rows && _buffer.LogicalRowContinuesPrevious(last + 1)) last++;
    }

    private void FindWord(int row, int col, out int startRow, out int startCol, out int endRow, out int endCol)
    {
        int columns = _buffer.Columns;
        int minRow = -_buffer.ScrollbackCount;
        SelectionCharClass kind = ClassAt(row, col);
        startRow = endRow = row;
        startCol = endCol = col;

        if (kind == SelectionCharClass.Whitespace)
        {
            // Blanks stay on the row and never run past the row's content to the right of the click.
            while (startCol > 0 && ClassAt(row, startCol - 1) == kind) startCol--;
            int scanEnd = Math.Max(col, _buffer.GetLogicalRowEnd(row));
            while (endCol < scanEnd && ClassAt(row, endCol + 1) == kind) endCol++;
            return;
        }

        // Words follow soft wraps in both directions.
        while (true)
        {
            if (startCol > 0)
            {
                if (ClassAt(startRow, startCol - 1) != kind) break;
                startCol--;
            }
            else if (startRow > minRow && _buffer.LogicalRowContinuesPrevious(startRow)
                     && ClassAt(startRow - 1, columns - 1) == kind)
            {
                startRow--;
                startCol = columns - 1;
            }
            else break;
        }
        if (startCol > 0 && IsContinuation(startRow, startCol)) startCol--;

        while (true)
        {
            if (endCol < columns - 1)
            {
                if (ClassAt(endRow, endCol + 1) != kind) break;
                endCol++;
            }
            else if (endRow + 1 < _buffer.Rows && _buffer.LogicalRowContinuesPrevious(endRow + 1)
                     && ClassAt(endRow + 1, 0) == kind)
            {
                endRow++;
                endCol = 0;
            }
            else break;
        }
    }

    private SelectionCharClass ClassAt(int row, int col)
    {
        if (!_buffer.TryGetLogicalRowCells(row, out var hot, out var cold) || col < 0 || col >= hot.Length)
            return SelectionCharClass.Whitespace;
        if (hot[col].IsContinuation && col > 0) col--;
        string? text = GraphemeHelper.Resolve(hot[col].Rune, cold[col].GraphemeIndex);
        return SelectionWordRules.Classify(text.AsSpan(), WordChars.AsSpan());
    }

    private bool IsContinuation(int row, int col) =>
        _buffer.TryGetLogicalRowCells(row, out var hot, out _) && col < hot.Length && hot[col].IsContinuation;

    private bool TryFindZone(long absoluteRow, int column, out Pos start, out Pos end)
    {
        start = end = default;
        var marks = _buffer.GetPromptMarks();
        var click = new Pos(absoluteRow, column);
        int index = -1;
        for (int i = marks.Count - 1; i >= 0; i--)
        {
            if (new Pos(marks[i].AbsoluteRow, marks[i].AbsoluteColumn) <= click)
            {
                index = i;
                break;
            }
        }
        if (index < 0) return false;

        start = new Pos(marks[index].AbsoluteRow, marks[index].AbsoluteColumn);
        if (index + 1 < marks.Count)
        {
            end = ToInclusiveEnd(new Pos(marks[index + 1].AbsoluteRow, marks[index + 1].AbsoluteColumn));
        }
        else
        {
            // The open zone runs to the last row with content.
            int lastRow = _buffer.ToLogicalRow(absoluteRow);
            for (int r = _buffer.Rows - 1; r > lastRow; r--)
            {
                if (_buffer.GetLogicalRowEnd(r) >= 0)
                {
                    lastRow = r;
                    break;
                }
            }
            end = new Pos(_buffer.ToAbsoluteRow(lastRow), Math.Max(0, _buffer.Columns - 1));
        }
        return start <= end && start.Row >= _buffer.OldestRetainedAbsoluteRow;
    }

    // ---- extraction (buffer lock held) ----

    private string ExtractStream(Pos start, Pos end)
    {
        var sb = new StringBuilder();
        int first = _buffer.ToLogicalRow(start.Row);
        int last = _buffer.ToLogicalRow(end.Row);
        int lineStart = 0;
        for (int row = first; row <= last; row++)
        {
            int startCol = row == first ? start.Col : 0;
            int endCol = row == last ? end.Col : _buffer.Columns - 1;
            AppendCells(row, startCol, Math.Min(endCol, _buffer.GetLogicalRowEnd(row)), sb, continuationAsSpace: false);
            bool joinsNext = row < last && _buffer.LogicalRowContinuesPrevious(row + 1);
            if (!_preserveTrailingSpaces && !joinsNext) TrimTrailingSpaces(sb, lineStart);
            if (row < last && !joinsNext)
            {
                sb.Append(Environment.NewLine);
                lineStart = sb.Length;
            }
        }
        return sb.ToString();
    }

    private string ExtractBlock(Pos a, Pos b)
    {
        var sb = new StringBuilder();
        int first = _buffer.ToLogicalRow(Math.Min(a.Row, b.Row));
        int last = _buffer.ToLogicalRow(Math.Max(a.Row, b.Row));
        int minCol = Math.Min(a.Col, b.Col);
        int maxCol = Math.Min(Math.Max(a.Col, b.Col), _buffer.Columns - 1);
        for (int row = first; row <= last; row++)
        {
            int lineStart = sb.Length;
            AppendCells(row, minCol, maxCol, sb, continuationAsSpace: true);
            if (!_preserveTrailingSpaces) TrimTrailingSpaces(sb, lineStart);
            if (row < last) sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends the clusters of columns [<paramref name="startCol"/>, <paramref name="endCol"/>] of a logical row. A wide
    /// glyph is emitted once by its lead cell; a continuation cell at the left edge of the range becomes a space when
    /// <paramref name="continuationAsSpace"/> (a block cut through a wide glyph keeps the column).
    /// </summary>
    private void AppendCells(int row, int startCol, int endCol, StringBuilder destination, bool continuationAsSpace)
    {
        _buffer.TryGetLogicalRowCells(row, out var hot, out var cold);
        for (int col = Math.Max(0, startCol); col <= endCol; col++)
        {
            if (col >= hot.Length)
            {
                destination.Append(' ');
                continue;
            }
            if (hot[col].IsContinuation)
            {
                if (continuationAsSpace && col == startCol) destination.Append(' ');
                continue;
            }
            destination.Append(GraphemeHelper.Resolve(hot[col].Rune, cold[col].GraphemeIndex) ?? " ");
        }
    }

    private static void TrimTrailingSpaces(StringBuilder text, int floor)
    {
        while (text.Length > floor && text[^1] == ' ')
            text.Length--;
    }
}
