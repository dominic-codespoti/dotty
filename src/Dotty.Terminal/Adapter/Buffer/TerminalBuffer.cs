using System;
using System.Globalization;
using System.Text;

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
    private int[] _rowVersions;
    private bool[] _dirtyRows;
    private int _scrollTop = 0;
    private int _scrollBottom;
    private bool _originMode = false;
    private bool _isAlternate = false;
    private int _scrollGeneration = 0;

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
        _rowVersions = new int[Rows];
        _dirtyRows = new bool[Rows];
        _cursor.SetSize(Rows, Columns);
        _writer = CreateWriter();
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            ClearScreen();
    }

    private Screen ActiveBuffer => _screens.Active;

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
        ClearScreen();
    }

    public void SetCursor(int row, int col)
    {
        // Diagnostic: log cursor moves when they affect the bottom of the screen
        // so we can trace statusline updates coming from the parser/adapter.
        try
        {
            if (row >= Rows - 6)
            {
                Console.WriteLine($"[SetCursor] in=({row},{col}) originMode={_originMode} scroll=({_scrollTop},{_scrollBottom})");
            }
        }
        catch { }
        // When origin mode (DECOM) is enabled, cursor coordinates are relative
        // to the current scroll region. The adapter passes 0-based params
        // (converted from 1-based by the parser adapter), so we need to
        // translate them into absolute coordinates when origin mode is on.
        if (_originMode)
        {
            int absRow = Math.Clamp(_scrollTop + row, 0, Rows - 1);
            _cursor.Set(absRow, Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
        else
        {
            _cursor.Set(Math.Clamp(row, 0, Rows - 1), Math.Clamp(col, 0, Columns - 1), Rows, Columns);
        }
    }

    public void SetOriginMode(bool enabled)
    {
        _originMode = enabled;
        try { Console.WriteLine($"[ORIGIN] set: enabled={_originMode} scroll=({_scrollTop},{_scrollBottom}) cursor=({_cursor.Row},{_cursor.Col})"); } catch { }
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
        _cursor.MoveBy(dRow, dCol, Rows, Columns);
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
            Console.WriteLine($"[LF] scroll region top={_scrollTop} bottom={_scrollBottom} cursor=({_cursor.Row},{_cursor.Col})");
            ActiveBuffer.ScrollUpRegion(_scrollTop, _scrollBottom, 1);
            // Shift the row-version numbers up within the same region so that
            // per-row caches remain properly aligned with their logical rows.
            // This prevents cache aliasing where two logical rows end up
            // referencing the same bitmap contents after a region scroll.
            ShiftRowVersionsUpRegion(_scrollTop, _scrollBottom, 1);

            // Mark the whole region dirty so the renderer repaints in-place.
            MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1);
            return;
        }

        // If the cursor has escaped below the scroll-bottom, ignore LF.
        // Some apps (Neovim) intentionally write the statusline on the last
        // row which is outside the scroll region and expect LF to be ignored.
        if (_cursor.Row > _scrollBottom)
        {
            try { Console.WriteLine($"[LF-IGNORED] cursor=({_cursor.Row},{_cursor.Col}) scrollBottom={_scrollBottom}"); } catch { }
            return;
        }

        // Otherwise behave like a normal line feed (move cursor down)
        Console.WriteLine($"[LF] move down cursor=({_cursor.Row},{_cursor.Col}) region=({_scrollTop},{_scrollBottom})");
        _cursor.MoveBy(1, 0, Rows, Columns);
    }

    public void EraseLine(int mode)
    {
        Console.WriteLine($"[EraseLine] row={_cursor.Row} mode={mode}");
        _eraser.EraseLine(ActiveBuffer, _cursor, Columns, mode);
        MarkRowDirty(_cursor.Row);
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
        ActiveBuffer.ScrollUp(lines);
        ShiftRowVersionsUp(lines);
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
        ResizeRowVersions(rows);
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
            Console.WriteLine($"[DECSTBM] reset full (omitted params)");
            _cursor.Set(0, 0, Rows, Columns);
            return;
        }

        // Apply defaults for omitted params (0 or negative -> default)
        if (top1Based <= 0) top1Based = 1;
        if (bottom1Based <= 0) bottom1Based = Rows;

        // Convert 1-based inputs to 0-based indices and clamp
        int top = Math.Clamp(top1Based - 1, 0, Rows - 1);
        int bottom = Math.Clamp(bottom1Based - 1, 0, Rows - 1);

        if (top >= bottom)
        {
            // Invalid region -> reset to full screen
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            Console.WriteLine($"[DECSTBM] reset full: top=0 bottom={_scrollBottom} (raw {top1Based};{bottom1Based})");
            _cursor.Set(0, 0, Rows, Columns);
            return;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        Console.WriteLine($"[DECSTBM] set: top={_scrollTop} bottom={_scrollBottom} origin={_originMode} (raw {top1Based};{bottom1Based})");

        // Correct cursor homing semantics: when origin mode is enabled the
        // cursor should be moved to the scroll-top; otherwise home to row 0.
        int homeRow = _originMode ? _scrollTop : 0;
        _cursor.Set(homeRow, 0, Rows, Columns);
        try { Console.WriteLine($"[HOME] origin={_originMode} row={homeRow}"); } catch { }

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
            () => _clearLineOnNextWrite,
            v => _clearLineOnNextWrite = v,
            CarriageReturn,
            LineFeed,
            MarkRowDirty);
    }

    public int GetRowVersion(int row)
    {
        if (row < 0 || row >= _rowVersions.Length)
        {
            return 0;
        }

        return _rowVersions[row];
    }

    public bool[] DirtyRows => _dirtyRows;

    public void ClearDirtyRows()
    {
        if (_dirtyRows == null) return;
        Array.Clear(_dirtyRows, 0, _dirtyRows.Length);
    }

    private void MarkRowDirty(int row)
    {
        if (row < 0 || row >= _rowVersions.Length)
        {
            return;
        }

        // Coalesce repeated dirty marks: only bump version the first time a row
        // becomes dirty within the current logical update. This avoids version
        // churn when many fine-grained writes target the same row (statusline).
        bool alreadyDirty = _dirtyRows != null && row >= 0 && row < _dirtyRows.Length && _dirtyRows[row];
        if (!alreadyDirty)
        {
            unchecked { _rowVersions[row]++; }
            if (_dirtyRows != null && row >= 0 && row < _dirtyRows.Length)
            {
                _dirtyRows[row] = true;
            }
        }
        try
        {
            Console.WriteLine($"[MarkRowDirty] row={row} version={_rowVersions[row]} coalesced={alreadyDirty}");
        }
        catch { }
    }

    private void MarkRowRangeDirty(int start, int count)
    {
        if (start < 0) start = 0;
        int end = Math.Min(_rowVersions.Length, start + count);
        for (int i = start; i < end; i++)
        {
            unchecked { _rowVersions[i]++; }
            if (_dirtyRows != null && i >= 0 && i < _dirtyRows.Length) _dirtyRows[i] = true;
        }
        BumpScrollGeneration($"MarkRowRangeDirty({start},{count})");
    }

    private void MarkAllRowsDirty()
    {
        for (int i = 0; i < _rowVersions.Length; i++)
        {
            unchecked { _rowVersions[i]++; }
            if (_dirtyRows != null && i >= 0 && i < _dirtyRows.Length) _dirtyRows[i] = true;
        }
        BumpScrollGeneration("MarkAllRowsDirty");
    }

    private void ResizeRowVersions(int rows)
    {
        var newVersions = new int[rows];
        int copyCount = Math.Min(rows, _rowVersions.Length);
        Array.Copy(_rowVersions, newVersions, copyCount);
        _rowVersions = newVersions;
        var newDirty = new bool[rows];
        if (_dirtyRows != null)
        {
            Array.Copy(_dirtyRows, newDirty, Math.Min(_dirtyRows.Length, newDirty.Length));
        }
        _dirtyRows = newDirty;
    }

    private void ShiftRowVersionsUp(int lines)
    {
        if (lines <= 0 || lines >= Rows)
        {
            MarkAllRowsDirty();
            return;
        }

        var debugShift = Environment.GetEnvironmentVariable("DOTTY_DEBUG_SHIFT_VERSIONS");
        if (!string.IsNullOrEmpty(debugShift))
        {
            try
            {
                var pre = string.Join(",", _rowVersions.Take(Math.Min(_rowVersions.Length, 24)));
                Console.WriteLine($"[ShiftRowVersionsUp] pre(small-sample) lines={lines} : {pre}");
            }
            catch { }
        }

        Array.Copy(_rowVersions, lines, _rowVersions, 0, Rows - lines);
        for (int i = Rows - lines; i < Rows; i++)
        {
            unchecked { _rowVersions[i]++; }
        }

        if (!string.IsNullOrEmpty(debugShift))
        {
            try
            {
                var post = string.Join(",", _rowVersions.Take(Math.Min(_rowVersions.Length, 24)));
                Console.WriteLine($"[ShiftRowVersionsUp] post(small-sample) lines={lines} : {post}");
            }
            catch { }
        }

        BumpScrollGeneration($"ShiftRowVersionsUp({lines})");
    }

    private void ShiftRowVersionsUpRegion(int top, int bottom, int lines)
    {
        if (top < 0) top = 0;
        if (bottom >= _rowVersions.Length) bottom = _rowVersions.Length - 1;
        if (top > bottom)
        {
            MarkAllRowsDirty();
            return;
        }

        int regionHeight = bottom - top + 1;
        if (lines <= 0) return;

        var debugShift = Environment.GetEnvironmentVariable("DOTTY_DEBUG_SHIFT_VERSIONS");
        if (!string.IsNullOrEmpty(debugShift))
        {
            try
            {
                var preSlice = string.Join(",", _rowVersions.Skip(Math.Max(0, top - 2)).Take(regionHeight + 4));
                Console.WriteLine($"[ShiftRowVersionsUpRegion] pre slice top={top} bottom={bottom} lines={lines} : {preSlice}");
            }
            catch { }
        }

        if (lines >= regionHeight)
        {
            // bump versions for the whole region
            for (int i = top; i <= bottom; i++) unchecked { _rowVersions[i]++; }
            if (!string.IsNullOrEmpty(debugShift)) Console.WriteLine($"[ShiftRowVersionsUpRegion] full-bump top={top} bottom={bottom} lines={lines}");
            BumpScrollGeneration($"ShiftRowVersionsUpRegion({top},{bottom},{lines})-full");
            return;
        }

        // shift the version numbers up within the region
        Array.Copy(_rowVersions, top + lines, _rowVersions, top, regionHeight - lines);
        for (int i = top + regionHeight - lines; i <= bottom; i++) unchecked { _rowVersions[i]++; }

        if (!string.IsNullOrEmpty(debugShift))
        {
            try
            {
                var postSlice = string.Join(",", _rowVersions.Skip(Math.Max(0, top - 2)).Take(regionHeight + 4));
                Console.WriteLine($"[ShiftRowVersionsUpRegion] post slice top={top} bottom={bottom} lines={lines} : {postSlice}");

                // Also log the textual contents for the small region to help correlate
                for (int r = Math.Max(0, top - 1); r <= Math.Min(_rowVersions.Length - 1, bottom + 1); r++)
                {
                    try
                    {
                        var text = GetRowText(r);
                        Console.WriteLine($"[ShiftRowVersionsUpRegion] row={r} ver={_rowVersions[r]} textSample='{(text?.Length > 80 ? text.Substring(0, 80) + "..." : text)}'");
                    }
                    catch { }
                }
            }
            catch { }
        }

        BumpScrollGeneration($"ShiftRowVersionsUpRegion({top},{bottom},{lines})");
    }

    private void BumpScrollGeneration(string reason)
    {
        try
        {
            unchecked { _scrollGeneration++; }
            Console.WriteLine($"[ScrollGen] {reason} -> {_scrollGeneration}");
        }
        catch { }
    }

    // Expose a lightweight generation counter that increments when operations
    // occur which move or erase visual rows. Consumers (renderer) can use this
    // to detect scroll/erase events that require compositor cache adjustments.
    public int ScrollGeneration => _scrollGeneration;
}
