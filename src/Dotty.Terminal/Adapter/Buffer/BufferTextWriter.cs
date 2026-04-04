using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace Dotty.Terminal.Adapter;

internal sealed class BufferTextWriter
{
    private readonly CursorController _cursor;
    private readonly BufferEraser _eraser;
    private readonly TerminalBuffer _ctx;
    private static readonly ConcurrentDictionary<string, string> _graphemeCache = new();
    private int _lastDirtyRow = -1;

    public BufferTextWriter(TerminalBuffer ctx, CursorController cursor, BufferEraser eraser)
    {
        _ctx = ctx;
        _cursor = cursor;
        _eraser = eraser;
    }

    private static bool IsAllAscii(ReadOnlySpan<char> text) {
        for (int i = 0; i < text.Length; i++) {
            if (text[i] >= 128) return false;
        }
        return true;
    }

    public void WriteText(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        if (text.IsEmpty) return;
        if (IsAllAscii(text)) WriteTextAsciiFast(text, in attributes);
        else {
            int index = 0;
            while (index < text.Length)
            {
                int len = System.Globalization.StringInfo.GetNextTextElementLength(text.Slice(index));
                if (len == 0) break;
                // Avoid substring allocations if length is 1 and it's ascii
                if (len == 1 && text[index] < 128)
                {
                    char c = text[index];
                    if (c < 32 || c == 127)
                    {
                        if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                        {
                            _ctx.CarriageReturn();
                            _ctx.LineFeed();
                            index += 2;
                            continue;
                        }
                        if (TryHandleControlChar(c, in attributes)) 
                        {
                            index++;
                            continue;
                        }
                    }
                    WriteGraphemeAscii(c, in attributes);
                }
                else
                {
                    var slice = text.Slice(index, len);
                    var lookup = _graphemeCache.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (!lookup.TryGetValue(slice, out string? element))
                    {
                        element = slice.ToString();
                        lookup.TryAdd(slice, element);
                    }
                    ProcessElement(element, in attributes);
                }
                index += len;
            }
        }
    }

    private void WriteTextAsciiFast(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        bool attrsDefault = IsDefaultAttributes(in attributes);
        int length = text.Length;
        int i = 0;
        while (i < length)
        {
            int runStart = i;
            while (i < length)
            {
                char ch = text[i];
                if (ch < 32 || ch == 127) break;
                i++;
            }

            if (i > runStart)
            {
                var run = text.Slice(runStart, i - runStart);
                if (!TryWriteAsciiRunFast(run, in attributes, attrsDefault))
                {
                    for (int j = 0; j < run.Length; j++)
                    {
                        WriteGraphemeAscii(run[j], in attributes, attrsDefault);
                    }
                }

                if (i >= length) break;
            }

            char c = text[i];
            if (c < 32 || c == 127)
            {
                if (c == '\r' && i + 1 < length && text[i + 1] == '\n')
                {
                    _ctx.CarriageReturn();
                    _ctx.LineFeed();
                    i++;
                    continue;
                }
                if (TryHandleControlChar(c, in attributes)) continue;
            }
            WriteGraphemeAscii(c, in attributes, attrsDefault);
            i++;
        }
    }

