using System;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Selection;

internal sealed class SelectionModel
{
    public bool IsSelecting { get; private set; }
    public bool HasSelection { get; private set; }

    private int _startRow;
    private int _startCol;
    private int _endRow;
    private int _endCol;

    public bool Begin(int row, int col)
    {
        IsSelecting = true;
        HasSelection = true;
        _startRow = _endRow = row;
        _startCol = _endCol = col;
        return true;
    }

    public bool Update(int row, int col)
    {
        if (!IsSelecting)
        {
            return false;
        }

        if (row == _endRow && col == _endCol)
        {
            return false;
        }

        _endRow = row;
        _endCol = col;
        return true;
    }

    public void End(int row, int col)
    {
        if (!IsSelecting)
        {
            return;
        }

        _endRow = row;
        _endCol = col;
        IsSelecting = false;
    }

    public void EndWithoutPosition()
    {
        IsSelecting = false;
    }

    public void Clear()
    {
        IsSelecting = false;
        HasSelection = false;
        _startRow = _startCol = _endRow = _endCol = 0;
    }

    public bool IsCellSelected(int row, int col)
    {
        if (!HasSelection)
        {
            return false;
        }

        GetCanonicalSelection(out var sr, out var sc, out var er, out var ec);
        if (row < sr || row > er) return false;
        if (sr == er)
        {
            return col >= sc && col <= ec;
        }

        if (row == sr) return col >= sc;
        if (row == er) return col <= ec;
        return true;
    }

    public string GetSelectedText(TerminalBuffer buffer)
    {
        if (!HasSelection)
        {
            return string.Empty;
        }

        GetCanonicalSelection(out var sr, out var sc, out var er, out var ec);
        var sb = new StringBuilder();
        for (int r = sr; r <= er; r++)
        {
            int rowStart = (r == sr) ? sc : 0;
            int rowEnd = (r == er) ? ec : buffer.Columns - 1;
            var rowBuilder = new StringBuilder();

            int c = rowStart;
            while (c <= rowEnd)
            {
                var cell = buffer.GetCell(r, c);
                if (cell.IsContinuation)
                {
                    int baseCol = c;
                    while (baseCol > 0 && buffer.GetCell(r, baseCol).IsContinuation) baseCol--;
                    var baseCell = buffer.GetCell(r, baseCol);

                    if (baseCol < rowStart)
                    {
                        var glyph = string.IsNullOrEmpty(baseCell.Grapheme) ? " " : baseCell.Grapheme;
                        rowBuilder.Append(glyph);
                    }

                    int advance = Math.Max(1, (int)baseCell.Width);
                    c = baseCol + advance;
                    if (c <= rowStart) c = rowStart + 1;
                }
                else
                {
                    var glyph = string.IsNullOrEmpty(cell.Grapheme) ? " " : cell.Grapheme;
                    rowBuilder.Append(glyph);
                    int advance = Math.Max(1, (int)cell.Width);
                    c += advance;
                }
            }

            var line = rowBuilder.ToString().TrimEnd();
            sb.Append(line);
            if (r < er) sb.Append('\n');
        }

        return sb.ToString();
    }

    private void GetCanonicalSelection(out int startRow, out int startCol, out int endRow, out int endCol)
    {
        int r1 = _startRow, c1 = _startCol, r2 = _endRow, c2 = _endCol;
        if (r1 > r2 || (r1 == r2 && c1 > c2))
        {
            startRow = r2; startCol = c2; endRow = r1; endCol = c1;
        }
        else
        {
            startRow = r1; startCol = c1; endRow = r2; endCol = c2;
        }
    }
}
