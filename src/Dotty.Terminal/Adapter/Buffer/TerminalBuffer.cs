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
        _cursor.SetSize(Rows, Columns);
        _writer = CreateWriter();
        ClearScreen();
    }

    private Screen ActiveBuffer => _screens.Active;

    public void ClearScreen()
    {
        _screens.ClearAll();
        _cursor.Reset();
        _clearLineOnNextWrite = false;
    }

    public void ClearScrollback()
    {
        ClearScreen();
    }

    public void SetCursor(int row, int col)
    {
        _cursor.Set(row, col, Rows, Columns);
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
        if (_cursor.LineFeed(Rows))
        {
            ScrollUp(1);
        }
    }

    public void EraseLine(int mode)
    {
        _eraser.EraseLine(ActiveBuffer, _cursor, Columns, mode);
    }

    public void EraseDisplay(int mode)
    {
        var reset = _eraser.EraseDisplay(ActiveBuffer, _cursor, Rows, Columns, mode);
        if (reset)
        {
            _cursor.Reset();
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
        _cursor.Reset();
    }

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
            LineFeed);
    }
}
