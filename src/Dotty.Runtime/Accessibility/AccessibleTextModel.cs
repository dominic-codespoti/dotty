using System;
using System.Collections.Generic;
using Dotty.Runtime.Selection;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Accessibility;

/// <summary>One grid cluster of an accessible line: its first column, cell width and UTF-16 span in the document.</summary>
public readonly record struct AccessibleCluster(int Column, int Width, int Offset, int Length);

/// <summary>Visible-grid cell range, both ends inclusive, in reading order.</summary>
public readonly record struct AccessibleCellRange(int StartRow, int StartColumn, int EndRow, int EndColumn);

/// <summary>Text navigation unit for <see cref="AccessibleTextModel.Move"/>.</summary>
public enum AccessibleTextUnit
{
    Character,
    Word,
    Line,
}

/// <summary>What changed since the last delivered <see cref="AccessibleTextChange"/>.</summary>
[Flags]
public enum AccessibleChangeKind
{
    None = 0,
    Text = 1,
    Caret = 2,
    Selection = 4,
}

/// <summary>A coalesced change notification: kinds plus the inclusive range of rows whose text changed (-1 when none).</summary>
public readonly record struct AccessibleTextChange(AccessibleChangeKind Kinds, int FirstRow, int LastRow)
{
    public AccessibleTextChange Merge(AccessibleTextChange other)
    {
        if (Kinds == AccessibleChangeKind.None) return other;
        if (other.Kinds == AccessibleChangeKind.None) return this;
        int first = FirstRow < 0 ? other.FirstRow : other.FirstRow < 0 ? FirstRow : Math.Min(FirstRow, other.FirstRow);
        int last = Math.Max(LastRow, other.LastRow);
        return new AccessibleTextChange(Kinds | other.Kinds, first, last);
    }
}

/// <summary>
/// Portable, headless text model for screen readers: the visible rows of a published <see cref="RenderSnapshot"/>
/// as lines with trailing blanks trimmed, joined by <c>\n</c>; a caret and selection mirrored as UTF-16 offsets into
/// that document; boundary moves by character (grid cluster), word (<see cref="SelectionWordRules"/>) and line.
/// It never touches the live buffer or its lock; platform bridges (UIA, AT-SPI, NSAccessibility) sit on top.
/// </summary>
public sealed class AccessibleTextModel
{
    private string[] _lines = [];
    private AccessibleCluster[][] _clusters = [];
    private int[] _lineStarts = [];
    private string _text = string.Empty;
    private readonly AccessibleChangeCoalescer _coalescer;

    /// <param name="coalescer">Burst coalescer for change events; a 50 ms window on the system clock when null.</param>
    public AccessibleTextModel(AccessibleChangeCoalescer? coalescer = null)
    {
        _coalescer = coalescer ?? new AccessibleChangeCoalescer(static () => Environment.TickCount64, 50);
    }

    /// <summary>Raised from <see cref="Poll"/> with the changes coalesced since the previous event.</summary>
    public event Action<AccessibleTextChange>? Changed;

    /// <summary>Punctuation kept inside words (<c>selection.wordChars</c>).</summary>
    public string WordChars { get; set; } = SelectionOptions.DefaultWordChars;

    /// <summary>Visible lines, trailing blanks trimmed.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The document: <see cref="Lines"/> joined with <c>\n</c>.</summary>
    public string Text => _text;

    /// <summary>Caret UTF-16 offset into <see cref="Text"/>.</summary>
    public int CaretOffset { get; private set; }

    /// <summary>Selection as [start, end) UTF-16 offsets, or null.</summary>
    public (int Start, int End)? Selection { get; private set; }

    /// <summary>Clusters of one line (continuation cells and trimmed blanks omitted).</summary>
    public IReadOnlyList<AccessibleCluster> GetClusters(int row) => _clusters[row];

    /// <summary>
    /// Mirrors a published snapshot: visible text, the cursor cell as the caret, and the optional visible-grid
    /// selection. Differences against the previous frame are queued in the coalescer.
    /// </summary>
    public void Update(RenderSnapshot snapshot, AccessibleCellRange? selection = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int rows = Math.Max(0, snapshot.Rows);
        var lines = new string[rows];
        var clusters = new AccessibleCluster[rows][];
        for (int row = 0; row < rows; row++)
            (lines[row], clusters[row]) = BuildLine(snapshot.GetRowCells(row), snapshot.GetRowColdCells(row));
        Apply(lines, clusters, snapshot.CursorRow, snapshot.CursorCol, selection);
    }

