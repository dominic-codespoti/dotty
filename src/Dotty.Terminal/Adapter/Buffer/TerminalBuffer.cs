using System;
using System.Text;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Very small screen model for now: stores visible lines and a simple scrollback.
/// Designed to be called from parser callbacks; it is not thread-safe by itself.
/// </summary>
public class TerminalBuffer
{
    public Screen ActiveBuffer => _screens.Active;

    internal Screen ActiveScreenForTests => ActiveBuffer;

    private readonly ScreenManager _screens;
    private readonly CursorController _cursor = new();
    private readonly BufferEraser _eraser = new();
    private BufferTextWriter _writer;
    private int _scrollTop = 0;
    private int _scrollBottom;
    private bool _originMode;
    private bool _isAlternate;

    private int _scrollGeneration = 0;
    private List<string> _hyperlinks = new List<string> { string.Empty };

    public TerminalBuffer(int rows = 24, int columns = 80)
    {
        Rows = rows;
        Columns = columns;
        _screens = new ScreenManager(rows, columns);
        _writer = CreateWriter();
        _scrollBottom = rows - 1;
        InitializeTabStops();
    }

    public void Resize(int rows, int cols)
    {
        // Maintain scroll region if it covered the entire screen
        bool fullScreenScroll = (_scrollTop == 0 && _scrollBottom == Rows - 1);

        Rows = rows;
        Columns = cols;
        _screens.Resize(rows, cols);

        if (fullScreenScroll)
        {
            _scrollBottom = rows - 1;
        }
        else
        {
            _scrollBottom = Math.Min(_scrollBottom, rows - 1);
        }
        _scrollTop = Math.Min(_scrollTop, _scrollBottom);
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
        int idx = _hyperlinks.IndexOf(uri);
        if (idx < 0)
        {
            idx = _hyperlinks.Count;
            _hyperlinks.Add(uri);
        }
        return (ushort)idx;
    }

