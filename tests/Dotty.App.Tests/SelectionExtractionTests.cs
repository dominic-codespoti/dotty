using Dotty.Runtime.Selection;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Characterization of <see cref="TextSelectionService"/> text extraction (WI-T0): visible and scrollback rows,
/// character / word / line / block modes, soft-wrap joining, trailing-space trimming and wide-glyph placeholders.
/// Golden strings were derived by reading the extraction code; they pin today's behavior so a later change of the
/// text source (WI-T4) cannot alter what a copy produces. Scrollback selections use a single history line, because
/// the service maps row -1 to <c>GetScrollbackLine(0)</c>.
/// </summary>
public sealed class SelectionExtractionTests
{
    private static readonly string NL = Environment.NewLine;

    /// <summary>
    /// 5 rows x 12 columns: "hello world", "foo   bar", a 15-char line soft-wrapped over rows 2-3
    /// ("abcdefghijkl" + "mno", row 3 continues row 2) and "界x界y" (wide glyphs at columns 0-1 and 3-4).
    /// </summary>
    private static TerminalBuffer BuildVisible()
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 12, scrollbackCapacity: 20);
        WriteAt(buffer, 0, 0, "hello world");
        WriteAt(buffer, 1, 0, "foo   bar");
        WriteAt(buffer, 2, 0, "abcdefghijklmno");
        WriteAt(buffer, 4, 0, "界x界y");
        return buffer;
    }

    /// <summary>
    /// One history line "a界e\u0301b c" (cells: a, 界, placeholder, e+U+0301, b, space, c) above a visible row 0
    /// holding "vis".
    /// </summary>
    private static TerminalBuffer BuildWithHistory()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 12, scrollbackCapacity: 20);
        WriteAt(buffer, 0, 0, "a界e\u0301b c");
        buffer.ScrollUpLines(1);
        WriteAt(buffer, 0, 0, "vis");
        return buffer;
    }

    private static void WriteAt(TerminalBuffer buffer, int row, int col, string text)
    {
        buffer.SetCursor(row, col);
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
    }

    private static string Select(
        TerminalBuffer buffer, int startRow, int startCol, int endRow, int endCol, SelectionMode mode = SelectionMode.Character)
    {
        var service = new TextSelectionService();
        service.StartSelection(startRow, startCol, mode);
        service.UpdateSelection(endRow, endCol);
        return service.GetSelectedText(buffer);
    }

    // ---- visible rows: character mode ----

    [Fact]
    public void Character_SingleRow()
    {
        Assert.Equal("hello", Select(BuildVisible(), 0, 0, 0, 4));
    }

    [Fact]
    public void Character_ReverseDragNormalizes()
    {
        Assert.Equal("hello", Select(BuildVisible(), 0, 4, 0, 0));
    }

    [Fact]
    public void Character_HardLineBreakInsertsNewline_AndTrimsTrailingSpaces()
    {
        // Row 1 selects "foo   " (columns 0-5); the trailing spaces are trimmed.
        Assert.Equal("world" + NL + "foo", Select(BuildVisible(), 0, 6, 1, 5));
    }

    [Fact]
    public void Character_SelectionPastTheRowEndStopsAtTheLastWrittenCell()
    {
        Assert.Equal("world" + NL + "foo   bar", Select(BuildVisible(), 0, 6, 1, 11));
    }

    [Fact]
    public void Character_SoftWrappedVisibleRowsJoinWithoutNewline()
    {
        Assert.Equal("ijklmn", Select(BuildVisible(), 2, 8, 3, 1));
    }

    [Fact]
    public void Character_StartingInsideTheWrappedLineKeepsTheNewlineBetweenUnrelatedRows()
    {
        Assert.Equal("foo   bar" + NL + "abcdefghijklmno", Select(BuildVisible(), 1, 0, 3, 11));
    }

    // ---- visible rows: wide glyphs ----

    [Fact]
    public void Character_WideGlyphsAreEmittedOncePerGlyph()
    {
        Assert.Equal("界x界y", Select(BuildVisible(), 4, 0, 4, 5));
    }

    [Fact]
    public void Character_StartingInsideAContinuationCellOmitsTheGlyph()
    {
        Assert.Equal("x", Select(BuildVisible(), 4, 1, 4, 2));
    }

    [Fact]
    public void Block_StartingInsideAContinuationCellKeepsTheSelectedColumnAsSpace()
    {
        Assert.Equal(" x", Select(BuildVisible(), 4, 1, 4, 2, SelectionMode.Block));
    }

    [Fact]
    public void Block_WideGlyphAtTheBlockStartIsEmittedOnce()
    {
        Assert.Equal("界x", Select(BuildVisible(), 4, 0, 4, 2, SelectionMode.Block));
    }

    // ---- visible rows: line and block modes ----

    [Fact]
    public void Line_SingleRow()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectLine(1, buffer.Columns);
        Assert.Equal("foo   bar", service.GetSelectedText(buffer));
    }

    [Fact]
    public void Line_DraggedAcrossASoftWrapJoinsTheWrappedRows()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectLine(1, buffer.Columns);
        service.UpdateLineSelection(2, buffer.Columns);
        Assert.Equal("foo   bar" + NL + "abcdefghijkl", service.GetSelectedText(buffer));

        service.UpdateLineSelection(3, buffer.Columns);
        Assert.Equal("foo   bar" + NL + "abcdefghijklmno", service.GetSelectedText(buffer));
    }

    [Fact]
    public void Block_NormalizesCornersAndKeepsInteriorAndTrailingSpaces()
    {
        Assert.Equal("llo w" + NL + "o   b", Select(BuildVisible(), 0, 6, 1, 2, SelectionMode.Block));
    }

    [Fact]
    public void Block_PastTheRowEndPadsWithSpaces()
    {
        Assert.Equal("rld ", Select(BuildVisible(), 0, 8, 0, 11, SelectionMode.Block));
    }

    // ---- exact (preserve-trailing-space) range selection ----

    [Fact]
    public void RangeExclusive_RetainsTrailingSpaces()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        Assert.True(service.StartRangeExclusive(buffer, 1, 0, 1, 6));
        Assert.Equal("foo   ", service.GetSelectedText(buffer));
    }

    [Fact]
    public void RangeExclusive_EmptyRangeClearsTheSelection()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        Assert.False(service.StartRangeExclusive(buffer, 1, 3, 1, 3));
        Assert.False(service.HasSelection);
        Assert.Equal(string.Empty, service.GetSelectedText(buffer));
    }

    [Fact]
    public void StartSelection_ResetsPreservedTrailingSpaces()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        Assert.True(service.StartRangeExclusive(buffer, 1, 0, 1, 6));
        service.StartSelection(1, 0);
        service.UpdateSelection(1, 5);
        Assert.Equal("foo", service.GetSelectedText(buffer));
    }

    [Fact]
    public void NoSelection_ProducesEmptyText()
    {
        Assert.Equal(string.Empty, new TextSelectionService().GetSelectedText(BuildVisible()));
    }

    // ---- word selection (visible) ----

    [Fact]
    public void Word_SelectsTheWordUnderTheCell()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectWord(buffer, 0, 7);

        Assert.Equal(SelectionMode.Word, service.Mode);
        var range = service.GetNormalizedRange();
        Assert.Equal((0, 6, 0, 10), (range.StartRow, range.StartColumn, range.EndRow, range.EndColumn));
        Assert.Equal("world", service.GetSelectedText(buffer));
    }

    [Fact]
    public void Word_WhitespaceRunSelectsTheRunButCopiesNothing()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectWord(buffer, 1, 4);

        var range = service.GetNormalizedRange();
        Assert.Equal((1, 3, 1, 5), (range.StartRow, range.StartColumn, range.EndRow, range.EndColumn));
        Assert.Equal(string.Empty, service.GetSelectedText(buffer));
    }

    [Fact]
    public void Word_OnAContinuationCellSelectsTheWholeWideRun()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectWord(buffer, 4, 1);

        var range = service.GetNormalizedRange();
        Assert.Equal((4, 0, 4, 5), (range.StartRow, range.StartColumn, range.EndRow, range.EndColumn));
        Assert.Equal("界x界y", service.GetSelectedText(buffer));
    }

    [Fact]
    public void Word_OutsideTheViewportClearsTheSelection()
    {
        var buffer = BuildVisible();
        var service = new TextSelectionService();
        service.SelectWord(buffer, 9, 0);
        Assert.False(service.HasSelection);
    }

    // ---- scrollback rows (cell index == column; the wide glyph's placeholder is a space cell) ----

    [Fact]
    public void Scrollback_Character_KeepsThePlaceholderSpace()
    {
        Assert.Equal("界 e\u0301", Select(BuildWithHistory(), -1, 1, -1, 3));
    }

    [Fact]
    public void Scrollback_Character_WholeRow()
    {
        Assert.Equal("a界 e\u0301b c", Select(BuildWithHistory(), -1, 0, -1, 11));
    }

    [Fact]
    public void Scrollback_Block_PadsPastTheLineEnd()
    {
        var buffer = BuildWithHistory();
        Assert.Equal("a界 e\u0301b c  ", Select(buffer, -1, 0, -1, 8, SelectionMode.Block));
        Assert.Equal(" c  ", Select(buffer, -1, 5, -1, 8, SelectionMode.Block));
    }

    [Fact]
    public void Scrollback_Line()
    {
        var buffer = BuildWithHistory();
        var service = new TextSelectionService();
        service.SelectLine(-1, buffer.Columns);
        Assert.Equal("a界 e\u0301b c", service.GetSelectedText(buffer));
    }

    [Theory]
    [InlineData(6, 6, 6, "c")]
    [InlineData(3, 3, 4, "e\u0301b")]
    [InlineData(1, 0, 1, "a界")]
    [InlineData(5, 5, 5, "")]
    public void Scrollback_Word(int column, int expectedStart, int expectedEnd, string expectedText)
    {
        var buffer = BuildWithHistory();
        var service = new TextSelectionService();
        service.SelectWord(buffer, -1, column);

        var range = service.GetNormalizedRange();
        Assert.Equal((-1, expectedStart, -1, expectedEnd), (range.StartRow, range.StartColumn, range.EndRow, range.EndColumn));
        Assert.Equal(expectedText, service.GetSelectedText(buffer));
    }

    [Fact]
    public void Scrollback_ThroughVisible_JoinsWithNewline()
    {
        Assert.Equal("a界 e\u0301b c" + NL + "vis", Select(BuildWithHistory(), -1, 0, 0, 2));
    }
}
