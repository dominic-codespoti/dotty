using System;
using System.Globalization;
using System.Text;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Handles text/grapheme writing, control chars, and attribute application on a screen.
/// </summary>
internal sealed class BufferTextWriter
{
    private readonly CursorController _cursor;
    private readonly BufferEraser _eraser;
    private readonly Func<Screen> _buffer;
    private readonly Func<int> _rows;
    private readonly Func<int> _cols;
    private readonly Action<int> _scrollUp;
    private readonly Func<bool> _getClearFlag;
    private readonly Action<bool> _setClearFlag;
    private readonly Action _carriageReturn;
    private readonly Action _lineFeed;
    private readonly Action<int> _markRowDirty;
    // Batch state: when true, dirty-mark requests are coalesced into _batchedRows
    private bool _inBatchWrite = false;
    private System.Collections.Generic.HashSet<int>? _batchedRows;

    public BufferTextWriter(
        CursorController cursor,
        BufferEraser eraser,
        Func<Screen> buffer,
        Func<int> rows,
        Func<int> cols,
        Action<int> scrollUp,
        Func<bool> getClearFlag,
        Action<bool> setClearFlag,
        Action carriageReturn,
        Action lineFeed,
        Action<int> markRowDirty)
    {
        _cursor = cursor;
        _eraser = eraser;
        _buffer = buffer;
        _rows = rows;
        _cols = cols;
        _scrollUp = scrollUp;
        _getClearFlag = getClearFlag;
        _setClearFlag = setClearFlag;
        _carriageReturn = carriageReturn;
        _lineFeed = lineFeed;
        _markRowDirty = markRowDirty;
    }

    public void WriteText(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        if (text.IsEmpty)
        {
            return;
        }

        // No diagnostic logging during normal operation.

        // If this write affects the bottom rows, take a snapshot of the
        // target row (the row we're about to write) so we can compare
        // before/after and see exactly what changes are being applied.
        string? beforeSnapshot = null;
        int targetRow = _cursor.Row;
        // No snapshot logging during normal operation.

        // Begin batch: coalesce repeated marks during this logical write
        _inBatchWrite = true;
        _batchedRows = new System.Collections.Generic.HashSet<int>();

        var enumerator = StringInfo.GetTextElementEnumerator(text.ToString());
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();

            if (element == "\r\n")
            {
                _carriageReturn();
                _lineFeed();
                continue;
            }

            if (element.Length == 1 && TryHandleControlChar(element[0], in attributes))
            {
                continue;
            }

            // If the application has requested a clear-on-next-write (carriage
            // return behavior), honor it by clearing from the cursor to the end
            // of line. Additionally, many full-screen apps (vim/neovim) write
            // status lines by positioning the cursor at column 0 and then
            // writing a shorter string than the previous content. Terminals
            // implicitly clear the entire line when writing begins at column
            // zero; emulate that behaviour here so stale characters to the
            // right do not remain.
            if (_getClearFlag())
            {
                _eraser.ClearLineFromCursor(_buffer(), _cursor, _cols());
                _setClearFlag(false);
                RequestMarkRowDirty(_cursor.Row);
            }

            // Implicit EL2 when writing starts at column 0: clear the entire
            // line before emitting the first grapheme. This matches what
            // applications expect when they do `CUP row,0` followed by text.
            if (_cursor.Col == 0)
            {
                _eraser.EraseLine(_buffer(), _cursor, _cols(), 2);
                RequestMarkRowDirty(_cursor.Row);
            }

            WriteGrapheme(element, in attributes);
        }

