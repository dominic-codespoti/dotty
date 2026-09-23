using System;
using System.Globalization;
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

    public void SelectWord(TerminalBuffer buffer, int row, int col)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        try
        {
            buffer.WithSyncRoot(() =>
            {
                if (row < 0)
                {
                    string[] cells = GetScrollbackCells(buffer.GetScrollbackLine(-row - 1).Text ?? string.Empty);
                    SelectCellRun(row, col, cells);
                }
                else if (row < buffer.Rows && buffer.Columns > 0)
                {
                    int selectedColumn = Math.Clamp(col, 0, buffer.Columns - 1);
                    WordClass kind = GetVisibleWordClass(buffer, row, selectedColumn);
                    int start = selectedColumn;
                    int end = selectedColumn;
                    while (start > 0 && GetVisibleWordClass(buffer, row, start - 1) == kind)
                        start--;

                    int rowEnd = GetVisibleRowEnd(buffer, row);
                    int scanEnd = kind == WordClass.Whitespace
                        ? Math.Max(selectedColumn, rowEnd)
                        : buffer.Columns - 1;
                    while (end < scanEnd && GetVisibleWordClass(buffer, row, end + 1) == kind)
                        end++;
                    StartSelection(row, start, SelectionMode.Word);
                    UpdateSelection(row, end);
                }
                else
                {
                    ClearSelection();
                }
            });
        }
        catch (TimeoutException)
        {
            ClearSelection();
        }
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
        if (!HasSelection) return;
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
        if (!HasSelection) return TerminalSelectionRange.Empty;
        if (_mode == SelectionMode.Block)
        {
            return new TerminalSelectionRange(
                Math.Min(_anchorRow, _activeRow),
                Math.Min(_anchorColumn, _activeColumn),
                Math.Max(_anchorRow, _activeRow),
                Math.Max(_anchorColumn, _activeColumn));
        }
        return TerminalSelectionRange.From(_anchorRow, _anchorColumn, _activeRow, _activeColumn);
    }

    public bool IsCellSelected(int row, int col)
    {
        if (!HasSelection) return false;
        if (_mode == SelectionMode.Block)
        {
            int minRow = Math.Min(_anchorRow, _activeRow);
            int maxRow = Math.Max(_anchorRow, _activeRow);
            int minCol = Math.Min(_anchorColumn, _activeColumn);
            int maxCol = Math.Max(_anchorColumn, _activeColumn);
            return row >= minRow && row <= maxRow && col >= minCol && col <= maxCol;
        }
        return GetNormalizedRange().Contains(row, col);
    }

    public string GetSelectedText(TerminalBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!HasSelection) return string.Empty;

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
        if (range.IsEmpty) return string.Empty;

        var sb = new StringBuilder();
        if (_mode == SelectionMode.Block)
        {
            for (int row = range.StartRow; row <= range.EndRow; row++)
            {
                ExtractBlockRow(buffer, row, range.StartColumn, range.EndColumn, sb);
                if (row < range.EndRow) sb.AppendLine();
            }
            return sb.ToString();
        }

        for (int row = range.StartRow; row <= range.EndRow; row++)
        {
            int startCol = row == range.StartRow ? range.StartColumn : 0;
            int endCol = row == range.EndRow ? range.EndColumn : buffer.Columns - 1;
            var rowText = new StringBuilder();
            ExtractCharacterRow(buffer, row, startCol, endCol, rowText);
            TrimTrailingSpaces(rowText);
            sb.Append(rowText);

            if (row < range.EndRow && !ContinuesPreviousVisibleRow(buffer, row + 1))
                sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void ExtractCharacterRow(
        TerminalBuffer buffer, int row, int startCol, int endCol, StringBuilder destination)
    {
        if (row < 0)
        {
            string[] cells = GetScrollbackCells(buffer.GetScrollbackLine(-row - 1).Text ?? string.Empty);
            int start = Math.Clamp(startCol, 0, cells.Length);
            int end = Math.Clamp(endCol + 1, start, cells.Length);
            for (int i = start; i < end; i++) destination.Append(cells[i]);
            return;
        }

        if (row >= buffer.Rows) return;
        int rowEnd = GetVisibleRowEnd(buffer, row);
        int startCell = Math.Max(0, startCol);
        int endCell = Math.Min(Math.Min(buffer.Columns - 1, endCol), rowEnd);
        if (startCell > endCell) return;
        for (int col = startCell; col <= endCell; col++)
            destination.Append(GetVisibleCellText(buffer, row, col));
    }

    private static void ExtractBlockRow(
        TerminalBuffer buffer, int row, int startCol, int endCol, StringBuilder destination)
    {
        int min = Math.Max(0, startCol);
        int max = Math.Max(min, endCol);
        if (row < 0)
        {
            string[] cells = GetScrollbackCells(buffer.GetScrollbackLine(-row - 1).Text ?? string.Empty);
            for (int col = min; col <= max; col++)
                destination.Append(col < cells.Length ? cells[col] : " ");
            return;
        }

        if (row >= buffer.Rows) return;
        min = Math.Min(min, Math.Max(0, buffer.Columns - 1));
        max = Math.Min(max, Math.Max(0, buffer.Columns - 1));
        for (int col = min; col <= max; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation)
            {
                // A continuation is already represented by its wide base glyph.
                // If the selection starts in it, retain the selected column.
                if (col == min) destination.Append(' ');
                continue;
            }
            destination.Append(GetVisibleCellText(buffer, row, col));
        }
    }

    private static bool ContinuesPreviousVisibleRow(TerminalBuffer buffer, int row) =>
        row >= 0 && row < buffer.Rows && buffer.ActiveBuffer.GetRowContinuesPrevious(row);

    private static int GetVisibleRowEnd(TerminalBuffer buffer, int row)
    {
        int end = buffer.ActiveBuffer.GetRowEndCol(row);
        if (end < 0) end = buffer.ActiveBuffer.GetRowMaxCol(row);
        return end;
    }

    private static string GetVisibleCellText(TerminalBuffer buffer, int row, int col)
    {
        var cell = buffer.GetCell(row, col);
        if (cell.IsContinuation) return string.Empty;
        var cold = buffer.GetColdCell(row, col);
        return GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex) ?? " ";
    }

    private static void TrimTrailingSpaces(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ')
            text.Length--;
    }

    private static WordClass GetVisibleWordClass(TerminalBuffer buffer, int row, int col)
    {
        string text = GetVisibleCellText(buffer, row, col);
        if (text.Length == 0 && col > 0)
            text = GetVisibleCellText(buffer, row, col - 1);
        return Classify(text);
    }

    private void SelectCellRun(int row, int col, string[] cells)
    {
        if (cells.Length == 0)
        {
            StartSelection(row, 0, SelectionMode.Word);
            return;
        }
        int selected = Math.Clamp(col, 0, cells.Length - 1);
        WordClass kind = Classify(cells[selected]);
        int start = selected;
        int end = selected;
        while (start > 0 && Classify(cells[start - 1]) == kind) start--;
        while (end + 1 < cells.Length && Classify(cells[end + 1]) == kind) end++;
        StartSelection(row, start, SelectionMode.Word);
        UpdateSelection(row, end);
    }

    private static string[] GetScrollbackCells(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        var cells = new string[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            int length = (i + 1 < starts.Length ? starts[i + 1] : text.Length) - starts[i];
            cells[i] = text.Substring(starts[i], length);
        }
        return cells;
    }

    private static WordClass Classify(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
            return WordClass.Whitespace;
        int index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length) return WordClass.Whitespace;
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            || text[index] == '_'
            ? WordClass.Word
            : WordClass.Punctuation;
    }

    private enum WordClass
    {
        Whitespace,
        Word,
        Punctuation
    }
}
