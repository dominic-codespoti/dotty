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
    private readonly ScreenManager _screens;
    private readonly CursorController _cursor = new();
    private readonly BufferEraser _eraser = new();
    private BufferTextWriter _writer;
    private int _scrollTop = 0;
    private int _scrollBottom;
    private bool _originMode = false;
    private bool _isAlternate = false;
    private int _scrollGeneration = 0;
    // Simple in-memory scrollback storage. Stores textual rows that have
    // scrolled off the top of the active screen. This is intentionally
    // lightweight; consumers that need richer history should snapshot
    // cells via `GetCell`/`GetRowText` or extend this with attributes.
    private readonly System.Collections.Generic.List<string> _scrollback = new System.Collections.Generic.List<string>();
    private int _maxScrollback = 10000;
    private bool[]? _tabStops;
    private bool _autoWrap = true; // DECAWM default is enabled
    private bool _bracketedPaste = false;
    public int ScrollbackCount => _scrollback.Count;
    public IReadOnlyList<string> GetScrollbackLines() => _scrollback.AsReadOnly();
    public int MaxScrollback
    {
        get => _maxScrollback;
        set => _maxScrollback = Math.Max(0, value);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow => _cursor.Row;
    public int CursorCol => _cursor.Col;
    public bool CursorVisible => _cursor.Visible;

    private bool _clearLineOnNextWrite;

    public TerminalBuffer(int rows = 24, int columns = 80)
    {
        Rows = rows;
        Columns = columns;
        _screens = new ScreenManager(Rows, Columns);
        // Per-row versioning and caching removed: renderer performs full-frame
        // composition each render. Keep minimal state here to preserve
        // compatibility with BufferTextWriter delegate signatures.
        _cursor.SetSize(Rows, Columns);
        _writer = CreateWriter();
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            ClearScreen();
        InitializeTabStops();
    }

    private void InitializeTabStops()
    {
        _tabStops = new bool[Columns];
        for (int c = 0; c < Columns; c++) _tabStops[c] = (c % 8) == 0;
    }

    public void SetAutoWrap(bool enabled)
    {
        _autoWrap = enabled;
    }

    public void SetBracketedPasteMode(bool enabled)
    {
        _bracketedPaste = enabled;
    }

    public bool BracketedPasteMode => _bracketedPaste;

    public bool AutoWrap => _autoWrap;

    public void SetTabStopAt(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        if (col < 0 || col >= Columns) return;
        _tabStops[col] = true;
    }

    public void ClearTabStopAt(int col)
    {
        if (_tabStops == null) InitializeTabStops();
        if (col < 0 || col >= Columns) return;
        _tabStops[col] = false;
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
            if (_tabStops[c]) return c;
        }
        // fallback to next multiple of 8 or end
        int tabStop = ((col / 8) + 1) * 8;
        return Math.Min(Columns - 1, tabStop);
    }

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
        BumpScrollGeneration("RestoreCursor");
    }

    private Screen ActiveBuffer => _screens.Active;

    // Internal accessor for tests to inspect the active screen without
    // resorting to reflection. Marked internal to avoid widening the public API.
    internal Screen ActiveScreenForTests => ActiveBuffer;

    public void ClearScreen()
    {
        _screens.ClearAll();
        _cursor.Reset();
        _clearLineOnNextWrite = false;
        MarkAllRowsDirty();
        BumpScrollGeneration("ClearScreen");
    }

    public void ClearScrollback()
    {
        // Clear the preserved history but do not modify the visible screen.
        _scrollback.Clear();
        BumpScrollGeneration("ClearScrollback");
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
        BumpScrollGeneration("SetOriginMode");
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
            // Capture the top-most line(s) of the scroll region before they
            // are moved out by the region scroll so we can preserve them in
            // the scrollback history.
            try
            {
                int lines = 1;
                for (int r = _scrollTop; r <= Math.Min(_scrollBottom, _scrollTop + lines - 1); r++)
                {
                    _scrollback.Add(GetRowText(r));
                }
                if (_scrollback.Count > _maxScrollback)
                {
                    int remove = _scrollback.Count - _maxScrollback;
                    _scrollback.RemoveRange(0, remove);
                }
            }
            catch { }

            ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, 1);

            // After a region scroll, clear the newly revealed bottom row(s)
            // within the region so stale UI (command palettes / statuslines)
            // does not remain visible.
            try
            {
                int lines = 1;
                int clearStart = Math.Max(_scrollTop, _scrollBottom - lines + 1);
                int clearEnd = _scrollBottom;
                for (int rr = clearStart; rr <= clearEnd; rr++)
                {
                    for (int cc = 0; cc < Columns; cc++) ActiveBuffer.ClearCell(rr, cc);
                }
            }
            catch { }

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
            BumpScrollGeneration("ReverseIndex");
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

    private void ScrollUp(int lines)
    {
        if (lines <= 0) return;

        // Capture the rows that will be scrolled out (top `lines` rows).
        try
        {
            for (int r = 0; r < Math.Min(lines, Rows); r++)
            {
                _scrollback.Add(GetRowText(r));
            }
            // Enforce a simple cap to avoid unbounded memory growth.
            if (_scrollback.Count > _maxScrollback)
            {
                int remove = _scrollback.Count - _maxScrollback;
                _scrollback.RemoveRange(0, remove);
            }
        }
        catch { }

        ActiveBuffer.ScrollUp(lines);
        // Ensure newly revealed rows at the bottom are cleared so stale
        // content (e.g., statuslines) does not persist after a scroll.
        try
        {
            int start = Math.Max(0, Rows - lines);
            for (int r = start; r < Rows; r++)
            {
                for (int c = 0; c < Columns; c++) ActiveBuffer.ClearCell(r, c);
            }
        }
        catch { }

        BumpScrollGeneration($"ScrollUp({lines})");
    }

    public string GetCurrentDisplay(bool showCursor = false, string? promptPrefix = null)
    {
        var sb = new StringBuilder();
        var buf = ActiveBuffer;
        for (int i = 0; i < Rows; i++)
        {
            for (int j = 0; j < Columns; j++)
            {
                var cell = buf.GetCell(i, j);
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
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Return a copy of the cell at the given coordinates. Safe for out-of-range queries.
    public Cell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
        {
            return new Cell { Grapheme = " ", Width = 1, Bold = false };
        }
        var c = ActiveBuffer.GetCell(row, col);
        return c;
    }

    public void SetAlternateScreen(bool enable)
    {
        _screens.SetAlternate(enable);
        _isAlternate = enable;
        _cursor.Reset();
        MarkAllRowsDirty();
        BumpScrollGeneration("SetAlternateScreen");
        // Reset scroll region to full when switching screens
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    // Expose whether the alternate screen buffer is active. This allows
    // the UI layer to detect mode changes and invalidate renderer caches
    // when an application switches between main/alternate screens.
    public bool IsAlternateScreenActive => _isAlternate;

    public void SetCursorVisible(bool visible)
    {
        _cursor.SetVisible(visible);
    }


    public void Resize(int rows, int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        if (rows == Rows && columns == Columns)
        {
            return;
        }

        _screens.Resize(rows, columns);

        Rows = rows;
        Columns = columns;
        _cursor.SetSize(Rows, Columns);
        _writer = CreateWriter();
        MarkAllRowsDirty();
        // Reset scroll region on resize
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    public void SetScrollRegion(int top1Based, int bottom1Based)
    {
        // DECSTBM: CSI top ; bottom r
        // Missing params default to 1 and Rows. Many parsers pass 0 for missing.

        // Both omitted -> reset full screen margins
        if (top1Based == 0 && bottom1Based == 0)
        {
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            _cursor.Set(0, 0, Rows, Columns);
            return;
        }

        // Apply defaults for omitted params (0 or negative -> default)
        if (top1Based <= 0) top1Based = 1;
        if (bottom1Based <= 0) bottom1Based = Rows;

        // Convert 1-based inputs to 0-based indices and clamp
        int top = Math.Clamp(top1Based - 1, 0, Rows - 1);
        int bottom = Math.Clamp(bottom1Based - 1, 0, Rows - 1);

        if (top > bottom)
        {
            // Invalid region -> reset to full screen
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            _cursor.Set(0, 0, Rows, Columns);
            return;
        }

        _scrollTop = top;
        _scrollBottom = bottom;

        // Correct cursor homing semantics: when origin mode is enabled the
        // cursor should be moved to the scroll-top; otherwise home to row 0.
        int homeRow = _originMode ? _scrollTop : 0;
        _cursor.Set(homeRow, 0, Rows, Columns);

        // Region change invalidates rows so the renderer repaints.
        MarkAllRowsDirty();
    }

    public string GetRowText(int row)
    {
        var sb = new StringBuilder();
        
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
        return new BufferTextWriter(
            _cursor,
            _eraser,
            () => ActiveBuffer,
            () => Rows,
            () => Columns,
            ScrollUp,
            () => _autoWrap,
            (int c) => GetNextTabStopFrom(c),
            () => _clearLineOnNextWrite,
            v => _clearLineOnNextWrite = v,
            CarriageReturn,
            LineFeed,
            MarkRowDirty);
    }

    // per-row versioning and dirty arrays removed

    private void MarkRowDirty(int row)
    {
        // Per-row versions removed. Signal a generation bump so renderers
        // can detect that something changed and perform a full repaint.
        BumpScrollGeneration($"MarkRowDirty({row})");
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        // Per-row versions removed — bump global generation for compatibility.
        BumpScrollGeneration($"MarkRowRangeDirty({start},{count})");
    }

    private void MarkAllRowsDirty()
    {
        // Per-row versions removed — bump global generation.
        BumpScrollGeneration("MarkAllRowsDirty");
    }

    // per-row version shifting helpers removed

    private void BumpScrollGeneration(string reason)
    {
        unchecked { _scrollGeneration++; }
    }

    // Expose a lightweight generation counter that increments when operations
    // occur which move or erase visual rows. Consumers (renderer) can use this
    // to detect scroll/erase events that require compositor cache adjustments.
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

    // Focused input logging: writes compact, replayable records of inputs
    // sent to the buffer when `DOTTY_DEBUG_INPUT_LOG` is enabled.
    private static string EscapeForLog(ReadOnlySpan<char> s)
    {
        var str = s.ToString();
        return str.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