        // No snapshot logging during normal operation.
        finally
        {
            // Flush any coalesced dirty marks
            try
            {
                if (_inBatchWrite && _batchedRows != null)
                {
                    foreach (var r in _batchedRows)
                    {
                        try { _markRowDirty(r); } catch { }
                    }
                }
            }
            catch { }
            _inBatchWrite = false;
            _batchedRows = null;
        }
    }

    private string SnapshotRow(Screen buf, int row)
    {
        var sb = new StringBuilder();
        int cols = _cols();
        for (int j = 0; j < cols; j++)
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

    private bool TryHandleControlChar(char ch, in CellAttributes attributes)
    {
        switch (ch)
        {
            case '\r':
                _carriageReturn();
                return true;
            case '\n':
                _lineFeed();
                return true;
            case '\t':
                WriteTab(in attributes);
                return true;
            case '\b':
            case '\u007f':
                _eraser.ErasePreviousGlyph(_buffer(), _cursor, _rows(), _cols());
                RequestMarkRowDirty(_cursor.Row);
                return true;
            default:
                return char.IsControl(ch);
        }
    }

    private void WriteTab(in CellAttributes attributes)
    {
        int tabStop = 8;
        int spaces = tabStop - (_cursor.Col % tabStop);
        for (int i = 0; i < spaces; i++)
        {
            WriteGrapheme(" ", in attributes);
        }
    }

    private void WriteGrapheme(string grapheme, in CellAttributes attributes)
    {
        if (string.IsNullOrEmpty(grapheme))
        {
            return;
        }

        int width = UnicodeWidth.GetWidth(grapheme);
        if (width == 0)
        {
            if (AttachCombiningMark(grapheme))
            {
                return;
            }

            width = 1;
        }

        bool scrolledOnEnsure = false;
        if (_cursor.EnsureSpace(width, _rows(), _cols()))
        {
            _scrollUp(1);
            scrolledOnEnsure = true;
        }

        var buf = _buffer();
        int currentRow = _cursor.Row;
        ref var cell = ref buf.GetCellRef(_cursor.Row, _cursor.Col);
        cell.Grapheme = grapheme;
        ApplyAttributes(ref cell, in attributes);
        cell.Width = (byte)Math.Clamp(width, 1, 2);
        cell.IsContinuation = false;

        for (int i = 1; i < width; i++)
        {
            ref var cont = ref buf.GetCellRef(_cursor.Row, _cursor.Col + i);
            cont.Reset();
            cont.IsContinuation = true;
            ApplyAttributes(ref cont, in attributes);
        }

        // Avoid double-scrolling: if EnsureSpace already caused a scroll,
        // don't perform another scroll for the same written grapheme.
        if (!scrolledOnEnsure && _cursor.AdvanceCursor(width, _rows(), _cols()))
        {
            _scrollUp(1);
        }
        RequestMarkRowDirty(currentRow);
    }

    private bool AttachCombiningMark(string mark)
    {
        var (row, col) = GetPreviousBaseCell();
        if (row < 0)
        {
            return false;
        }

        var buf = _buffer();
        ref var cell = ref buf.GetCellRef(row, col);
        if (string.IsNullOrEmpty(cell.Grapheme))
        {
            return false;
        }

        cell.Grapheme += mark;
        RequestMarkRowDirty(row);
        return true;
    }

    private void RequestMarkRowDirty(int row)
    {
        if (_inBatchWrite)
        {
            _batchedRows ??= new System.Collections.Generic.HashSet<int>();
            _batchedRows.Add(row);
        }
        else
        {
            try { _markRowDirty(row); } catch { }
        }
    }

    private (int row, int col) GetPreviousBaseCell()
    {
        int row = _cursor.Row;
        int col = _cursor.Col;

        if (row == 0 && col == 0)
        {
            return (-1, -1);
        }

        if (col == 0)
        {
            row--;
            col = _cols() - 1;
        }
        else
        {
            col--;
        }

        var buf = _buffer();
        while (row >= 0)
        {
            ref var cell = ref buf.GetCellRef(row, col);
            if (!cell.IsContinuation)
            {
                if (!cell.IsEmpty)
                {
                    return (row, col);
                }

                return (-1, -1);
            }

            if (col == 0)
            {
                row--;
                col = _cols() - 1;
            }
            else
            {
                col--;
            }
        }

        return (-1, -1);
    }

    private static void ApplyAttributes(ref Cell cell, in CellAttributes attributes)
    {
        cell.Foreground = attributes.Foreground;
        cell.Background = attributes.Background;
        cell.Bold = attributes.Bold;
        cell.Italic = attributes.Italic;
        cell.Underline = attributes.Underline;
        cell.Faint = attributes.Faint;
        cell.Inverse = attributes.Inverse;
        cell.UnderlineColor = attributes.UnderlineColor;
    }
}
