using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Dotty.Terminal.Adapter.Buffer;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Very small screen model for now: stores visible lines and a simple scrollback.
/// Designed to be called from parser callbacks; it is not thread-safe by itself.
/// </summary>
public class TerminalBuffer : IRenderSource
{
    public object SyncRoot { get; } = new object();

    /// <summary>
    /// Set by the renderer around its bounded <c>TryEnter</c> so the PTY
    /// writer can yield between sub-chunks, giving the UI thread a window to
    /// acquire the lock under sustained output. Volatile hint only; the writer
    /// checks it at sub-chunk boundaries and merely yields — correctness does
    /// not depend on it.
    /// </summary>
    public volatile bool ReaderWaiting;

    /// <summary>
    /// Executes <paramref name="action"/> under <see cref="SyncRoot"/> with a
    /// bounded retry window sized for user-initiated operations (copy, search,
    /// accessibility). Unlike the renderer's single 4 ms <c>TryEnter</c>, this
    /// retries until the PTY consumer releases between chunks, so it always
    /// completes unless the wait budget is exhausted.
    /// </summary>
    public void WithSyncRoot(Action action, int timeoutMs = 500)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            bool taken = false;
            try
            {
                Monitor.TryEnter(SyncRoot, 32, ref taken);
                if (taken)
                {
                    action();
                    return;
                }
            }
            finally
            {
                if (taken) Monitor.Exit(SyncRoot);
            }

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("Timed out waiting for the terminal buffer lock.");
            Thread.Sleep(1);
        }
    }

    public Screen ActiveBuffer => _screens.Active;

    internal Screen ActiveScreenForTests => ActiveBuffer;

    public StyleSet StyleSet { get; } = new();

    private readonly ScreenManager _screens;
    private CursorController _cursor = new();
    private readonly BufferEraser _eraser = new();
    private BufferTextWriter _writer;
    private int _scrollTop = 0;
    private int _scrollBottom;
    private bool _originMode;
    private bool _isAlternate;
    private int _savedTotalScrolled = 0;
    private bool _hasAlternateSavedCursor = false;
    private int _alternateSavedCursorRow;
    private int _alternateSavedCursorCol;
    private bool _alternateSavedCursorVisible;

    private ulong[] _rowGenerations = Array.Empty<ulong>();
    private ulong _globalGeneration;
    /// <summary>
    /// Monotonic identity generation for diagnostic correlation.
    /// Read it while holding <see cref="SyncRoot"/> when a consistent frame value is required.
    /// </summary>
    public ulong Generation => _globalGeneration;

    // Motion epochs: travel with content across scrolls (rotated like the
    // Screen ring) and are bumped only for rows whose content actually changed
    // in place (writes, erases) or was newly exposed by a scroll. The renderer
    // mirrors this array and re-renders only rows where bufferEpoch != mirror,
    // i.e. rows whose pixels were NOT already moved into place by a memmove.
    // Distinct from _rowGenerations (identity; bumped on every content change)
    // so the composer's per-row classification cache can key on identity
    // without false hits after a scroll rotation.
    private ulong[] _rowScrollEpochs = Array.Empty<ulong>();

    private List<string> _hyperlinks = new List<string> { string.Empty };
    private Dictionary<string, ushort> _hyperlinkLookup = new Dictionary<string, ushort>();

    public TerminalBuffer(int rows = 24, int columns = 80, int scrollbackCapacity = 10000)
    {
        Rows = rows;
        Columns = columns;
        _rowGenerations = new ulong[rows];
        _rowScrollEpochs = new ulong[rows];
        _screens = new ScreenManager(rows, columns, scrollbackCapacity);
        _writer = CreateWriter();
        _scrollBottom = rows - 1;
        InitializeTabStops();
    }

    public void Resize(int rows, int cols)
    {
        bool fullScreenScroll = (_scrollTop == 0 && _scrollBottom == Rows - 1);

        Rows = rows;
        Columns = cols;
        _screens.Resize(rows, cols);
        _cursor.SetSize(rows, cols);

        if (fullScreenScroll)
        {
            _scrollBottom = rows - 1;
        }
        else
        {
            _scrollBottom = Math.Min(_scrollBottom, rows - 1);
        }
        _scrollTop = Math.Min(_scrollTop, _scrollBottom);

        Array.Resize(ref _rowGenerations, rows);
        Array.Resize(ref _rowScrollEpochs, rows);
        unchecked { _globalGeneration++; }
    }

    public void SetAlternateScreen(bool active)
    {
        if (active == _isAlternate)
            return;

        if (active)
        {
            _hasAlternateSavedCursor = true;
            _alternateSavedCursorRow = _cursor.Row;
            _alternateSavedCursorCol = _cursor.Col;
            _alternateSavedCursorVisible = _cursor.Visible;

            // Save main screen scrollback count and reset for alt screen.
            // Alt screen has no scrollback, so any LineFeed/ScrollUpLines
            // calls while in alt mode must not corrupt the main screen count.
            _savedTotalScrolled = _totalScrolled;
            _totalScrolled = 0;
        }

        _screens.SetAlternate(active);
        _isAlternate = active;

        if (!active)
        {
            // Restore main screen scrollback count and cursor, discarding
            // whatever accumulated while the alt screen was active.
            _totalScrolled = _savedTotalScrolled;
            if (_hasAlternateSavedCursor)
            {
                _cursor.Set(Math.Clamp(_alternateSavedCursorRow, 0, Rows - 1),
                    Math.Clamp(_alternateSavedCursorCol, 0, Columns - 1), Rows, Columns);
                _cursor.SetVisible(_alternateSavedCursorVisible);
                _hasAlternateSavedCursor = false;
            }
        }

        MarkAllRowsDirty();
    }

    public bool IsAlternateScreenActive => _isAlternate;

    public void SetScrollRegion(int top1Based, int bottom1Based)
    {
        int newTop = Math.Max(0, top1Based - 1);
        int newBottom = Math.Clamp(bottom1Based - 1, 0, Rows - 1);
        if (newTop < newBottom)
        {
            _scrollTop = newTop;
            _scrollBottom = newBottom;
        }
        else
        {
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
        }

        // DECSTBM homes the cursor after setting margins.
        _cursor.Set(_originMode ? _scrollTop : 0, 0, Rows, Columns);
    }

    public void SetCursorVisible(bool visible)
    {
        _cursor.SetVisible(visible);
    }

    public ushort GetOrCreateHyperlinkId(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return 0;
        }
        // O(1) lookup using dictionary
        if (_hyperlinkLookup.TryGetValue(uri, out ushort id))
        {
            return id;
        }
        // Not found - add new entry
        ushort idx = (ushort)_hyperlinks.Count;
        _hyperlinks.Add(uri);
        _hyperlinkLookup[uri] = idx;
        return idx;
    }

    /// <summary>
    /// Gets the hyperlink URL for a given hyperlink ID.
    /// Returns null if the ID is invalid or not found.
    /// </summary>
    public string? GetHyperlinkUrl(ushort hyperlinkId)
    {
        if (hyperlinkId == 0 || hyperlinkId >= _hyperlinks.Count)
        {
            return null;
        }
        return _hyperlinks[hyperlinkId];
    }

    private int _totalScrolled = 0;
    private readonly List<PromptMark> _promptMarks = new();
    private bool[]? _tabStops;
    internal bool _autoWrap = true; // DECAWM default is enabled
    private bool _bracketedPaste = false;

    public int CursorRow => _cursor.Row;
    public int CursorCol => _cursor.Col;
    public bool CursorVisible => _cursor.Visible;

    // Saved cursor state for DECSC/DECRC
    private bool _hasSavedCursor = false;
    private int _savedCursorRow;
    private int _savedCursorCol;
    private bool _savedCursorVisible;

    public void SaveCursor()
    {
        _hasSavedCursor = true;
        _savedCursorRow = _cursor.Row;
        _savedCursorCol = _cursor.Col;
        _savedCursorVisible = _cursor.Visible;
    }

    public void RestoreCursor()
    {
        if (!_hasSavedCursor) return;
        _cursor.Set(Math.Clamp(_savedCursorRow, 0, Rows - 1), Math.Clamp(_savedCursorCol, 0, Columns - 1), Rows, Columns);
        _cursor.SetVisible(_savedCursorVisible);
        _hasSavedCursor = false;
        MarkAllRowsDirty();
    }

    public void SetAutoWrap(bool enabled)
    {
        _autoWrap = enabled;
    }

    public bool AutoWrap => _autoWrap;

    public void SetBracketedPasteMode(bool enabled)
    {
        _bracketedPaste = enabled;
    }

    public bool BracketedPasteMode => _bracketedPaste;

    private void InitializeTabStops()
    {
        _tabStops = new bool[Columns];
        for (int c = 0; c < Columns; c += 8)
        {
            _tabStops[c] = true;
        }
    }

    public void SetTabStopAt(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        if (col < 0 || col >= Columns) return;
        _tabStops![col] = true;
    }

    public void ClearTabStopAt(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        if (col < 0 || col >= Columns) return;
        _tabStops![col] = false;
    }

    public void ClearAllTabStops()
    {
        InitializeTabStops();
    }

    public int GetNextTabStopFrom(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        for (int c = col + 1; c < Columns; c++)
        {
            if (_tabStops![c]) return c;
        }
        return Columns - 1;
    }

    public int GetPrevTabStopFrom(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        for (int c = col - 1; c >= 0; c--)
        {
            if (_tabStops![c]) return c;
        }
        return 0;
    }

    public void AddPromptMark(PromptKind kind)
    {
        int absoluteRow = _totalScrolled + _cursor.Row;
        _promptMarks.Add(new PromptMark(absoluteRow, kind));
        if (_promptMarks.Count > 5000)
            _promptMarks.RemoveRange(0, _promptMarks.Count - 5000);
    }

    public IReadOnlyList<PromptMark> GetPromptMarks() => _promptMarks;

    public PromptMark? FindNearestPrompt(int fromVisibleRow, bool searchForward)
    {
        if (_promptMarks.Count == 0) return null;

        int targetAbsolute = _totalScrolled + fromVisibleRow;

        if (searchForward)
        {
            for (int i = 0; i < _promptMarks.Count; i++)
            {
                if (_promptMarks[i].AbsoluteRow > targetAbsolute)
                    return _promptMarks[i];
            }
            return null;
        }
        else
        {
            for (int i = _promptMarks.Count - 1; i >= 0; i--)
            {
                if (_promptMarks[i].AbsoluteRow < targetAbsolute)
                    return _promptMarks[i];
            }
            return null;
        }
    }

    public int GetPromptVisibleRow(PromptMark mark)
    {
        return mark.AbsoluteRow - _totalScrolled;
    }

    public void ClearScrollback()
    {
        _totalScrolled = 0;
        _promptMarks.Clear();
    }

    /// <summary>
    /// Aggressively reduces scrollback to a smaller size while keeping the session running.
    /// This is used when a tab becomes inactive to free memory while preserving some context.
    /// The Screen's ring buffer cannot be partially trimmed; this only adjusts the count.
    /// </summary>
    public void TrimScrollback(int maxLines)
    {
        if (maxLines < 0) maxLines = 0;
        int trimStart = _totalScrolled - maxLines; // lines being dropped
        if (trimStart > 0)
            _promptMarks.RemoveAll(m => m.AbsoluteRow < trimStart);
        _totalScrolled = Math.Min(_totalScrolled, maxLines);
    }

    public void FullReset()
    {
        // RIS - Reset to Initial State
        _screens.ClearAll();
        _cursor.Reset();
        _bracketedPaste = false;
        _hasSavedCursor = false;
        InitializeTabStops();
        _totalScrolled = 0;
        MarkAllRowsDirty();
    }

    public void ScrollUpLines(int n)
    {
        ScrollRegionUp(_scrollTop, _scrollBottom, n);
    }

    public void ScrollDownLines(int n)
    {
        ScrollRegionDown(_scrollTop, _scrollBottom, n);
    }

    /// <summary>
    /// Shared SU/CSI-S/LF-at-region-bottom implementation: scrolls content up
    /// by n rows inside [top..bottom], rotates motion epochs with the content,
    /// bumps the exposed bottom band, and records the scroll for the renderer.
    /// </summary>
    private void ScrollRegionUp(int top, int bottom, int n)
    {
        if (n <= 0) return;
        ActiveBuffer.ScrollUpRegion(top, bottom, n);
        int height = bottom - top + 1;
        int delta = Math.Min(n, height);
        if (top == 0)
            unchecked { _totalScrolled += delta; }

        // Motion epochs travel with content: row r now holds the content that
        // was at row r+delta, so its epoch moves down the array. The exposed
        // (blanked) bottom band gets fresh epochs.
        ScrollEpochMath.RotateRange(_rowScrollEpochs, top, bottom, -delta);
        for (int r = bottom - delta + 1; r <= bottom; r++)
            unchecked { _rowScrollEpochs[r]++; }

        // Identity generations: the whole region changed; classification and
        // glyph caches must re-examine every row.
        BumpIdentity(top, height);
    }

    /// <summary>
    /// Shared SD/CSI-T/RI-at-region-top implementation: scrolls content down by
    /// n rows inside [top..bottom], rotating motion epochs and bumping the
    /// exposed top band (which holds restored scrollback or blanked rows).
    /// </summary>
    private void ScrollRegionDown(int top, int bottom, int n)
    {
        if (n <= 0) return;
        int clampedLines = Math.Min(n, bottom - top + 1);
        int restoredFromScrollback = 0;
        bool isFullScreenRegion = top == 0 && bottom == Rows - 1;
        if (isFullScreenRegion)
            restoredFromScrollback = Math.Min(_totalScrolled, clampedLines);

        ActiveBuffer.ScrollDownRegion(top, bottom, n);
        if (top == 0 && _totalScrolled > 0)
            unchecked { _totalScrolled = Math.Max(0, _totalScrolled - clampedLines); }

        // Full-screen reverse scrolling should reveal history until scrollback is
        // exhausted, then fall back to the terminal's blank-line insertion.
        if (isFullScreenRegion)
        {
            int blankLines = clampedLines - restoredFromScrollback;
            for (int row = 0; row < blankLines; row++)
                ActiveBuffer.ClearRow(row);
        }

        // Content moved down: row r now holds the content that was at row
        // r-delta; epochs rotate up the array. The exposed top band gets fresh
        // epochs (its content is restored-from-scrollback or blanked — new to
        // the renderer either way).
        int height = bottom - top + 1;
        ScrollEpochMath.RotateRange(_rowScrollEpochs, top, bottom, clampedLines);
        for (int r = top; r < top + clampedLines; r++)
            unchecked { _rowScrollEpochs[r]++; }

        BumpIdentity(top, height);
    }

    public void SetCursor(int row, int col)
    {
        // When origin mode (DECOM) is enabled, cursor coordinates are relative
        // to the current scroll region. The adapter passes 0-based params
        // (converted from 1-based by the parser adapter), so we need to
        // translate them into absolute coordinates when origin mode is on.
        if (_originMode)
        {
            int absRow = _scrollTop + row;
            int clampedRow = Math.Clamp(absRow, _scrollTop, _scrollBottom);
            _cursor.Set(clampedRow, Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
        else
        {
            _cursor.Set(Math.Clamp(row, 0, Rows - 1), Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
    }

    public readonly struct ScrollbackLine
    {
        public readonly string Text;
        public readonly int Length => Text?.Length ?? 0;
        public ScrollbackLine(string text) { Text = text; }
        public override string ToString() => Text ?? string.Empty;
    }

    private int _maxScrollbackOverride = -1;
    public int ScrollbackCount => Math.Min(_totalScrolled, _maxScrollbackOverride > 0 ? _maxScrollbackOverride : ActiveBuffer.ScrollbackCapacity);

    public ScrollbackLine GetScrollbackLine(int index)
    {
        if (index < 0 || index >= ScrollbackCount)
            return new ScrollbackLine(string.Empty);
        int sbIndex = ScrollbackCount - 1 - index;
        return new ScrollbackLine(ActiveBuffer.GetScrollbackRow(sbIndex));
    }

    public IReadOnlyList<string> GetScrollbackLines()
    {
        int count = ScrollbackCount;
        string[] lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            lines[i] = GetScrollbackLine(i).ToString();
        }
        return lines;
    }

    public int MaxScrollback
    {
        get => _maxScrollbackOverride > 0 ? _maxScrollbackOverride : ActiveBuffer.ScrollbackCapacity;
        set => _maxScrollbackOverride = value > 0 ? value : -1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public CellHot GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            var c = new CellHot();
            c.Rune = 32;
            return c;
        }
        return ActiveBuffer.GetCell(row, col);
    }

    /// <summary>
    /// Zero-copy read-only view of one visible row's hot cells (see
    /// <see cref="Screen.GetRowCells"/>). Used by the renderer's row-based
    /// classification to avoid per-cell bounds checks and repair writes.
    /// </summary>
    public ReadOnlySpan<CellHot> GetRowCells(int row)
        => ActiveBuffer.GetRowCells(row);

    /// <summary>
    /// Zero-copy read-only view of one visible row's cold cells.
    /// </summary>
    public ReadOnlySpan<ColdCell> GetRowColdCells(int row)
        => ActiveBuffer.GetRowColdCells(row);

    public ref readonly CellAttributes GetStyle(ushort styleId)
        => ref StyleSet.GetStyle(styleId);

    public string GetScrollbackLineText(int index)
        => GetScrollbackLine(index).Text ?? string.Empty;

    /// <summary>
    /// Captures the render state under the caller's SyncRoot hold: one bounded
    /// memcpy of the cell arenas plus style/generation/scrollback metadata.
    /// The returned snapshot is immutable and can be rasterized without the
    /// lock (see B-lite; docs/architecture/AvaloniaOptimizationPlan.md §10.7).
    /// The visible scrollback range [<paramref name="sbStart"/>, <paramref name="sbEnd"/>]
    /// (negative row indices, -1 = newest) is materialized as text.
    /// </summary>
    public RenderSnapshot CaptureRenderSnapshot(int sbStart, int sbEnd)
    {
        var styles = StyleSet.CaptureStyles();
        var snapshot = RenderSnapshot.Capture(
            ActiveBuffer,
            _rowGenerations,
            styles,
            ScrollbackCount,
            IsAlternateScreenActive,
            CursorRow,
            CursorCol);
        snapshot.GlobalGeneration = Generation;

        int count = sbEnd - sbStart + 1;
        if (count > 0)
        {
            snapshot.CapturedSbStart = sbStart;
            var text = new string[count];
            for (int r = sbStart; r <= sbEnd; r++)
            {
                int idx = r + ScrollbackCount;
                idx = Math.Max(0, Math.Min(ScrollbackCount - 1, idx));
                text[r - sbStart] = GetScrollbackLine(idx).Text ?? string.Empty;
            }
            snapshot.ScrollbackText = text;
        }

        return snapshot;
    }

    /// <summary>
    /// Per-frame render capture (B-lite): copies only the visible rows' cell
    /// slices instead of the whole arena. The raster reads ~viewport-rows x
    /// columns; a full-arena memcpy per content frame is pure overhead.
    /// </summary>
    public RenderSnapshot CaptureRenderSnapshotVisible(int sbStart, int sbEnd)
    {
        var styles = StyleSet.CaptureStyles();
        var snapshot = RenderSnapshot.CaptureVisible(
            ActiveBuffer,
            _rowGenerations,
            styles,
            ScrollbackCount,
            IsAlternateScreenActive,
            CursorRow,
            CursorCol);
        snapshot.GlobalGeneration = Generation;

        int count = sbEnd - sbStart + 1;
        if (count > 0)
        {
            snapshot.CapturedSbStart = sbStart;
            var text = new string[count];
            for (int r = sbStart; r <= sbEnd; r++)
            {
                int idx = r + ScrollbackCount;
                idx = Math.Max(0, Math.Min(ScrollbackCount - 1, idx));
                text[r - sbStart] = GetScrollbackLine(idx).Text ?? string.Empty;
            }
            snapshot.ScrollbackText = text;
        }

        return snapshot;
    }

    public ColdCell GetColdCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return default;
        return ActiveBuffer.GetColdCell(row, col);
    }

    public void MoveCursorTo(int row, int col)
    {
        // When origin mode (DECOM) is enabled, cursor coordinates are relative
        // to the current scroll region. The adapter passes 0-based params
        // (converted from 1-based by the parser adapter), so we need to
        // translate them into absolute coordinates when origin mode is on.
        if (_originMode)
        {
            int absRow = _scrollTop + row;
            int clampedRow = Math.Clamp(absRow, _scrollTop, _scrollBottom);
            _cursor.Set(clampedRow, Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
        else
        {
            _cursor.Set(Math.Clamp(row, 0, Rows - 1), Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
    }

    public void SetOriginMode(bool enabled)
    {
        _originMode = enabled;
        // DECOM resets the cursor to the current home position.
        _cursor.Set(_originMode ? _scrollTop : 0, 0, Rows, Columns);
        // Changing origin may change visible content; mark all rows dirty.
        MarkAllRowsDirty();
    }

    public void MoveCursorBy(int dRow, int dCol)
    {
        int newRow = _cursor.Row + dRow;
        int newCol = _cursor.Col + dCol;

        if (_originMode)
        {
            newRow = Math.Clamp(newRow, _scrollTop, _scrollBottom);
        }
        else
        {
            newRow = Math.Clamp(newRow, 0, Rows - 1);
        }

        newCol = Math.Clamp(newCol, 0, Columns - 1);
        _cursor.Set(newRow, newCol, Rows, Columns);
    }

    public void CarriageReturn()
    {
        _cursor.CarriageReturn();
    }

    public void LineFeed()
    {
        // If the cursor is at the bottom of the scroll region, scroll only
        // that region. This implements DECSTBM semantics used by applications
        // like Neovim.
        if (_cursor.Row == _scrollBottom)
        {
            ScrollRegionUp(_scrollTop, _scrollBottom, 1);
            return;
        }

        // If the cursor has escaped below the scroll-bottom, ignore LF.
        // Some apps (Neovim) intentionally write the statusline on the last
        // row which is outside the scroll region and expect LF to be ignored.
        if (_cursor.Row > _scrollBottom)
        {
            return;
        }
        // Otherwise behave like a normal line feed (move cursor down)
        _cursor.MoveBy(1, 0, Rows, Columns);
    }

    public void ReverseIndex()
    {
        if (_cursor.Row == _scrollTop)
        {
            ScrollDownLines(1);
            return;
        }

        if (_cursor.Row <= _scrollBottom)
        {
            _cursor.MoveBy(-1, 0, Rows, Columns);
        }
    }

    public void EraseLine(int mode)
    {
        _eraser.EraseLine(ActiveBuffer, _cursor, Columns, mode);
        MarkRowDirty(_cursor.Row);
    }

    public void InsertChars(int count)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        int cols = Columns;
        // Shift cells to the right
        for (int c = cols - 1; c >= col + count; c--)
        {
            ref var dst = ref ActiveBuffer.GetCellRef(row, c);
            var src = ActiveBuffer.GetCell(row, c - count);
            dst = src;
            var srcCold = ActiveBuffer.GetColdCell(row, c - count);
            ActiveBuffer.GetColdCellRef(row, c) = srcCold;
        }
        // Clear inserted region
        for (int c = col; c < Math.Min(cols, col + count); c++)
        {
            ActiveBuffer.ClearCell(row, c);
        }
        // Clean orphaned continuations in the shifted range
        CleanRowContinuations(row, col + count, cols);
        MarkRowDirty(row);
    }

    public void DeleteChars(int count)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        int cols = Columns;
        int endShift = cols - count;
        for (int c = col; c < endShift; c++)
        {
            ref var dst = ref ActiveBuffer.GetCellRef(row, c);
            var src = ActiveBuffer.GetCell(row, c + count);
            dst = src;
            var srcCold = ActiveBuffer.GetColdCell(row, c + count);
            ActiveBuffer.GetColdCellRef(row, c) = srcCold;
        }
        // Clear trailing cells
        for (int c = Math.Max(0, endShift); c < cols; c++)
        {
            ActiveBuffer.ClearCell(row, c);
        }
        // Clean orphaned continuations in the copied range
        CleanRowContinuations(row, col, cols);
        MarkRowDirty(row);
    }

    private void CleanRowContinuations(int row, int startCol, int endCol)
    {
        var buf = ActiveBuffer;
        for (int c = startCol; c < endCol && c < Columns; c++)
        {
            ref var cell = ref buf.GetCellRef(row, c);
            if (!cell.IsContinuation) continue;
            bool valid = c > 0;
            if (valid)
            {
                var prev = buf.GetCellRef(row, c - 1);
                valid = !prev.IsContinuation && prev.Rune != 0 && prev.Width == 2;
            }
            if (!valid)
            {
                cell.Reset();
                buf.GetColdCellRef(row, c).Reset();
            }
        }
        ActiveBuffer.RecalculateRowMaxCol(row);
    }

    public void EraseCharacters(int count)
    {
        if (count <= 0) return;

        int row = _cursor.Row;
        int start = Math.Clamp(_cursor.Col, 0, Columns - 1);
        int endExclusive = Math.Min(Columns, start + count);

        for (int c = start; c < endExclusive; c++)
        {
            ActiveBuffer.ClearCell(row, c);
        }

        MarkRowDirty(row);
    }

    public void InsertLines(int count)
    {
        if (count <= 0) return;
        int top = _scrollTop;
        int bottom = _scrollBottom;
        int row = Math.Clamp(_cursor.Row, top, bottom);
        int regionHeight = bottom - row + 1;
        if (count >= regionHeight)
        {
            // clear region: every row is exposed; plain identity+epoch bump.
            for (int r = row; r <= bottom; r++)
            for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
            MarkRowRangeDirty(row, bottom - row + 1);
            return;
        }
        // shift down
        for (int r = bottom; r >= row + count; r--)
        {
            for (int c = 0; c < Columns; c++)
            {
                ref var dst = ref ActiveBuffer.GetCellRef(r, c);
                var src = ActiveBuffer.GetCell(r - count, c);
                dst = src;
                var srcCold = ActiveBuffer.GetColdCell(r - count, c);
                ActiveBuffer.GetColdCellRef(r, c) = srcCold;
            }
            // Row metadata travels with the content, not with the physical row.
            int dstPhys = ActiveBuffer.GetPhysicalRow(r);
            int srcPhys = ActiveBuffer.GetPhysicalRow(r - count);
            ActiveBuffer.RowMaxCol[dstPhys] = ActiveBuffer.RowMaxCol[srcPhys];
            ActiveBuffer.RowColdFlags[dstPhys] = ActiveBuffer.RowColdFlags[srcPhys];
        }
        // clear inserted lines
        for (int r = row; r < row + count; r++)
        for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
        ScrollEpochMath.RotateRange(_rowScrollEpochs, row, bottom, count);
        for (int r = row; r < row + count; r++)
            unchecked { _rowScrollEpochs[r]++; }
        BumpIdentity(row, regionHeight);
    }

    public void DeleteLines(int count)
    {
        if (count <= 0) return;
        int top = _scrollTop;
        int bottom = _scrollBottom;
        int row = Math.Clamp(_cursor.Row, top, bottom);
        int regionHeight = bottom - row + 1;
        if (count >= regionHeight)
        {
            // clear region: every row is exposed; plain identity+epoch bump.
            for (int r = row; r <= bottom; r++)
            for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
            MarkRowRangeDirty(row, bottom - row + 1);
            return;
        }
        // shift up
        for (int r = row; r <= bottom - count; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                ref var dst = ref ActiveBuffer.GetCellRef(r, c);
                var src = ActiveBuffer.GetCell(r + count, c);
                dst = src;
                var srcCold = ActiveBuffer.GetColdCell(r + count, c);
                ActiveBuffer.GetColdCellRef(r, c) = srcCold;
            }
            // Row metadata travels with the content, not with the physical row.
            int dstPhys = ActiveBuffer.GetPhysicalRow(r);
            int srcPhys = ActiveBuffer.GetPhysicalRow(r + count);
            ActiveBuffer.RowMaxCol[dstPhys] = ActiveBuffer.RowMaxCol[srcPhys];
            ActiveBuffer.RowColdFlags[dstPhys] = ActiveBuffer.RowColdFlags[srcPhys];
        }
        // clear trailing lines
        for (int r = bottom - count + 1; r <= bottom; r++)
        for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
        ScrollEpochMath.RotateRange(_rowScrollEpochs, row, bottom, -count);
        for (int r = bottom - count + 1; r <= bottom; r++)
            unchecked { _rowScrollEpochs[r]++; }
        BumpIdentity(row, regionHeight);
    }

    public void EraseDisplay(int mode)
    {
        var reset = _eraser.EraseDisplay(ActiveBuffer, _cursor, Rows, Columns, mode);
        if (reset)
        {
            _cursor.Reset();
        }
        if (mode == 2)
        {
            MarkAllRowsDirty();
        }
        else if (mode == 0)
        {
            MarkRowRangeDirty(_cursor.Row, Rows - _cursor.Row);
        }
        else if (mode == 1)
        {
            MarkRowRangeDirty(0, _cursor.Row + 1);
        }
    }

    public void WriteText(ReadOnlySpan<char> text, string? foreground, string? background = null, bool bold = false)
    {
        var attributes = new CellAttributes
        {
            Foreground = HexToSgrColorArgb(foreground),
            Background = HexToSgrColorArgb(background),
            Bold = bold,
        };

        WriteText(text, attributes);
    }

    private static SgrColorArgb HexToSgrColorArgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 7 || hex[0] != '#')
            return default;
        
        // Parse hex color #RRGGBB
        if (uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return new SgrColorArgb(0xFF000000u | rgb);
        }
        return default;
    }

    public void WriteText(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        _writer.WriteText(text, in attributes);
    }

    /// <summary>
    /// Fast path: writes ASCII bytes directly to the buffer without a char conversion.
    /// </summary>
    internal void WriteAscii(ReadOnlySpan<byte> text, in CellAttributes attributes)
    {
        _writer.WriteAscii(text, in attributes);
    }

    internal void ScrollUp(int lines)
    {
        ScrollRegionUp(_scrollTop, _scrollBottom, lines);
    }

    /// <summary>
    /// Delegates to <see cref="Screen.ValidateInvariants"/> on the active screen.
    /// Test/debug aid only; not called on the live path.
    /// </summary>
    public List<string> ValidateInvariants() => ActiveBuffer.ValidateInvariants();

    public string GetRowText(int row)
    {
        using var sb = ZStr.CreateStringBuilder(Columns);
        var buf = _screens.Active;
        for (int j = 0; j < Columns; j++)
        {
            var cell = buf.GetCell(row, j);
            var cold = buf.GetColdCell(row, j);
            if (cell.IsContinuation)
            {
                sb.Append(' ');
            }
            else
            {
                var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
                if (string.IsNullOrEmpty(grapheme))
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(grapheme);
                }
            }
        }

        return sb.ToString();
    }

    private BufferTextWriter CreateWriter()
    {
        return new BufferTextWriter(this, _cursor, _eraser, StyleSet);
    }

    // per-row versioning and dirty arrays removed

    internal void MarkRowDirty(int row)
    {
        if (row < 0 || row >= _rowGenerations.Length) return;
        unchecked { _rowGenerations[row]++; }
        if (row < _rowScrollEpochs.Length) unchecked { _rowScrollEpochs[row]++; }
        unchecked { _globalGeneration++; }
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(start + count, _rowGenerations.Length);
        for (int i = start; i < end; i++)
        {
            unchecked { _rowGenerations[i]++; }
            if (i < _rowScrollEpochs.Length) unchecked { _rowScrollEpochs[i]++; }
        }
        unchecked { _globalGeneration += (ulong)(end - start); }
    }

    private void MarkAllRowsDirty()
    {
        for (int i = 0; i < _rowGenerations.Length; i++)
        {
            unchecked { _rowGenerations[i]++; }
            if (i < _rowScrollEpochs.Length) unchecked { _rowScrollEpochs[i]++; }
        }
        unchecked { _globalGeneration += (ulong)_rowGenerations.Length; }
    }

    /// <summary>
    /// Bumps only the identity generations for [start, start+count). Used by
    /// scroll operations, which must invalidate the classification/glyph
    /// caches for the whole region while handling motion epochs themselves
    /// (rotation + exposed-row bump) so the renderer can skip moved rows.
    /// </summary>
    private void BumpIdentity(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(start + count, _rowGenerations.Length);
        for (int i = start; i < end; i++)
            unchecked { _rowGenerations[i]++; }
        unchecked { _globalGeneration += (ulong)(end - start); }
    }

    public ulong GetRowGeneration(int row)
    {
        if (row < 0 || row >= _rowGenerations.Length) return 0;
        return _rowGenerations[row];
    }

    public ReadOnlySpan<ulong> RowGenerations => _rowGenerations;

    /// <summary>Motion epoch of one logical row (see <c>_rowScrollEpochs</c>).</summary>
    public ulong GetRowEpoch(int row)
    {
        if (row < 0 || row >= _rowScrollEpochs.Length) return 0;
        return _rowScrollEpochs[row];
    }

    /// <summary>The full motion-epoch array; the renderer mirrors this.</summary>
    public ReadOnlySpan<ulong> RowScrollEpochs => _rowScrollEpochs;

    /// <summary>
    /// Notify the active screen that a render cycle is starting so it can
    /// reset any per-render debug state (such as dedupe caches used by
    /// DumpRowRange/DumpRowDetail).
    /// </summary>
    public void MarkRender()
    {
        // The renderer's dirty-row detection keys on generation/epoch bumps.
        // The text writer coalesces consecutive same-row writes into one bump;
        // resetting that coalescing here guarantees every write that lands
        // after a render is visible to the next one (typing in a single row
        // would otherwise never mark the row dirty and the display would go
        // stale).
        _writer.ResetRowDirtyCoalescing();
        ActiveBuffer.MarkRender();
    }

    public void ClearScreen()
    {
        _screens.ClearAll();
        _cursor.Reset();
        MarkAllRowsDirty();
    }

    public string GetDebugInfo()
    {
        var sb = new StringBuilder();
        var buf = ActiveBuffer;
        sb.Append($"H={buf.Head} TScrolled={_totalScrolled} SbCnt={ScrollbackCount}");
        sb.Append($" sTOP={_scrollTop} sBOT={_scrollBottom} org={_originMode}");
        sb.Append($" gen={_globalGeneration}");
        sb.Append($" rows={Rows} cols={Columns} cur=({_cursor.Row},{_cursor.Col})");
        sb.Append(" physmap:[");
        for (int r = 0; r < Math.Min(Rows, 6); r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append(buf.GetPhysicalRow(r));
        }
        if (Rows > 6) sb.Append("...");
        sb.Append(']');
        // Last 3 rows of scroll region (for warping diagnostics)
        sb.Append(" tails:");
        int regionBot = Math.Min(_scrollBottom, Rows - 1);
        for (int r = Math.Max(0, regionBot - 2); r <= regionBot; r++)
        {
            sb.Append(r == Math.Max(0, regionBot - 2) ? '|' : ';');
            var text = GetRowText(r).TrimEnd();
            if (text.Length > 24) text = text.Substring(0, 24);
            sb.Append(text);
        }
        return sb.ToString();
    }
}
