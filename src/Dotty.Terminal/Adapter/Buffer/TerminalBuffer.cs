using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter.Buffer;
namespace Dotty.Terminal.Adapter;

/// <summary>
/// Very small screen model for now: stores visible lines and a simple scrollback.
/// Designed to be called from parser callbacks; it is not thread-safe by itself.
/// </summary>
public partial class TerminalBuffer : IRenderSource, IDisposable
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
    private bool _leftRightMarginMode;
    private int _leftMargin;
    private int _rightMargin;
    private bool _reverseWraparound;
    private bool _extendedReverseWraparound;
    private bool _protectionActive;
    private bool _respectIsoProtection;
    private bool _isAlternate;
    private int _savedTotalScrolled = 0;
    private SavedCursor _mainSavedCursor;
    private SavedCursor _alternateSavedCursor;
    private bool _hasMainSavedCursor;
    private bool _hasAlternateSavedCursor;

    private readonly record struct SavedCursor(CursorState Cursor, bool ProtectionActive, bool OriginMode);

    /// <summary>Optional real-scroll notification used only while DEC smooth scrolling is enabled.</summary>
    internal Action<int, int, int, int, bool>? SmoothScrollRequested { get; set; }
    private ulong[] _rowGenerations = Array.Empty<ulong>();
    private int _disposed;
    private readonly object _snapshotPoolGate = new();
    private RenderSnapshot? _snapshotPoolHead;
    private ulong _globalGeneration;
    /// <summary>
    /// Monotonic identity generation for diagnostic correlation.
    /// Read it while holding <see cref="SyncRoot"/> when a consistent frame value is required.
    /// </summary>
    public ulong Generation => _globalGeneration;
    internal int ScrollTop => _scrollTop;
    internal int ScrollBottom => _scrollBottom;
    internal int LeftMargin => _leftMargin;
    internal int RightMargin => _rightMargin;


    // Motion epochs: travel with content across scrolls (rotated like the
    // Screen ring) and are bumped only for rows whose content actually changed
    // in place (writes, erases) or was newly exposed by a scroll. The renderer
    // mirrors this array and re-renders only rows where bufferEpoch != mirror,
    // i.e. rows whose pixels were NOT already moved into place by a memmove.
    // Distinct from _rowGenerations (identity; bumped on every content change)
    // so the composer's per-row classification cache can key on identity
    // without false hits after a scroll rotation.

    /// <summary>A buffer with the built-in scrollback policy (<see cref="ScrollbackPolicy.Default"/>).</summary>
    public TerminalBuffer(int rows = 24, int columns = 80)
        : this(rows, columns, ScrollbackPolicy.Default)
    {
    }

    /// <summary>A buffer that keeps exactly <paramref name="scrollbackCapacity"/> history lines at any size (no byte cap).</summary>
    public TerminalBuffer(int rows, int columns, int scrollbackCapacity)
        : this(rows, columns, ScrollbackPolicy.Fixed(scrollbackCapacity))
    {
    }

    /// <summary>
    /// A buffer whose history capacity is <c>scrollbackPolicy.CapacityFor(rows, columns)</c>, recomputed on every
    /// resize: under a byte cap a wider (or taller) screen keeps fewer lines, and a narrower one more.
    /// </summary>
    public TerminalBuffer(int rows, int columns, ScrollbackPolicy scrollbackPolicy)
    {
        Rows = rows;
        Columns = columns;
        _rowGenerations = new ulong[rows];
        _screens = new ScreenManager(rows, columns, scrollbackPolicy);
        _writer = CreateWriter();
        _scrollBottom = rows - 1;
        InitializeTabStops();
    }

    public void Resize(int rows, int cols)
    {
        if (rows == Rows && cols == Columns)
            return;

        lock (SyncRoot)
        {
            if (rows == Rows && cols == Columns)
                return;
            try
            {
                ResizeWithReflow(rows, cols);
            }
            catch
            {
                // A failure between capturing and rebasing the placement anchors must not leave them at stale rows.
                AbandonGraphicsReflow();
                throw;
            }
        }
    }

    /// <summary>DECSET/DECRST 1049 saves and restores the ordinary main-screen cursor slot.</summary>
    public void SetAlternateScreen(bool active)
    {
        if (active == _isAlternate)
            return;
        if (active)
            SaveCursor();
        _screens.SetAlternate(active, clearOnEnter: true, clearOnExit: true);
        SwitchScreenState(active);
        if (!active)
            RestoreCursor();
    }

    /// <summary>DECSET/DECRST 47 retains alternate contents; 1047 clears them before exit.</summary>
    public void SwitchAlternateScreen(int mode, bool active)
    {
        if (mode is not (47 or 1047))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (active == _isAlternate)
            return;
        _screens.SetAlternate(active, clearOnEnter: false, clearOnExit: mode == 1047);
        SwitchScreenState(active);
    }

    private void SwitchScreenState(bool active)
    {
        BumpCoordinateEpoch();
        if (active)
        {
            _savedTotalScrolled = _totalScrolled;
            _totalScrolled = 0;
        }
        else
            _totalScrolled = _savedTotalScrolled;
        _isAlternate = active;
        MarkAllRowsDirty();
    }

    public bool IsAlternateScreenActive => _isAlternate;

    public void SetScrollRegion(int top1Based, int bottom1Based)
    {
        int newTop = Math.Clamp(top1Based - 1, 0, Rows - 1);
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

        HomeCursor();
    }

    public bool LeftRightMarginMode => _leftRightMarginMode;
    public bool ReverseWraparound => _reverseWraparound;
    public bool ExtendedReverseWraparound => _extendedReverseWraparound;

    private int EffectiveLeftMargin => _leftRightMarginMode ? _leftMargin : 0;
    private int EffectiveRightMargin => _leftRightMarginMode ? _rightMargin : Columns - 1;

    internal int WriteLeftMargin => EffectiveLeftMargin;
    internal int GetWriteRightMargin(int column) =>
        _leftRightMarginMode && column <= _rightMargin ? _rightMargin : Columns - 1;

    internal bool IsProtectionActive => _protectionActive;
    internal bool RespectIsoProtection => _respectIsoProtection;

    internal void SetIsoProtection(bool active)
    {
        _protectionActive = active;
        if (active)
            _respectIsoProtection = true;
    }

    internal void SetDecProtection(int mode)
    {
        if ((uint)mode > 2) return;
        _protectionActive = mode == 1;
        _respectIsoProtection = false;
    }

    public void SetLeftRightMarginMode(bool enabled)
    {
        if (_leftRightMarginMode == enabled)
            return;

        _leftRightMarginMode = enabled;
        _leftMargin = 0;
        _rightMargin = Columns - 1;
    }

    public void SetHorizontalMargins(int left1Based, int right1Based)
    {
        int left = left1Based == 0 ? 0 : left1Based - 1;
        int right = right1Based == 0 || right1Based > Columns ? Columns - 1 : right1Based - 1;
        if (left < 0 || right >= Columns || left >= right)
            return;

        _leftMargin = left;
        _rightMargin = right;
        HomeCursor();
    }

    public void SetReverseWrapMode(int mode, bool enabled)
    {
        if (mode == 45)
            _reverseWraparound = enabled;
        else if (mode == 1045)
            _extendedReverseWraparound = enabled;
    }

    public void ScreenAlignmentTest(in CellAttributes attributes)
    {
        _originMode = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _leftMargin = 0;
        _rightMargin = Columns - 1;


        Span<char> fill = stackalloc char[128];
        fill.Fill('E');
        for (int row = 0; row < Rows; row++)
        {
            ActiveBuffer.ClearRowContinuation(row);
            for (int col = 0; col < Columns;)
            {
                int length = Math.Min(fill.Length, Columns - col);
                SetCursor(row, col);
                _writer.WriteText(fill[..length], in attributes);
                col += length;
            }
        }

        HomeCursor();
        MarkAllRowsDirty();
    }

    private void HomeCursor() =>
        _cursor.Set(_originMode ? _scrollTop : 0, _originMode ? EffectiveLeftMargin : 0, Rows, Columns);

    private int _totalScrolled = 0;
    private readonly List<PromptMark> _promptMarks = new();
    private bool[]? _tabStops;
    internal bool _autoWrap = true; // DECAWM default is enabled
    private bool _bracketedPaste = false;

    public int CursorRow => _cursor.Row;
    public int CursorCol => _cursor.Col;
    public int CursorColForReport => _originMode ? _cursor.Col - EffectiveLeftMargin : _cursor.Col;
    public bool CursorVisible => _cursor.Visible;
    public TerminalCursorShape CursorShape { get; private set; } = TerminalCursorShape.Block;
    public bool CursorBlinking { get; private set; } = true;

    /// <summary>
    /// True until the application selects a style with DECSCUSR 1-6, and again after DECSCUSR 0, DECSTR or RIS.
    /// The renderer then draws the configured <c>cursor.shape</c>/<c>cursor.blink</c> instead of
    /// <see cref="CursorShape"/>/<see cref="CursorBlinking"/> (which hold the VT default, a blinking block).
    /// </summary>
    public bool CursorStyleIsDefault { get; private set; } = true;

    /// <summary>Applies an explicit application cursor style (DECSCUSR 1-6).</summary>
    public void SetCursorStyle(TerminalCursorShape shape, bool blinking)
    {
        CursorShape = shape;
        CursorBlinking = blinking;
        CursorStyleIsDefault = false;
    }

    /// <summary>Returns to the user's configured default cursor style (DECSCUSR 0, DECSTR, RIS).</summary>
    public void ResetCursorStyle()
    {
        CursorShape = TerminalCursorShape.Block;
        CursorBlinking = true;
        CursorStyleIsDefault = true;
    }

    public void SetCursorVisible(bool visible) => _cursor.SetVisible(visible);


    // Saved cursor state is independent for the main and alternate screens.
    private bool HasSavedCursor => _isAlternate ? _hasAlternateSavedCursor : _hasMainSavedCursor;
    private SavedCursor ActiveSavedCursor => _isAlternate ? _alternateSavedCursor : _mainSavedCursor;

    public void SaveCursor()
    {
        var state = new SavedCursor(_cursor.CaptureState(), _protectionActive, _originMode);
        if (_isAlternate)
        {
            _alternateSavedCursor = state;
            _hasAlternateSavedCursor = true;
        }
        else
        {
            _mainSavedCursor = state;
            _hasMainSavedCursor = true;
        }
    }

    public void RestoreCursor()
    {
        if (!HasSavedCursor)
            return;
        var state = ActiveSavedCursor;
        _cursor.RestoreState(state.Cursor, Rows, Columns);
        _protectionActive = state.ProtectionActive;
        _originMode = state.OriginMode;
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
        if (_tabStops == null || _tabStops.Length < Columns)
            Array.Resize(ref _tabStops, Columns);
        Array.Clear(_tabStops!, 0, Columns);
        for (int c = 0; c < Columns; c += 8)
            _tabStops[c] = true;
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
        if (_tabStops is not null)
            Array.Clear(_tabStops, 0, Columns);
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
        _promptMarks.Add(new PromptMark(absoluteRow, kind, CursorCol));
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
    public int GetPromptVisibleColumn(PromptMark mark) => mark.AbsoluteColumn;
    public bool HasPromptMarks(PromptKind kind)
    {
        foreach (var mark in _promptMarks)
            if (mark.Kind == kind) return true;
        return false;
    }


    public PromptMark? FindNearestPrompt(PromptKind kind, int fromVisibleRow, bool searchForward)
    {
        int targetAbsolute = _totalScrolled + fromVisibleRow;
        if (searchForward)
        {
            foreach (var mark in _promptMarks)
                if (mark.Kind == kind && mark.AbsoluteRow > targetAbsolute) return mark;
        }
        else
        {
            for (int i = _promptMarks.Count - 1; i >= 0; i--)
                if (_promptMarks[i].Kind == kind && _promptMarks[i].AbsoluteRow < targetAbsolute) return _promptMarks[i];
        }
        return null;
    }

    public bool TryGetLatestCommandOutputRange(out int startRow, out int startColumn, out int endRow, out int endColumn)
    {
        startRow = startColumn = endRow = endColumn = 0;
        int end = -1;
        for (int i = _promptMarks.Count - 1; i >= 0; i--)
            if (_promptMarks[i].Kind == PromptKind.CommandEnd) { end = i; break; }
        if (end < 0) return false;
        int start = -1;
        for (int i = end - 1; i >= 0; i--)
            if (_promptMarks[i].Kind == PromptKind.Output) { start = i; break; }
        if (start < 0) return false;
        var output = _promptMarks[start];
        var commandEnd = _promptMarks[end];
        startRow = output.AbsoluteRow - _totalScrolled;
        startColumn = output.AbsoluteColumn;
        endRow = commandEnd.AbsoluteRow - _totalScrolled;
        endColumn = commandEnd.AbsoluteColumn;
        return true;
    }
    public void ClearScrollback()
    {
        _totalScrolled = 0;
        BumpCoordinateEpoch();
        _promptMarks.Clear();
        // Blank the rows that held history and return their pages to the OS; the visible screen stays.
        ActiveBuffer.DiscardHistory();
        // Only the history goes: the floor is the first visible row, so visible placements stay.
        DiscardGraphicsHistory(wholeRing: false);
        if (!_isAlternate)
            RaiseHistoryInvalidated(HistoryDiscardReason.Cleared);
    }

    public void FullReset()
    {
        // RIS - Reset to Initial State: leave the alternate screen, clear everything, then the DECSTR core.
        if (_isAlternate)
            SetAlternateScreen(false);
        _screens.ClearAll();
        // RIS deletes every image of both screens (the configured limits stay with the manager).
        if (_screens.ClearImageStores())
            BumpGraphicsGeneration();
        _cursor.Reset();
        _bracketedPaste = false;
        InitializeTabStops();
        _totalScrolled = 0;
        BumpCoordinateEpoch();
        _savedTotalScrolled = 0;
        ResetFullOnlyState();
        ApplySoftResetCore();
        MarkAllRowsDirty();
        RaiseHistoryInvalidated(HistoryDiscardReason.Reset);
    }

    public void ScrollUpLines(int n)
    {
        ScrollRegionUp(_scrollTop, _scrollBottom, n);
    }

    public void ScrollDownLines(int n)
    {
        ScrollRegionDown(_scrollTop, _scrollBottom, n);
    }

    /// <summary>Scrolls a vertical region upward, preserving the full-width ring/history fast path.</summary>
    private void ScrollRegionUp(int top, int bottom, int n)
    {
        if (n <= 0) return;
        int left = EffectiveLeftMargin;
        int right = EffectiveRightMargin;
        bool partialWidth = left != 0 || right != Columns - 1;
        bool smoothableRegion = !partialWidth;
        if (smoothableRegion)
            SmoothScrollRequested?.Invoke(-1, n, top, bottom, true);
        int height = bottom - top + 1;
        int delta;
        if (partialWidth)
        {
            delta = ActiveBuffer.ScrollRectangle(top, bottom, left, right, n);
            if (delta <= 0)
            {
                if (smoothableRegion) SmoothScrollRequested?.Invoke(-1, 0, top, bottom, false);
                return;
            }
        }
        else
        {
            delta = ActiveBuffer.ScrollUpRegion(top, bottom, n);
            if (delta <= 0)
            {
                if (smoothableRegion) SmoothScrollRequested?.Invoke(-1, 0, top, bottom, false);
                return;
            }
            if (top == 0)
                unchecked { _totalScrolled += delta; }
        }

        if (_screens.ActiveImages is { HasPlacements: true } images)
        {
            if (partialWidth)
            {
                if (images.OnRectangleScrolled(top, bottom, left, right, delta, Rows) != 0)
                    BumpGraphicsGeneration();
            }
            else
            {
                if (top == 0 && bottom == Rows - 1)
                    images.OnRingRotatedUp(delta, ActiveBuffer.ScrollbackCapacity);
                else
                    images.OnRegionScrolled(top, bottom, delta, Rows);
                BumpGraphicsGeneration();
            }
        }

        BumpIdentity(top, height);
        if (smoothableRegion) SmoothScrollRequested?.Invoke(-1, delta, top, bottom, false);
    }

    /// <summary>Scrolls a vertical region downward, using the ring only when the full screen width moves.</summary>
    private void ScrollRegionDown(int top, int bottom, int n)
    {
        if (n <= 0) return;
        int clampedLines = Math.Min(n, bottom - top + 1);
        int left = EffectiveLeftMargin;
        int right = EffectiveRightMargin;
        bool partialWidth = left != 0 || right != Columns - 1;
        bool smoothableRegion = !partialWidth;
        if (smoothableRegion)
            SmoothScrollRequested?.Invoke(1, n, top, bottom, true);
        int height = bottom - top + 1;

        if (partialWidth)
        {
            int delta = ActiveBuffer.ScrollRectangle(top, bottom, left, right, -n);
            if (delta <= 0)
            {
                return;
            }
            if (_screens.ActiveImages is { HasPlacements: true } partialImages
                && partialImages.OnRectangleScrolled(top, bottom, left, right, -delta, Rows) != 0)
                BumpGraphicsGeneration();
            BumpIdentity(top, height);
            return;
        }

        int restoredFromScrollback = 0;
        bool isFullScreenRegion = top == 0 && bottom == Rows - 1;
        if (isFullScreenRegion)
        {
            int retainedHistory = Math.Max(0, Math.Min(_totalScrolled, ActiveBuffer.ScrollbackCapacity));
            restoredFromScrollback = Math.Min(retainedHistory, clampedLines);
        }

        ActiveBuffer.ScrollDownRegion(top, bottom, n);
        if (top == 0 && _totalScrolled > 0)
            unchecked { _totalScrolled = Math.Max(0, _totalScrolled - clampedLines); }

        if (isFullScreenRegion)
        {
            int blankLines = clampedLines - restoredFromScrollback;
            for (int row = 0; row < blankLines; row++)
                ActiveBuffer.ClearRow(row);
            if (blankLines > 0)
                ActiveBuffer.ClearRowContinuation(blankLines);
        }

        if (_screens.ActiveImages is { HasPlacements: true } images)
        {
            if (isFullScreenRegion)
                images.OnRingRotatedDown(clampedLines, Rows);
            else
                images.OnRegionScrolled(top, bottom, -clampedLines, Rows);
            BumpGraphicsGeneration();
        }

        BumpIdentity(top, height);
        if (smoothableRegion) SmoothScrollRequested?.Invoke(1, clampedLines, top, bottom, false);
    }

    public void LineFeed()
    {
        Index();
        if (NewLineMode)
            CarriageReturn();
    }

    private void IndexCore()
    {
        _cursor.SetWrapPending(false);
        int row = _cursor.Row;
        if (row == _scrollBottom)
        {
            if (_cursor.Col >= EffectiveLeftMargin && _cursor.Col <= EffectiveRightMargin)
                ScrollRegionUp(_scrollTop, _scrollBottom, 1);
            return;
        }

        if (row < Rows - 1)
            _cursor.MoveBy(1, 0, Rows, Columns);
    }

    /// <summary>Autowrap line advance. True when the cursor advances or a multi-row region scrolls.</summary>
    internal bool WrapToNextLine()
    {
        int row = _cursor.Row;
        bool advances = row < Rows - 1
            && (row != _scrollBottom
                || (_cursor.Col >= EffectiveLeftMargin && _cursor.Col <= EffectiveRightMargin
                    && _scrollTop < _scrollBottom));
        if (row == _scrollBottom && _scrollTop < _scrollBottom
            && _cursor.Col >= EffectiveLeftMargin && _cursor.Col <= EffectiveRightMargin)
            advances = true;
        Index();
        CarriageReturn();
        _cursor.SetWrapPending(false);
        return advances;
    }

    /// <summary>IND (ESC D): line index without carriage return.</summary>
    public void Index() => IndexCore();

    /// <summary>NEL (ESC E): index first, then carriage return.</summary>
    public void NextLine()
    {
        Index();
        CarriageReturn();
    }

    public void ReverseIndex()
    {
        _cursor.SetWrapPending(false);
        int row = _cursor.Row;
        if (row == _scrollTop)
        {
            if (_cursor.Col >= EffectiveLeftMargin && _cursor.Col <= EffectiveRightMargin)
                ScrollRegionDown(_scrollTop, _scrollBottom, 1);
            return;
        }

        if (row > 0)
            _cursor.MoveBy(-1, 0, Rows, Columns);
    }


    public void SetCursor(int row, int col)
    {
        long translatedRow = (long)(_originMode ? _scrollTop : 0) + row;
        long translatedColumn = (long)(_originMode ? EffectiveLeftMargin : 0) + col;
        int clampedRow = (int)Math.Clamp(translatedRow, _originMode ? _scrollTop : 0, _originMode ? _scrollBottom : Rows - 1);
        int clampedColumn = (int)Math.Clamp(translatedColumn, _originMode ? EffectiveLeftMargin : 0, _originMode ? EffectiveRightMargin : Columns - 1);
        _cursor.Set(clampedRow, clampedColumn, Rows, Columns);
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

    internal RenderSnapshot RentRenderSnapshot()
    {
        lock (_snapshotPoolGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            RenderSnapshot snapshot;
            if (_snapshotPoolHead is null)
            {
                snapshot = new RenderSnapshot();
            }
            else
            {
                snapshot = _snapshotPoolHead;
                _snapshotPoolHead = snapshot.NextPooled;
                snapshot.NextPooled = null;
            }

            snapshot.Activate(this);
            return snapshot;
        }
    }

    internal void ReturnRenderSnapshot(RenderSnapshot snapshot)
    {
        lock (_snapshotPoolGate)
        {
            if (_disposed != 0)
            {
                snapshot.ReleaseCapacity();
                return;
            }

            snapshot.NextPooled = _snapshotPoolHead;
            _snapshotPoolHead = snapshot;
        }
    }

    public void Dispose()
    {
        RenderSnapshot? pooled;
        lock (_snapshotPoolGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            pooled = _snapshotPoolHead;
            _snapshotPoolHead = null;
        }

        _screens.Dispose();
        while (pooled is not null)
        {
            RenderSnapshot next = pooled.NextPooled!;
            pooled.NextPooled = null;
            pooled.ReleaseCapacity();
            pooled = next;
        }
    }

    /// <summary>
    /// Per-frame render capture (B-lite): copies only the visible rows' cell
    /// slices instead of the whole arena. The raster reads ~viewport-rows x
    /// columns; a full-arena memcpy per content frame is pure overhead.
    /// </summary>
    public RenderSnapshot CaptureRenderSnapshotVisible(int sbStart = 0, int sbEnd = -1, int scrollOffset = 0)
    {
        var styles = StyleSet.CaptureStylesShared();
        var snapshot = RenderSnapshot.CaptureVisible(
            this,
            ActiveBuffer,
            _rowGenerations,
            styles,
            ScrollbackCount,
            IsAlternateScreenActive,
            CursorRow,
            CursorCol,
            CursorShape,
            CursorBlinking,
            CursorStyleIsDefault,
            CursorVisible,
            scrollOffset);
        snapshot.GlobalGeneration = Generation;
        snapshot.ViewTopAbsoluteRow = _totalScrolled;
        CaptureGraphics(snapshot, scrollOffset);

        int count = sbEnd - sbStart + 1;
        if (count > 0)
        {
            snapshot.CapturedSbStart = sbStart;
            var text = new string[count];
            BufferMemoryMetrics.ChangeSnapshotCapacity(0, (long)count * IntPtr.Size);
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


    public void MoveCursorToScreenColumn(int column) =>
        _cursor.Set(_cursor.Row, column, Rows, Columns);

    public void SetCursorColumn(int column)
    {
        int minimum = _originMode ? EffectiveLeftMargin : 0;
        int maximum = _originMode ? EffectiveRightMargin : Columns - 1;
        int absolute = (int)Math.Clamp((long)minimum + column, minimum, maximum);
        _cursor.Set(_cursor.Row, absolute, Rows, Columns);
    }

    public void SetCursorRow(int row)
    {
        int minimum = _originMode ? _scrollTop : 0;
        int maximum = _originMode ? _scrollBottom : Rows - 1;
        int absolute = (int)Math.Clamp((long)minimum + row, minimum, maximum);
        _cursor.Set(absolute, _cursor.Col, Rows, Columns);
    }

    public void SetOriginMode(bool enabled)
    {
        _originMode = enabled;
        HomeCursor();
        MarkAllRowsDirty();
    }

    public bool OriginMode => _originMode;


    private void GetDirectionalBounds(out int top, out int bottom, out int left, out int right)
    {
        top = _cursor.Row < _scrollTop ? 0 : _scrollTop;
        bottom = _cursor.Row > _scrollBottom ? Rows - 1 : _scrollBottom;
        left = _cursor.Col < EffectiveLeftMargin ? 0 : EffectiveLeftMargin;
        right = _cursor.Col > EffectiveRightMargin ? Columns - 1 : EffectiveRightMargin;
    }

    private void GetAbsoluteBounds(out int top, out int bottom, out int left, out int right)
    {
        top = _originMode ? _scrollTop : 0;
        bottom = _originMode ? _scrollBottom : Rows - 1;
        left = _originMode ? EffectiveLeftMargin : 0;
        right = _originMode ? EffectiveRightMargin : Columns - 1;
    }

    public void MoveCursorHorizontalRelative(int n)
    {
        GetAbsoluteBounds(out _, out _, out int left, out int right);
        int col = (int)Math.Clamp((long)_cursor.Col + n, left, right);
        _cursor.Set(_cursor.Row, col, Rows, Columns);
    }

    public void MoveCursorVerticalRelative(int n)
    {
        GetAbsoluteBounds(out int top, out int bottom, out _, out _);
        int row = (int)Math.Clamp((long)_cursor.Row + n, top, bottom);
        _cursor.Set(row, _cursor.Col, Rows, Columns);
    }

    public void MoveCursorBy(int dRow, int dCol)
    {
        GetDirectionalBounds(out int top, out int bottom, out int left, out int right);
        int row = dRow == 0 ? _cursor.Row : (int)Math.Clamp((long)_cursor.Row + dRow, top, bottom);
        int col = dCol == 0 ? _cursor.Col : (int)Math.Clamp((long)_cursor.Col + dCol, left, right);
        _cursor.Set(row, col, Rows, Columns);
    }

    public void CarriageReturn()
    {
        int left = EffectiveLeftMargin;
        int column = _cursor.Col >= left || _originMode ? left : 0;
        _cursor.Set(_cursor.Row, column, Rows, Columns);
    }

    public void MoveCursorBack(int n)
    {
        long remaining = Math.Max(1, n);
        int left = EffectiveLeftMargin;
        int right = EffectiveRightMargin;
        if (_cursor.Col < left)
            left = 0;

        bool reverseWrap = _autoWrap && (_reverseWraparound || _extendedReverseWraparound);
        if (reverseWrap && _cursor.WrapPending)
            remaining--;

        int row = _cursor.Row;
        int col = _cursor.Col;
        if (remaining > 0 && _autoWrap && _extendedReverseWraparound)
        {
            long withinRow = col - left;
            if (remaining <= withinRow)
            {
                col -= (int)remaining;
                remaining = 0;
            }

            if (remaining > 0)
            {
                int top = row >= _scrollTop && row <= _scrollBottom ? _scrollTop : 0;
                int bottom = row >= _scrollTop && row <= _scrollBottom ? _scrollBottom : Rows - 1;
                long width = right - left + 1L;
                long regionHeight = bottom - top + 1L;
                long position = (row - top) * width + col - left;
                long cycle = width * regionHeight;
                position = (position - remaining % cycle + cycle) % cycle;
                row = top + (int)(position / width);
                col = left + (int)(position % width);
            }
        }
        else
        {
            int top = row >= _scrollTop && row <= _scrollBottom ? _scrollTop : 0;
            while (remaining > 0)
            {
                long withinRow = col - left;
                if (remaining <= withinRow)
                {
                    col -= (int)remaining;
                    break;
                }

                remaining -= withinRow + 1;
                if (!_autoWrap || !_reverseWraparound || row <= top
                    || !ActiveBuffer.GetRowContinuesPrevious(row))
                {
                    col = left;
                    break;
                }

                row--;
                col = right;
            }
        }

        _cursor.Set(row, col, Rows, Columns);
    }


    public void EraseLine(int mode)
    {
        _eraser.EraseLine(ActiveBuffer, _cursor, Columns, mode, RespectIsoProtection);
        MarkRowDirty(_cursor.Row);
    }

    internal void EraseLineSelective(int mode)
    {
        _eraser.EraseLine(ActiveBuffer, _cursor, Columns, mode, respectProtection: !RespectIsoProtection);
        MarkRowDirty(_cursor.Row);
    }

    public void InsertChars(int count)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        int right = EffectiveRightMargin;
        if (col < EffectiveLeftMargin || col > right) return;
        ActiveBuffer.InsertColumns(row, col, right, count);
        MarkRowDirty(row);
    }

    public void DeleteChars(int count)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        int right = EffectiveRightMargin;
        if (col < EffectiveLeftMargin || col > right) return;
        ActiveBuffer.DeleteColumns(row, col, right, count);
        MarkRowDirty(row);
    }

    public void InsertColumns(int count) => EditColumnsAtCursor(count, insert: true);

    public void DeleteColumns(int count) => EditColumnsAtCursor(count, insert: false);

    /// <summary>Moves the cursor left, or shifts the horizontal scroll region right at its left margin.</summary>
    public void BackIndex()
    {
        int left = EffectiveLeftMargin;
        if (_cursor.Col != left)
        {
            MoveCursorBack(1);
            return;
        }
        if (_cursor.Row < _scrollTop || _cursor.Row > _scrollBottom)
            return;

        EditColumnRegion(left, EffectiveRightMargin, 1, insert: true);
    }

    /// <summary>Moves the cursor right, or shifts the horizontal scroll region left at its right margin.</summary>
    public void ForwardIndex()
    {
        int right = EffectiveRightMargin;
        if (_cursor.Col != right)
        {
            MoveCursorBy(0, 1);
            return;
        }
        if (_cursor.Row < _scrollTop || _cursor.Row > _scrollBottom)
            return;

        EditColumnRegion(EffectiveLeftMargin, right, 1, insert: false);
    }

    private void EditColumnsAtCursor(int count, bool insert)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        int right = EffectiveRightMargin;
        if (row < _scrollTop || row > _scrollBottom
            || col < EffectiveLeftMargin || col > right)
            return;

        count = (int)Math.Min((long)count, (long)right - col + 1);
        EditColumnRegion(col, right, count, insert);
    }

    private void EditColumnRegion(int left, int right, int count, bool insert)
    {
        bool followingRowHasLink = _scrollBottom + 1 < Rows
            && ActiveBuffer.GetRowContinuesPrevious(_scrollBottom + 1);
        for (int row = _scrollTop; row <= _scrollBottom; row++)
        {
            if (insert)
                ActiveBuffer.InsertColumns(row, left, right, count);
            else
            {
                ActiveBuffer.DeleteColumns(row, left, right, count);
                if (row + 1 < Rows)
                    ActiveBuffer.ClearRowContinuation(row + 1);
            }
        }

        NotifyColumnsScrolled(left, right, insert ? -count : count);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
        if (followingRowHasLink && !ActiveBuffer.GetRowContinuesPrevious(_scrollBottom + 1))
            MarkRowDirty(_scrollBottom + 1);
    }

    private void NotifyColumnsScrolled(int left, int right, int delta)
    {
        if (_screens.ActiveImages is { HasPlacements: true } images
            && images.OnColumnsScrolled(_scrollTop, _scrollBottom, left, right, delta, Rows) != 0)
            BumpGraphicsGeneration();
    }


    public void EraseCharacters(int count)
    {
        if (count <= 0) return;

        int row = _cursor.Row;
        int start = Math.Clamp(_cursor.Col, 0, Columns - 1);
        // Saturated parameters (CSI 99999999999 X) must not overflow start + count.
        int endExclusive = (int)Math.Min((long)Columns, (long)start + count);

        if (RespectIsoProtection)
            ActiveBuffer.ClearUnprotectedColumns(row, start, endExclusive);
        else
            ActiveBuffer.ClearColumns(row, start, endExclusive);

        MarkRowDirty(row);
    }

    public void InsertLines(int count) => InsertOrDeleteLines(count, insert: true);

    public void DeleteLines(int count) => InsertOrDeleteLines(count, insert: false);

    private void InsertOrDeleteLines(int count, bool insert)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int bottom = _scrollBottom;
        int left = EffectiveLeftMargin;
        int right = EffectiveRightMargin;
        if (row < _scrollTop || row > bottom || _cursor.Col < left || _cursor.Col > right)
            return;

        int height = bottom - row + 1;
        count = Math.Min(count, height);
        bool partialWidth = left != 0 || right != Columns - 1;
        int delta = insert ? -count : count;
        if (partialWidth)
        {
            ActiveBuffer.ScrollRectangle(row, bottom, left, right, delta);
        }
        else if (count == height)
        {
            for (int r = row; r <= bottom; r++)
                ActiveBuffer.ClearRow(r);
            ActiveBuffer.ClearRowContinuation(bottom + 1);
        }
        else if (insert)
        {
            ActiveBuffer.ShiftRegionDown(row, bottom, count);
        }
        else
        {
            ActiveBuffer.ShiftRegionUp(row, bottom, count);
        }

        if (_screens.ActiveImages is { HasPlacements: true } images)
        {
            int changed = partialWidth
                ? images.OnRectangleScrolled(row, bottom, left, right, delta, Rows)
                : images.OnRegionScrolled(row, bottom, delta, Rows);
            if (changed != 0)
                BumpGraphicsGeneration();
        }
        BumpIdentity(row, height);
    }

    public void EraseDisplay(int mode)
    {
        var reset = _eraser.EraseDisplay(ActiveBuffer, _cursor, Rows, Columns, mode, RespectIsoProtection, selective: false);
        if (reset)
        {
            _cursor.Reset();
        }
        if (mode == 2)
        {
            _totalScrolled = 0;
            MarkAllRowsDirty();
            // ED 2 discards history and image placements even when ISO-protected text survives.
            DiscardGraphicsHistory(wholeRing: true);
            if (!_isAlternate)
            {
                BumpCoordinateEpoch();
                RaiseHistoryInvalidated(HistoryDiscardReason.Cleared);
            }
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

    internal void EraseDisplaySelective(int mode)
    {
        _eraser.EraseDisplay(ActiveBuffer, _cursor, Rows, Columns, mode, respectProtection: !RespectIsoProtection, selective: true);
        if (mode == 2)
            MarkAllRowsDirty();
        else if (mode == 0)
            MarkRowRangeDirty(_cursor.Row, Rows - _cursor.Row);
        else if (mode == 1)
            MarkRowRangeDirty(0, _cursor.Row + 1);
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
        var buf = _screens.Active;
        var hot = buf.GetRowCells(row);
        // An out-of-range row reads as blank cells (as GetCell reports for it).
        if (hot.IsEmpty)
            return new string(' ', Columns);
        return RowText.Build(hot, buf.GetRowColdCells(row), 0, Columns - 1, ContinuationPolicy.SpaceCell);
    }

    private BufferTextWriter CreateWriter()
    {
        return new BufferTextWriter(this, _cursor, _eraser, StyleSet);
    }

    // per-row versioning and dirty arrays removed

    internal void MarkRowDirty(int row)
    {
        if (row < 0 || row >= Rows) return;
        unchecked { _rowGenerations[row]++; }
        unchecked { _globalGeneration++; }
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(start + count, Rows);
        for (int i = start; i < end; i++)
        {
            unchecked { _rowGenerations[i]++; }
        }
        unchecked { _globalGeneration += (ulong)(end - start); }
    }

    private void MarkAllRowsDirty()
    {
        for (int i = 0; i < Rows; i++)
        {
            unchecked { _rowGenerations[i]++; }
        }
        unchecked { _globalGeneration += (ulong)Rows; }
    }

    /// <summary>
    /// Invalidates cached row composition after a palette change (OSC 4/10/11).
    /// Cell content is unchanged; only resolved colors differ.
    /// </summary>
    public void InvalidateRowsForPaletteChange() => MarkAllRowsDirty();


    /// <summary>
    /// Bumps only the identity generations for [start, start+count). Used by
    /// scroll operations, which must invalidate the classification/glyph
    /// caches for the whole region while handling motion epochs themselves
    /// (rotation + exposed-row bump) so the renderer can skip moved rows.
    /// </summary>
    private void BumpIdentity(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(start + count, Rows);
        for (int i = start; i < end; i++)
            unchecked { _rowGenerations[i]++; }
        unchecked { _globalGeneration += (ulong)(end - start); }
    }

    public ulong GetRowGeneration(int row)
    {
        if (row < 0 || row >= Rows) return 0;
        return _rowGenerations[row];
    }

    public ReadOnlySpan<ulong> RowGenerations => _rowGenerations.AsSpan(0, Rows);

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
        BumpCoordinateEpoch();
        DiscardAllGraphicsPlacements();
        _cursor.Reset();
        MarkAllRowsDirty();
        RaiseHistoryInvalidated(HistoryDiscardReason.Reset);
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
