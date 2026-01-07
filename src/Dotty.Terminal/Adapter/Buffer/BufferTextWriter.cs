using System;
using System.Globalization;

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

            if (_getClearFlag())
            {
                _eraser.ClearLineFromCursor(_buffer(), _cursor, _cols());
                _setClearFlag(false);
                _markRowDirty(_cursor.Row);
            }

            WriteGrapheme(element, in attributes);
        }
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
                _markRowDirty(_cursor.Row);
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

        if (_cursor.EnsureSpace(width, _rows(), _cols()))
        {
            _scrollUp(1);
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

        if (_cursor.AdvanceCursor(width, _rows(), _cols()))
        {
            _scrollUp(1);
        }
        _markRowDirty(currentRow);
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
        _markRowDirty(row);
        return true;
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
