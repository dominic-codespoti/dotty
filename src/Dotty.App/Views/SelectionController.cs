using System;
using Dotty.App.Controls.Canvas;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Views;

internal sealed class SelectionController
{
    private int _anchorRow;
    private int _anchorColumn;
    private TerminalSelectionRange _range = TerminalSelectionRange.Empty;

    public TerminalSelectionRange Range => _range;
    public bool HasSelection => !_range.IsEmpty;
    public bool IsDragging { get; private set; }

    public void BeginSelection(int row, int column)
    {
        _anchorRow = row;
        _anchorColumn = column;
        IsDragging = true;
        _range = TerminalSelectionRange.From(row, column, row, column);
    }

    public void UpdateSelection(int row, int column)
    {
        if (!IsDragging) return;
        _range = TerminalSelectionRange.From(_anchorRow, _anchorColumn, row, column);
    }

    public void EndSelection()
    {
        IsDragging = false;
    }

    public void Clear()
    {
        IsDragging = false;
        _range = TerminalSelectionRange.Empty;
    }

    public void SelectLine(int row, int columns)
    {
        IsDragging = false;
        _range = new TerminalSelectionRange(row, 0, row, columns - 1);
    }

    public void SelectAll(int rows, int columns)
    {
        IsDragging = false;
        _range = new TerminalSelectionRange(0, 0, rows - 1, columns - 1);
    }

    public string ExtractText(TerminalBuffer buffer)
    {
        if (!HasSelection)
        {
            return string.Empty;
        }

        // User-initiated read of shared buffer state: run under SyncRoot so the
        // extraction never observes a partially-parsed chunk (the consumer
        // mutates under the same lock).
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
        using var sb = ZStr.CreateStringBuilder(buffer.Columns * (Range.EndRow - Range.StartRow + 1));
        for (int row = Range.StartRow; row <= Range.EndRow; row++)
        {
            int startCol = row == Range.StartRow ? Range.StartColumn : 0;
            int endCol = row == Range.EndRow ? Range.EndColumn : buffer.Columns - 1;

            if (row < 0)
            {
                // Scrollback rows are not addressable through GetCell (its bounds
                // check returns blank cells for negative rows); read the scrollback
                // line API instead. Row -1 is the newest scrollback line.
                int sbIdx = -row - 1;
                if (sbIdx >= buffer.ScrollbackCount)
                {
                    if (row < Range.EndRow) sb.AppendLine();
                    continue;
                }
                var line = buffer.GetScrollbackLine(sbIdx).Text ?? string.Empty;
                int s = Math.Clamp(startCol, 0, line.Length);
                int e = Math.Clamp(endCol + 1, s, line.Length);
                sb.Append(line.AsSpan(s, e - s));
            }
            else
            {
                for (int col = startCol; col <= endCol; col++)
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

            if (row < Range.EndRow)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
