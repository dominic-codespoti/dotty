using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;

namespace Dotty.Runtime.Selection;

public sealed class TextSelectionService
{
    private int _anchorRow;
    private int _anchorColumn;
    private int _activeRow;
    private int _activeColumn;
    private SelectionMode _mode = SelectionMode.None;
    private bool _hasSelection;

    public SelectionMode Mode => _mode;
    public bool HasSelection => _hasSelection && _mode != SelectionMode.None;
    public int AnchorRow => _anchorRow;
    public int AnchorColumn => _anchorColumn;
    public int ActiveRow => _activeRow;
    public int ActiveColumn => _activeColumn;

    public void StartSelection(int row, int col, SelectionMode mode = SelectionMode.Character)
    {
        _anchorRow = row;
        _anchorColumn = col;
        _activeRow = row;
        _activeColumn = col;
        _mode = mode;
        _hasSelection = mode != SelectionMode.None;
    }
    public void SelectLine(int row, int totalColumns)
    {
        _anchorRow = row;
        _anchorColumn = 0;
        _activeRow = row;
        _activeColumn = Math.Max(0, totalColumns - 1);
        _mode = SelectionMode.Line;
        _hasSelection = true;
    }

    public void UpdateLineSelection(int row, int totalColumns)
    {
        if (!HasSelection) return;
        _mode = SelectionMode.Line;
        int maxCol = Math.Max(0, totalColumns - 1);
        if (row >= _anchorRow)
        {
            _anchorColumn = 0;
            _activeRow = row;
            _activeColumn = maxCol;
        }
        else
        {
            _anchorColumn = maxCol;
            _activeRow = row;
            _activeColumn = 0;
        }
    }

    public void UpdateSelection(int row, int col)
    {
        if (!HasSelection)
        {
            return;
        }

        _activeRow = row;
        _activeColumn = col;
    }
    public void ClearSelection()
    {
        _mode = SelectionMode.None;
        _hasSelection = false;
        _anchorRow = 0;
        _anchorColumn = 0;
        _activeRow = 0;
        _activeColumn = 0;
    }

    public TerminalSelectionRange GetNormalizedRange()
    {
        if (!HasSelection)
        {
            return TerminalSelectionRange.Empty;
        }

        if (_mode == SelectionMode.Block)
        {
            int minRow = Math.Min(_anchorRow, _activeRow);
            int maxRow = Math.Max(_anchorRow, _activeRow);
            int minCol = Math.Min(_anchorColumn, _activeColumn);
            int maxCol = Math.Max(_anchorColumn, _activeColumn);
            return new TerminalSelectionRange(minRow, minCol, maxRow, maxCol);
        }

        return TerminalSelectionRange.From(_anchorRow, _anchorColumn, _activeRow, _activeColumn);
    }

    public bool IsCellSelected(int row, int col)
    {
        if (!HasSelection)
        {
            return false;
        }

        if (_mode == SelectionMode.Block)
        {
            int minRow = Math.Min(_anchorRow, _activeRow);
            int maxRow = Math.Max(_anchorRow, _activeRow);
            int minCol = Math.Min(_anchorColumn, _activeColumn);
            int maxCol = Math.Max(_anchorColumn, _activeColumn);

            return row >= minRow && row <= maxRow && col >= minCol && col <= maxCol;
        }

        var range = GetNormalizedRange();
        return range.Contains(row, col);
    }

    public string GetSelectedText(TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!HasSelection)
        {
            return string.Empty;
        }

        string result = string.Empty;
        try
        {
            buffer.WithSyncRoot(() => result = ExtractTextCore(buffer));
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }

        return result;
    }

    private string ExtractTextCore(TerminalBuffer buffer)
    {
        var range = GetNormalizedRange();
        if (range.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        if (_mode == SelectionMode.Block)
        {
            int minCol = range.StartColumn;
            int maxCol = range.EndColumn;

            for (int row = range.StartRow; row <= range.EndRow; row++)
            {
                ExtractRowSegment(buffer, row, minCol, maxCol, sb);
                if (row < range.EndRow)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        for (int row = range.StartRow; row <= range.EndRow; row++)
        {
            int startCol = row == range.StartRow ? range.StartColumn : 0;
            int endCol = row == range.EndRow ? range.EndColumn : buffer.Columns - 1;

            ExtractRowSegment(buffer, row, startCol, endCol, sb);

            if (row < range.EndRow)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void ExtractRowSegment(TerminalBuffer buffer, int row, int startCol, int endCol, StringBuilder sb)
    {
        if (row < 0)
        {
            // Negative row indicates scrollback. Row -1 is the newest scrollback line.
            int sbIdx = -row - 1;
            if (sbIdx >= buffer.ScrollbackCount)
            {
                return;
            }

            string line = buffer.GetScrollbackLine(sbIdx).Text ?? string.Empty;
            int s = Math.Clamp(startCol, 0, line.Length);
            int e = Math.Clamp(endCol + 1, s, line.Length);
            sb.Append(line.AsSpan(s, e - s));
        }
        else
        {
            if (row >= buffer.Rows)
            {
                return;
            }

            int clampedStart = Math.Max(0, startCol);
            int clampedEnd = Math.Min(buffer.Columns - 1, endCol);

            for (int col = clampedStart; col <= clampedEnd; col++)
            {
                var cell = buffer.GetCell(row, col);
                if (cell.IsContinuation)
                {
                    continue;
                }

                var cold = buffer.GetColdCell(row, col);
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
    }
}
