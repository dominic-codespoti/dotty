using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Terminal.Adapter.Buffer;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Very small screen model for now: stores visible lines and a simple scrollback.
/// Designed to be called from parser callbacks; it is not thread-safe by itself.
/// </summary>
public class TerminalBuffer
{
    public object SyncRoot { get; } = new object();

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

    private ulong[] _rowGenerations = Array.Empty<ulong>();
    private ulong _globalGeneration;
    private List<string> _hyperlinks = new List<string> { string.Empty };
    private Dictionary<string, ushort> _hyperlinkLookup = new Dictionary<string, ushort>();

    public TerminalBuffer(int rows = 24, int columns = 80, int scrollbackCapacity = 10000)
    {
        Rows = rows;
        Columns = columns;
        _rowGenerations = new ulong[rows];
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
        unchecked { _globalGeneration++; }
    }

    public void SetAlternateScreen(bool active)
    {
        _isAlternate = active;
        _screens.SetAlternate(active);
    }

    public bool IsAlternateScreenActive => _isAlternate;

    public void SetScrollRegion(int top, int bottom)
    {
        int newTop = Math.Max(0, top);
        int newBottom = Math.Clamp(bottom, 0, Rows - 1);

        if (newTop < newBottom)
        {
            _scrollTop = newTop;
            _scrollBottom = newBottom;
        }
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
    private bool[]? _tabStops;
    internal bool _autoWrap = true; // DECAWM default is enabled
    private bool _bracketedPaste = false;

    internal bool _clearLineOnNextWrite = false;

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

    public void ClearScrollback()
    {
        _totalScrolled = 0;
    }

    /// <summary>
    /// Aggressively reduces scrollback to a smaller size while keeping the session running.
    /// This is used when a tab becomes inactive to free memory while preserving some context.
    /// The Screen's ring buffer cannot be partially trimmed; this only adjusts the count.
    /// </summary>
    public void TrimScrollback(int maxLines)
    {
        if (maxLines < 0) maxLines = 0;
        _totalScrolled = Math.Min(_totalScrolled, maxLines);
    }

    public void FullReset()
    {
        // RIS - Reset to Initial State
        _screens.ClearAll();
        _cursor.Reset();
        _bracketedPaste = false;
        _hasSavedCursor = false;
        _clearLineOnNextWrite = false;
        InitializeTabStops();
        _totalScrolled = 0;
        MarkAllRowsDirty();
    }

    public void ScrollUpLines(int n)
    {
        if (n <= 0) return;
        ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, n);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
    }

    public void ScrollDownLines(int n)
    {
        if (n <= 0) return;
        ActiveBuffer.ScrollDownRegion(_scrollTop, _scrollBottom, n);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
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
        // When toggling origin mode, ensure the cursor is in a sensible place.
        if (_originMode)
        {
            if (_cursor.Row < _scrollTop || _cursor.Row > _scrollBottom)
            {
                _cursor.Set(_scrollTop, _cursor.Col, Rows, Columns);
            }
        }
        else
        {
            // Ensure cursor remains within full-screen bounds
            _cursor.Set(Math.Clamp(_cursor.Row, 0, Rows - 1), _cursor.Col, Rows, Columns);
        }
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
        _clearLineOnNextWrite = true;
    }

    public void LineFeed()
    {
        // If the cursor is at the bottom of the scroll region, scroll only
        // that region. This implements DECSTBM semantics used by applications
        // like Neovim.
        if (_cursor.Row == _scrollBottom)
        {
            ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, 1);
            if (_scrollTop == 0)
                unchecked { _totalScrolled++; }

            MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
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
        // If the cursor is at the top of the scroll region, scroll the region
        // down by one line (DEC RI). Otherwise move the cursor up.
        if (_cursor.Row == _scrollTop)
        {
            ActiveBuffer.ScrollDownRegion(_scrollTop, _scrollBottom, 1);
            // Signal movement so renderer can update caches
            MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
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
        // Shift cells to the right
        for (int c = Columns - 1; c >= col + count; c--)
        {
            ref var dst = ref ActiveBuffer.GetCellRef(row, c);
            var src = ActiveBuffer.GetCell(row, c - count);
            dst = src;
            var srcCold = ActiveBuffer.GetColdCell(row, c - count);
            ActiveBuffer.GetColdCellRef(row, c) = srcCold;
        }
        // Clear inserted region
        for (int c = col; c < Math.Min(Columns, col + count); c++)
        {
            ActiveBuffer.ClearCell(row, c);
        }
        MarkRowDirty(row);
    }

    public void DeleteChars(int count)
    {
        if (count <= 0) return;
        int row = _cursor.Row;
        int col = _cursor.Col;
        for (int c = col; c < Columns - count; c++)
        {
            ref var dst = ref ActiveBuffer.GetCellRef(row, c);
            var src = ActiveBuffer.GetCell(row, c + count);
            dst = src;
            var srcCold = ActiveBuffer.GetColdCell(row, c + count);
            ActiveBuffer.GetColdCellRef(row, c) = srcCold;
        }
        // Clear trailing cells
        for (int c = Math.Max(0, Columns - count); c < Columns; c++)
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
            // clear region
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
        }
        // clear inserted lines
        for (int r = row; r < row + count; r++)
        for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
        MarkRowRangeDirty(row, bottom - row + 1);
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
            // clear region
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
        }
        // clear trailing lines
        for (int r = bottom - count + 1; r <= bottom; r++)
        for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
        MarkRowRangeDirty(row, bottom - row + 1);
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

    internal void ScrollUp(int lines)
    {
        if (lines <= 0) return;

        ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, lines);
        if (_scrollTop == 0)
            unchecked { _totalScrolled += Math.Min(lines, _scrollBottom - _scrollTop + 1); }
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
    }

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
        unchecked { _globalGeneration++; }
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(start + count, _rowGenerations.Length);
        for (int i = start; i < end; i++)
            unchecked { _rowGenerations[i]++; }
        unchecked { _globalGeneration += (ulong)(end - start); }
    }

    private void MarkAllRowsDirty()
    {
        for (int i = 0; i < _rowGenerations.Length; i++)
            unchecked { _rowGenerations[i]++; }
        unchecked { _globalGeneration += (ulong)_rowGenerations.Length; }
    }

    public ulong GetRowGeneration(int row)
    {
        if (row < 0 || row >= _rowGenerations.Length) return 0;
        return _rowGenerations[row];
    }

    public ReadOnlySpan<ulong> RowGenerations => _rowGenerations;

    /// <summary>
    /// Notify the active screen that a render cycle is starting so it can
    /// reset any per-render debug state (such as dedupe caches used by
    /// DumpRowRange/DumpRowDetail).
    /// </summary>
    public void MarkRender()
    {
        ActiveBuffer.MarkRender();
    }

    public void ClearScreen()
    {
        _screens.ClearAll();
        _cursor.Reset();
        _clearLineOnNextWrite = false;
        MarkAllRowsDirty();
    }
}