    private bool TryWriteAsciiRunFast(ReadOnlySpan<char> run, in CellAttributes attributes, bool attrsDefault)
    {
        if (run.IsEmpty) return true;

        if (_ctx._autoWrap && _cursor.WrapPending)
        {
            _ctx.LineFeed();
            _ctx.CarriageReturn();
            _cursor.SetWrapPending(false);
        }

        int row = _cursor.Row;
        int col = _cursor.Col;
        int cols = _ctx.Columns;

        if (col < 0 || col >= cols) return false;
        if (run.Length > cols - col) return false;

        var buf = _ctx.ActiveBuffer;

        if (_ctx._clearLineOnNextWrite)
        {
            _eraser.ClearLineFromCursor(buf, _cursor, _ctx.Columns);
            _ctx._clearLineOnNextWrite = false;
            RequestMarkRowDirty(row);
        }

        for (int i = 0; i < run.Length; i++)
        {
            int writeCol = col + i;
            ref var cell = ref buf.GetCellRef(row, writeCol);
            if (cell.IsContinuation || cell.Width > 1)
            {
                buf.ClearCell(row, writeCol);
                cell = ref buf.GetCellRef(row, writeCol);
            }

            cell.SetAscii(run[i]);
            ApplyAttributesFast(ref cell, in attributes, attrsDefault);
            cell.Width = 1;
            cell.IsContinuation = false;
        }

        // Update row max col for scrollback capture
        int endCol = col + run.Length - 1;
        buf.UpdateRowMaxCol(row, endCol);
        if (_ctx._autoWrap)
        {
            if (endCol >= cols - 1)
            {
                _cursor.Set(row, cols - 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                _cursor.Set(row, endCol + 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(false);
            }
        }
        else
        {
            _cursor.Set(row, Math.Min(endCol + 1, cols - 1), _ctx.Rows, cols);
            _cursor.SetWrapPending(false);
        }

        RequestMarkRowDirty(row);
        return true;
    }

    private void ProcessElement(string element, in CellAttributes attributes)
    {
        if (element == "\r\n") { _ctx.CarriageReturn(); _ctx.LineFeed(); return; }
        if (element.Length == 1 && TryHandleControlChar(element[0], in attributes)) return;
        ProcessElementInner(element, in attributes, isAscii: false);
    }

    private void ProcessElementInner(string element, in CellAttributes attributes, bool isAscii)
    {
        if (_ctx._autoWrap && _cursor.WrapPending)
        {
            _ctx.LineFeed();
            _ctx.CarriageReturn();
            _cursor.SetWrapPending(false);
        }

        if (_ctx._clearLineOnNextWrite)
        {
            _eraser.ClearLineFromCursor(_ctx.ActiveBuffer, _cursor, _ctx.Columns);
            _ctx._clearLineOnNextWrite = false;
            RequestMarkRowDirty(_cursor.Row);
        }

        /*
        if (_cursor.Col == 0)
        {
            _eraser.EraseLine(_ctx.ActiveBuffer, _cursor, _ctx.Columns, 2);
            RequestMarkRowDirty(_cursor.Row);
        }
        */

        if (isAscii) WriteGraphemeAscii(element[0], in attributes);
        else WriteGrapheme(element, in attributes);
    }

    private bool TryHandleControlChar(char ch, in CellAttributes attributes)
    {
        switch (ch)
        {
            case '\r': _ctx.CarriageReturn(); return true;
            case '\n': _ctx.LineFeed(); return true;
            case '\t': WriteTab(in attributes); return true;
            case '\b': case '\u007f':
                _eraser.ErasePreviousGlyph(_ctx.ActiveBuffer, _cursor, _ctx.Rows, _ctx.Columns);
                RequestMarkRowDirty(_cursor.Row);
                return true;
            default: return char.IsControl(ch);
        }
    }

    private void WriteTab(in CellAttributes attributes)
    {
        int cols = _ctx.Columns;
        int current = _cursor.Col;
        int target = _ctx.GetNextTabStopFrom(current);
        if (target <= current) target = Math.Min(cols - 1, current + 1);
        int spaces = target - current;
        bool attrsDefault = IsDefaultAttributes(in attributes);
        for (int i = 0; i < spaces; i++) WriteGraphemeAscii(' ', in attributes, attrsDefault);
    }

    private void WriteGraphemeAscii(char ch, in CellAttributes attributes, bool attrsDefault)
    {
        bool autoWrap = _ctx._autoWrap;
        int startCol;
        if (autoWrap)
        {
            // When at the end of the line with autowrap enabled, set wrap pending
            // and let the caller handle scrolling via LineFeed()
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
            }
            // Only ensure we have space, don't scroll here - scrolling is handled by LineFeed
            _cursor.EnsureSpace(1, _ctx.Rows, _ctx.Columns);
            startCol = _cursor.Col;
        }
        else
        {
            int cols = _ctx.Columns;
            startCol = _cursor.Col;
            if (startCol > cols - 1) startCol = Math.Max(0, cols - 1);
            _cursor.Set(_cursor.Row, startCol, _ctx.Rows, cols);
        }

        var buf = _ctx.ActiveBuffer;
        int currentRow = _cursor.Row;
        
        ref var cell = ref buf.GetCellRef(currentRow, startCol);
        if (cell.IsContinuation || cell.Width > 1) {
            buf.ClearCell(currentRow, startCol);
            // Need to get ref again because ClearCell might have changed it? No, ref is stable.
        }

        cell.SetAscii(ch);
        ApplyAttributesFast(ref cell, in attributes, attrsDefault);
        cell.Width = 1;
        cell.IsContinuation = false;
        
        // Update row max col for scrollback capture
        buf.UpdateRowMaxCol(currentRow, startCol);

        if (autoWrap)
        {
            int cols = _ctx.Columns;
            if (startCol >= cols - 1)
            {
                _cursor.Set(currentRow, Math.Min(cols - 1, startCol), _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                _cursor.Set(currentRow, startCol + 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(false);
            }
        }
        else
        {
            int cols = _ctx.Columns;
            _cursor.Set(currentRow, Math.Min(startCol + 1, cols - 1), _ctx.Rows, cols);
            _cursor.SetWrapPending(false);
        }
        RequestMarkRowDirty(currentRow);
    }

    private void WriteGraphemeAscii(char ch, in CellAttributes attributes)
    {
        WriteGraphemeAscii(ch, in attributes, IsDefaultAttributes(in attributes));
    }

    private void WriteGrapheme(string grapheme, in CellAttributes attributes)
    {
        if (string.IsNullOrEmpty(grapheme)) return;
        int width = UnicodeWidth.GetWidth(grapheme);
        if (width == 0)
        {
            if (AttachCombiningMark(grapheme)) return;
            width = 1;
        }

        bool autoWrap = _ctx._autoWrap;
        int startCol;
        if (autoWrap)
        {
            // When at the end of the line with autowrap enabled, set wrap pending
            // and let the caller handle scrolling via LineFeed()
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
            }
            // Only ensure we have space, don't scroll here - scrolling is handled by LineFeed
            _cursor.EnsureSpace(width, _ctx.Rows, _ctx.Columns);
            startCol = _cursor.Col;
        }
        else
        {
            int cols = _ctx.Columns;
            startCol = _cursor.Col;
            if (startCol > cols - width) startCol = Math.Max(0, cols - width);
            _cursor.Set(_cursor.Row, startCol, _ctx.Rows, cols);
        }

        var buf = _ctx.ActiveBuffer;
        int currentRow = _cursor.Row;
        buf.ClearCell(currentRow, startCol);
        ref var cell = ref buf.GetCellRef(_cursor.Row, startCol);
        cell.Grapheme = grapheme;
        ApplyAttributes(ref cell, in attributes);
        cell.Width = (byte)Math.Clamp(width, 1, 2);
        cell.IsContinuation = false;

        for (int i = 1; i < width; i++)
        {
            ref var cont = ref buf.GetCellRef(_cursor.Row, startCol + i);
            cont.Reset();
            cont.IsContinuation = true;
            ApplyAttributes(ref cont, in attributes);
        }
        
        // Update row max col for scrollback capture
        buf.UpdateRowMaxCol(currentRow, startCol + width - 1);

        if (autoWrap)
        {
            int cols = _ctx.Columns;
            int endCol = startCol + width - 1;
            if (endCol >= cols - 1)
            {
                _cursor.Set(_cursor.Row, Math.Min(cols - 1, endCol), _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                _cursor.Set(_cursor.Row, endCol + 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(false);
            }
        }
        else
        {
            int cols = _ctx.Columns;
            _cursor.Set(_cursor.Row, Math.Min(_cursor.Col + width, cols - 1), _ctx.Rows, cols);
            _cursor.SetWrapPending(false);
        }
        RequestMarkRowDirty(currentRow);
    }

    private bool AttachCombiningMark(string mark)
    {
        var (row, col) = GetPreviousBaseCell();
        if (row < 0) return false;
        var buf = _ctx.ActiveBuffer;
        ref var cell = ref buf.GetCellRef(row, col);
        if (string.IsNullOrEmpty(cell.Grapheme)) return false;
        cell.Grapheme += mark;
        RequestMarkRowDirty(row);
        return true;
    }

    private void RequestMarkRowDirty(int row)
    {
        if (row == _lastDirtyRow) return;
        _lastDirtyRow = row;
        _ctx.MarkRowDirty(row);
    }

    private (int row, int col) GetPreviousBaseCell()
    {
        int row = _cursor.Row;
        int col = _cursor.Col;
        if (row == 0 && col == 0) return (-1, -1);
        if (col == 0) { row--; col = _ctx.Columns - 1; }
        else col--;

        var buf = _ctx.ActiveBuffer;
        while (row >= 0)
        {
            ref var cell = ref buf.GetCellRef(row, col);
            if (!cell.IsContinuation)
            {
                if (!cell.IsEmpty) return (row, col);
                return (-1, -1);
            }
            if (col == 0) { row--; col = _ctx.Columns - 1; }
            else col--;
        }
        return (-1, -1);
    }

    private static void ApplyAttributes(ref Cell cell, in CellAttributes attributes)
    {
        cell.Foreground = attributes.Foreground.IsEmpty ? 0 : attributes.Foreground.Argb;
        cell.Background = attributes.Background.IsEmpty ? 0 : attributes.Background.Argb;
        cell.Bold = attributes.Bold;
        cell.Italic = attributes.Italic;
        cell.Underline = attributes.Underline;
        cell.DoubleUnderline = attributes.DoubleUnderline;
        cell.Faint = attributes.Faint;
        cell.Inverse = attributes.Inverse;
        cell.Strikethrough = attributes.Strikethrough;
        cell.Overline = attributes.Overline;
        cell.Invisible = attributes.Invisible;
        cell.SlowBlink = attributes.SlowBlink;
        cell.UnderlineColor = attributes.UnderlineColor.IsEmpty ? 0 : attributes.UnderlineColor.Argb;
        cell.HyperlinkId = attributes.HyperlinkId;
    }

    private static void ApplyAttributesFast(ref Cell cell, in CellAttributes attributes, bool attrsDefault)
    {
        if (attrsDefault)
        {
            cell.Foreground = 0;
            cell.Background = 0;
            cell.UnderlineColor = 0;
            cell.Flags = 0;
            cell.HyperlinkId = 0;
            return;
        }

        ApplyAttributes(ref cell, in attributes);
    }

    private static bool IsDefaultAttributes(in CellAttributes attributes)
    {
        return attributes.IsDefaultColors
            && !attributes.Bold
            && !attributes.Italic
            && !attributes.Underline
            && !attributes.DoubleUnderline
            && !attributes.Faint
            && !attributes.Inverse
            && !attributes.Strikethrough
            && !attributes.Overline
            && !attributes.Invisible
            && !attributes.SlowBlink
            && attributes.HyperlinkId == 0;
    }
}