    // Simple in-memory scrollback storage. Stores textual rows that have
    // scrolled off the top of the active screen. This is intentionally
    // lightweight; consumers that need richer history should snapshot
    // cells via `GetCell`/`GetRowText` or extend this with attributes.
    private ScrollbackLine[] _scrollbackRing = System.Array.Empty<ScrollbackLine>();
    private int _scrollbackHead = 0;
    private int _scrollbackCount = 0;
    private int _maxScrollback = 10000;
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
        BumpScrollGeneration();
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
        // Clear the preserved history but do not modify the visible screen.
        _scrollbackCount = 0; _scrollbackHead = 0;
        BumpScrollGeneration();
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
        _scrollbackCount = 0; _scrollbackHead = 0;
        MarkAllRowsDirty();
        BumpScrollGeneration();
    }

    public void ScrollUpLines(int n)
    {
        if (n <= 0) return;
        ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, n);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
        BumpScrollGeneration();
    }

    public void ScrollDownLines(int n)
    {
        if (n <= 0) return;
        ActiveBuffer.ScrollDownRegion(_scrollTop, _scrollBottom, n);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
        BumpScrollGeneration();
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
        public readonly char[] Buffer;
        public readonly int Length;
        public ScrollbackLine(char[] buffer, int length) { Buffer = buffer; Length = length; }
        public override string ToString() => Buffer == null ? string.Empty : new string(Buffer, 0, Length);
    }
    public int ScrollbackCount => _scrollbackCount;
    public IReadOnlyList<string> GetScrollbackLines()
    {
        string[] lines = new string[_scrollbackCount];
        for (int i = 0; i < _scrollbackCount; i++)
        {
            lines[i] = _scrollbackRing[(_scrollbackHead + i) % _scrollbackRing.Length].ToString();
        }
        return lines;
    }
    public int MaxScrollback
    {
        get => _maxScrollback;
        set => _maxScrollback = Math.Max(0, value);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public Cell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            var c = new Cell { Width = 1 };
            c.Rune = 32;
            return c;
        }
        var c2 = ActiveBuffer.GetCell(row, col);
        return c2;
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
        BumpScrollGeneration();
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
            // Capture the top-most line of the scroll region before it's
            // scrolled out, preserving it in scrollback history.
            if (_scrollTop == 0)
            {
                AddToScrollback(_scrollTop);
            }

            ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, 1);

            // Mark the whole region dirty so the renderer repaints in-place.
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
            BumpScrollGeneration();
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
            Foreground = ToColor(foreground),
            Background = ToColor(background),
            Bold = bold,
        };

        WriteText(text, attributes);
    }

    private static SgrColor? ToColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        return new SgrColor(hex);
    }

    public void WriteText(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        _writer.WriteText(text, in attributes);
    }

    internal void ScrollUp(int lines)
    {
        if (lines <= 0) return;

        if (_scrollTop == 0)
        {
            int rowsToCapture = Math.Min(lines, _scrollBottom + 1);
            for (int r = 0; r < rowsToCapture; r++)
            {
                AddToScrollback(r);
            }
        }

        ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, lines);
        MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);

        BumpScrollGeneration();
    }

    /// <summary>
    /// Adds a row to scrollback, trimming old entries if over capacity.
    /// Uses a more efficient approach than RemoveRange(0, n) for trimming.
    /// </summary>
    private void AddToScrollback(int row)
    {
        if (_maxScrollback <= 0) return;

        if (_scrollbackRing == null || _scrollbackRing.Length != _maxScrollback)
        {
            var newRing = new ScrollbackLine[_maxScrollback];
            if (_scrollbackRing != null && _scrollbackCount > 0)
            {
                int copyCount = System.Math.Min(_scrollbackCount, _maxScrollback);
                for (int i = 0; i < copyCount; i++)
                {
                    newRing[i] = _scrollbackRing[(_scrollbackHead + i) % _scrollbackRing.Length];
                }
                _scrollbackCount = copyCount;
                _scrollbackHead = 0;
            }
            _scrollbackRing = newRing;
        }

        int targetIdx = (_scrollbackHead + _scrollbackCount) % _scrollbackRing.Length;
        
        if (_scrollbackCount == _scrollbackRing.Length)
        {
            targetIdx = _scrollbackHead;
            _scrollbackHead = (_scrollbackHead + 1) % _scrollbackRing.Length;
        }
        else
        {
            _scrollbackCount++;
        }

        _scrollbackRing[targetIdx] = GetRowTextFast(row, _scrollbackRing[targetIdx].Buffer);
    }

    private ScrollbackLine GetRowTextFast(int row, char[] existingArr)
    {
        var buf = _screens.Active;
        int lastCol = -1;
        for (int j = Columns - 1; j >= 0; j--)
        {
            var cell = buf.GetCell(row, j);
            if (!cell.IsContinuation && cell.Rune != 0 && cell.Rune != 32)
            {
                lastCol = j;
                break;
            }
        }
        if (lastCol < 0) return new ScrollbackLine(existingArr ?? System.Array.Empty<char>(), 0);
        
        char[] arr = existingArr;
        if (arr == null || arr.Length < lastCol + 1)
        {
            arr = new char[System.Math.Max(lastCol + 1, Columns)];
        }
        int writeIdx = 0;
        for (int j = 0; j <= lastCol; j++)
        {
            var cell = buf.GetCell(row, j);
            if (cell.IsContinuation || cell.Rune == 0)
            {
                arr[writeIdx++] = ' ';
            }
            else
            {
                if (writeIdx + 2 > arr.Length) {
                    var newArr = new char[arr.Length * 2];
                    System.Array.Copy(arr, newArr, arr.Length);
                    arr = newArr;
                }
                
                if (cell.Rune <= 0xFFFF) {
                    arr[writeIdx++] = (char)cell.Rune;
                } else {
                    var utf16 = System.Text.Rune.TryCreate((int)cell.Rune, out var r) ? r : new System.Text.Rune(0xFFFD);
                    writeIdx += utf16.EncodeToUtf16(arr.AsSpan(writeIdx));
                }
            }
        }
        return new ScrollbackLine(arr, writeIdx);
    }

    public string GetRowText(int row)
    {
        // Pre-allocate for typical row (mostly single-width chars)
        using var sb = ZStr.CreateStringBuilder(Columns);
        var buf = _screens.Active;
        for (int j = 0; j < Columns; j++)
        {
            var cell = buf.GetCell(row, j);
            if (cell.IsContinuation)
            {
                sb.Append(' ');
            }
            else if (string.IsNullOrEmpty(cell.Grapheme))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(cell.Grapheme);
            }
        }

        return sb.ToString();
    }

    private BufferTextWriter CreateWriter()
    {
        return new BufferTextWriter(this, _cursor, _eraser);
    }

    // per-row versioning and dirty arrays removed

    internal void MarkRowDirty(int row)
    {
        // Per-row versions removed. Signal a generation bump so renderers
        // can detect that something changed and perform a full repaint.
        BumpScrollGeneration();
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        // Per-row versions removed — bump global generation for compatibility.
        BumpScrollGeneration();
    }

    private void MarkAllRowsDirty()
    {
        BumpScrollGeneration();
    }

    // per-row version shifting helpers removed

    private void BumpScrollGeneration()
    {
        unchecked { _scrollGeneration++; }
    }

    public int ScrollGeneration => _scrollGeneration;

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
        BumpScrollGeneration();
    }
}