    private void Apply(string[] lines, AccessibleCluster[][] clusters, int cursorRow, int cursorColumn, AccessibleCellRange? selection)
    {
        int first = -1, last = -1;
        int common = Math.Max(lines.Length, _lines.Length);
        for (int row = 0; row < common; row++)
        {
            string? before = row < _lines.Length ? _lines[row] : null;
            string? after = row < lines.Length ? lines[row] : null;
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                if (first < 0) first = row;
                last = row;
            }
        }

        _lines = lines;
        var starts = new int[lines.Length];
        int offset = 0;
        for (int row = 0; row < lines.Length; row++)
        {
            starts[row] = offset;
            offset += lines[row].Length + 1;
            var rowClusters = clusters[row];
            for (int i = 0; i < rowClusters.Length; i++)
                rowClusters[i] = rowClusters[i] with { Offset = rowClusters[i].Offset + starts[row] };
        }
        _clusters = clusters;
        _lineStarts = starts;
        if (first >= 0)
            _text = string.Join('\n', lines);

        var kinds = first >= 0 ? AccessibleChangeKind.Text : AccessibleChangeKind.None;
        int caret = lines.Length == 0 ? 0 : OffsetFromCell(cursorRow, cursorColumn);
        if (caret != CaretOffset)
        {
            CaretOffset = caret;
            kinds |= AccessibleChangeKind.Caret;
        }
        (int, int)? mirrored = selection is { } s && lines.Length > 0
            ? Order(OffsetFromCell(s.StartRow, s.StartColumn), EndOffsetFromCell(s.EndRow, s.EndColumn))
            : null;
        if (mirrored != Selection)
        {
            Selection = mirrored;
            kinds |= AccessibleChangeKind.Selection;
        }
        if (kinds != AccessibleChangeKind.None)
            _coalescer.Report(new AccessibleTextChange(kinds, first, last));
    }

    private static (int, int) Order(int a, int b) => a <= b ? (a, b) : (b, a);

    /// <summary>Delivers a coalesced <see cref="Changed"/> event when the coalescer's window has elapsed.</summary>
    public bool Poll()
    {
        if (!_coalescer.TryFlush(out var change))
            return false;
        Changed?.Invoke(change);
        return true;
    }

    /// <summary>
    /// Document offset of the cluster covering (row, column); columns past the trimmed text map to the line end.
    /// Rows and columns are clamped to the grid.
    /// </summary>
    public int OffsetFromCell(int row, int column)
    {
        if (_lines.Length == 0) return 0;
        row = Math.Clamp(row, 0, _lines.Length - 1);
        foreach (var cluster in _clusters[row])
            if (column < cluster.Column + cluster.Width)
                return cluster.Offset;
        return _lineStarts[row] + _lines[row].Length;
    }

    private int EndOffsetFromCell(int row, int column)
    {
        if (_lines.Length == 0) return 0;
        row = Math.Clamp(row, 0, _lines.Length - 1);
        foreach (var cluster in _clusters[row])
            if (column < cluster.Column + cluster.Width)
                return cluster.Offset + cluster.Length;
        return _lineStarts[row] + _lines[row].Length;
    }

    /// <summary>
    /// (row, column) of <paramref name="offset"/>: the cluster containing it, or the column after the trimmed text
    /// at a line end. Offsets are clamped to the document.
    /// </summary>
    public (int Row, int Column) CellFromOffset(int offset)
    {
        if (_lines.Length == 0) return (0, 0);
        offset = Math.Clamp(offset, 0, _text.Length);
        int row = Array.BinarySearch(_lineStarts, offset);
        if (row < 0) row = ~row - 1;
        foreach (var cluster in _clusters[row])
            if (offset < cluster.Offset + cluster.Length)
                return (row, cluster.Column);
        var clusters = _clusters[row];
        return (row, clusters.Length == 0 ? 0 : clusters[^1].Column + clusters[^1].Width);
    }

    /// <summary>
    /// Moves <paramref name="offset"/> by <paramref name="count"/> units (negative goes back), stopping at the
    /// document ends. Word moves land on word starts going either way; line moves keep the column.
    /// </summary>
    public int Move(int offset, AccessibleTextUnit unit, int count)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        int step = Math.Sign(count);
        for (int i = 0; i < Math.Abs(count); i++)
        {
            offset = unit switch
            {
                AccessibleTextUnit.Character => MoveCharacter(offset, step),
                AccessibleTextUnit.Word => MoveWord(offset, step),
                _ => MoveLine(offset, step),
            };
        }
        return offset;
    }

    // Stops: every cluster start, every line end ('\n' position) and the document end.
    private int MoveCharacter(int offset, int step)
    {
        if (step > 0)
        {
            if (offset >= _text.Length) return _text.Length;
            var (start, length) = UnitAt(offset);
            return start + length;
        }
        if (offset <= 0) return 0;
        return UnitAt(offset - 1).Start;
    }

    private int MoveWord(int offset, int step)
    {
        if (step > 0)
        {
            if (offset >= _text.Length) return _text.Length;
            var cls = ClassAt(offset);
            int pos = offset;
            while (pos < _text.Length && cls != SelectionCharClass.Whitespace && ClassAt(pos) == cls)
                pos = MoveCharacter(pos, 1);
            while (pos < _text.Length && ClassAt(pos) == SelectionCharClass.Whitespace)
                pos = MoveCharacter(pos, 1);
            return pos;
        }
        if (offset <= 0) return 0;
        int back = offset;
        while (back > 0 && ClassAt(MoveCharacter(back, -1)) == SelectionCharClass.Whitespace)
            back = MoveCharacter(back, -1);
        if (back == 0) return 0;
        var runClass = ClassAt(MoveCharacter(back, -1));
        while (back > 0 && ClassAt(MoveCharacter(back, -1)) == runClass)
            back = MoveCharacter(back, -1);
        return back;
    }

    private int MoveLine(int offset, int step)
    {
        var (row, column) = CellFromOffset(offset);
        int target = row + step;
        if (target < 0) return 0;
        if (target >= _lines.Length) return _text.Length;
        return OffsetFromCell(target, column);
    }

    // The cluster (or line break) containing offset.
    private (int Start, int Length) UnitAt(int offset)
    {
        int row = Array.BinarySearch(_lineStarts, offset);
        if (row < 0) row = ~row - 1;
        foreach (var cluster in _clusters[row])
            if (offset < cluster.Offset + cluster.Length)
                return (cluster.Offset, cluster.Length);
        return (offset, 1); // the '\n' after the line
    }

    private SelectionCharClass ClassAt(int offset)
    {
        var (start, length) = UnitAt(offset);
        if (start >= _text.Length || _text[start] == '\n') return SelectionCharClass.Whitespace;
        return SelectionWordRules.Classify(_text.AsSpan(start, length), WordChars);
    }

    private static (string Text, AccessibleCluster[] Clusters) BuildLine(ReadOnlySpan<CellHot> hot, ReadOnlySpan<ColdCell> cold)
    {
        var sb = new ValueStringBuilder(stackalloc char[256]);
        try
        {
            var clusters = new List<AccessibleCluster>(hot.Length);
            int trimmedLength = 0, trimmedClusters = 0;
            for (int col = 0; col < hot.Length; col++)
            {
                ref readonly var cell = ref hot[col];
                if (cell.IsContinuation) continue;
                int start = sb.Length;
                int written = RowText.AppendCell(ref sb, cell, col < cold.Length ? cold[col].GraphemeIndex : (short)-1);
                int width = Math.Max(1, (int)cell.Width);
                clusters.Add(new AccessibleCluster(col, width, start, written));
                if (!sb.AsSpan().Slice(start, written).IsWhiteSpace())
                {
                    trimmedLength = sb.Length;
                    trimmedClusters = clusters.Count;
                }
            }
            string text = sb.AsSpan()[..trimmedLength].ToString();
            return (text, clusters.GetRange(0, trimmedClusters).ToArray());
        }
        finally
        {
            sb.Dispose();
        }
    }
}

/// <summary>
/// Coalesces bursts of accessibility changes: reports merge until <c>windowMs</c> has passed since the first
/// pending one, so a screen reader hears one update per burst. Clock injected for tests.
/// </summary>
public sealed class AccessibleChangeCoalescer
{
    private readonly Func<long> _clockMs;
    private readonly int _windowMs;
    private AccessibleTextChange _pending;
    private long _firstPendingMs;

    public AccessibleChangeCoalescer(Func<long> clockMs, int windowMs)
    {
        _clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
        _windowMs = Math.Max(0, windowMs);
    }

    public bool HasPending => _pending.Kinds != AccessibleChangeKind.None;

    public void Report(AccessibleTextChange change)
    {
        if (change.Kinds == AccessibleChangeKind.None) return;
        if (!HasPending) _firstPendingMs = _clockMs();
        _pending = _pending.Merge(change);
    }

    /// <summary>The merged change once the window since the first pending report has elapsed.</summary>
    public bool TryFlush(out AccessibleTextChange change)
    {
        change = default;
        if (!HasPending || _clockMs() - _firstPendingMs < _windowMs) return false;
        change = _pending;
        _pending = default;
        return true;
    }
}
