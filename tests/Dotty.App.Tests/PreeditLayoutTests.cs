using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class PreeditLayoutTests
{
    private static string Cells(List<PreeditCell> cells) =>
        string.Join(" ", cells.Select(c => $"{c.Column},{c.Row}w{c.Width}"));

    // text, caret (UTF-16), start col/row, box (left,right,top,bottom), expected "col,rowwN" cells, caret col,row.
    public static TheoryData<string, int, int, int, int, int, int, int, string, int, int> LayoutCases => new()
    {
        // Fits in place.
        { "abc", 3, 2, 0, 0, 10, 0, 4, "2,0w1 3,0w1 4,0w1", 5, 0 },
        { "abc", 0, 2, 0, 0, 10, 0, 4, "2,0w1 3,0w1 4,0w1", 2, 0 },
        { "abc", 1, 2, 0, 0, 10, 0, 4, "2,0w1 3,0w1 4,0w1", 3, 0 },
        // Wraps at the row end onto the box's left edge.
        { "abcd", 4, 8, 0, 0, 10, 0, 4, "8,0w1 9,0w1 0,1w1 1,1w1", 2, 1 },
        // A wide grapheme never straddles the right edge: it moves to the next row.
        { "a界b", 3, 8, 0, 0, 10, 0, 4, "8,0w1 0,1w2 2,1w1", 3, 1 },
        { "界", 0, 9, 0, 0, 10, 0, 4, "0,1w2", 0, 1 },
        // Caret after text that ends exactly at the right edge goes to the next row.
        { "ab", 2, 8, 0, 0, 10, 0, 4, "8,0w1 9,0w1", 0, 1 },
        // Cursor on the last row: the text is shifted up (drawn over the previous row).
        { "abcd", 4, 8, 3, 0, 10, 0, 4, "8,2w1 9,2w1 0,3w1 1,3w1", 2, 3 },
        // Pane box offset (a right-hand pane at columns 10-19, rows 1-3).
        { "abcd", 4, 18, 1, 10, 20, 1, 4, "18,1w1 19,1w1 10,2w1 11,2w1", 12, 2 },
        // Taller than the box: earlier rows scroll off so the caret row stays visible.
        { "abcdefgh", 8, 0, 0, 0, 3, 0, 2, "0,0w1 1,0w1 2,0w1 0,1w1 1,1w1", 2, 1 },
        // One-row box (search input): shows the caret's segment, i.e. shifts left.
        { "abcdefg", 7, 2, 0, 2, 5, 0, 1, "2,0w1", 3, 0 },
        { "abcdefg", 7, 2, 5, 2, 5, 5, 6, "2,5w1", 3, 5 },
        { "abcdefg", 1, 2, 0, 2, 5, 0, 1, "2,0w1 3,0w1 4,0w1", 3, 0 },
        // Combining marks stay in one cell; the caret inside a grapheme snaps to its start.
        { "e\u0301x", 1, 0, 0, 0, 10, 0, 1, "0,0w1 1,0w1", 0, 0 },
        // A wide grapheme in a one-column box cannot be drawn; the caret is still placed.
        { "界", 0, 0, 0, 0, 1, 0, 2, "", 0, 0 },
    };

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void Compute_WrapsShiftsAndPlacesCaret(string text, int caret, int startColumn, int startRow,
        int left, int right, int top, int bottom, string expectedCells, int caretColumn, int caretRow)
    {
        var cells = new List<PreeditCell>();
        var state = new ImePreeditState(text, caret, 0, 0);

        var result = PreeditLayout.Compute(in state, startColumn, startRow, new PreeditBox(left, right, top, bottom), cells);

        Assert.Equal(expectedCells, Cells(cells));
        Assert.True(result.CaretVisible);
        Assert.Equal((caretColumn, caretRow), (result.CaretColumn, result.CaretRow));
        Assert.All(cells, c => Assert.InRange(c.Column + c.Width, left + 1, right));
        Assert.All(cells, c => Assert.InRange(c.Row, top, bottom - 1));
    }

    [Theory]
    [InlineData("", 0, 0, 0, 10, 0, 4)]
    [InlineData("abc", 10, 0, 0, 10, 0, 4)]  // start column outside the box
    [InlineData("abc", 0, 4, 0, 10, 0, 4)]   // start row outside the box
    [InlineData("abc", 0, 0, 0, 0, 0, 4)]    // empty box
    public void Compute_InactiveOrOutsideProducesNothing(string text, int startColumn, int startRow, int left, int right, int top, int bottom)
    {
        var cells = new List<PreeditCell> { new(0, 1, 0, 0, 1, 0, PreeditCellStyle.Clause) };

        var result = PreeditLayout.Compute(new ImePreeditState(text, 0, 0, 0), startColumn, startRow, new PreeditBox(left, right, top, bottom), cells);

        Assert.Empty(cells);
        Assert.False(result.CaretVisible);
    }

    [Fact]
    public void Compute_StylesFocusedClauseAndIndexesClauses()
    {
        // Three clauses "ab|界|c", focused = the second.
        Assert.True(ImePreeditState.TryCreate(['a', 'b', 0x754C, 'c'], [2, 1, 1], focusedBlock: 1, out var state));
        var cells = new List<PreeditCell>();

        PreeditLayout.Compute(in state, 0, 0, new PreeditBox(0, 10, 0, 1), cells);

        Assert.Equal(new[] { 0, 0, 1, 2 }, cells.Select(c => c.Clause));
        Assert.Equal(
            new[] { PreeditCellStyle.Clause, PreeditCellStyle.Clause, PreeditCellStyle.Focused, PreeditCellStyle.Clause },
            cells.Select(c => c.Style));
    }

    [Fact]
    public void Compute_UnconvertedTextHasNoClausesAndNoFocus()
    {
        Assert.True(ImePreeditState.TryCreate(['a', 'b'], [], focusedBlock: -1, out var state));
        var cells = new List<PreeditCell>();

        var result = PreeditLayout.Compute(in state, 0, 0, new PreeditBox(0, 10, 0, 1), cells);

        Assert.All(cells, c => Assert.Equal((-1, PreeditCellStyle.Clause), (c.Clause, c.Style)));
        Assert.Equal(2, result.CaretColumn);
    }

    [Fact]
    public void Clauses_UseUtf16BoundariesAndValueEquality()
    {
        Assert.True(ImePreeditState.TryCreate(['a', 0x1F642, 'b'], [2, 1], focusedBlock: 0, out var first));
        Assert.True(ImePreeditState.TryCreate(['a', 0x1F642, 'b'], [2, 1], focusedBlock: 0, out var second));

        Assert.Equal(2, first.Clauses.Count);
        Assert.Equal((0, 3), (first.Clauses.Start(0), first.Clauses.End(0)));
        Assert.Equal((3, 4), (first.Clauses.Start(1), first.Clauses.End(1)));
        Assert.Equal(1, first.Clauses.IndexAt(3));
        Assert.Equal(-1, first.Clauses.IndexAt(4));
        Assert.Equal(first, second);
        Assert.NotEqual(first, second with { Clauses = new PreeditClauses([1, 4]) });
        Assert.Throws<ArgumentException>(() => new PreeditClauses([3, 1]));
    }

    [Fact]
    public void RemoveCovered_DropsCellsOverlappingThePreeditOnly()
    {
        PreeditCell[] preedit = [new(0, 1, 3, 0, 2, -1, PreeditCellStyle.Clause)];
        CellInstance[] instances =
        [
            new() { Col = 2, Row = 0 },                            // left neighbour: kept
            new() { Col = 3, Row = 0 },                            // covered
            new() { Col = 4, Row = 0 },                            // covered (second half of the wide preedit cell)
            new() { Col = 5, Row = 0 },                            // right neighbour: kept
            new() { Col = 2, Row = 0, Flags = CellFlags.WideCell }, // wide cell reaching into column 3: covered
            new() { Col = 3, Row = 1 },                            // other row: kept
        ];

        int kept = ImePreeditQuadBuilder.RemoveCovered(instances, preedit);

        Assert.Equal(new[] { (2, 0), (5, 0), (3, 1) }, instances.Take(kept).Select(i => ((int)i.Col, (int)i.Row)));
    }

    [Theory]
    [InlineData(0xFFD4D4D4u, 0xFF1E1E1Eu, 0x803385DBu)]   // dark theme, translucent selection
    [InlineData(0xFF000000u, 0xFFFFFFFFu, 0xFFADD6FFu)]   // light theme
    [InlineData(0xFF102030u, 0x00FFFFFFu, 0u)]            // no selection colour, transparent background byte
    public void Colors_FollowThemeAndAreOpaque(uint foreground, uint background, uint selection)
    {
        var colors = PreeditColors.FromTheme(new SgrColorArgb(foreground), new SgrColorArgb(background), new SgrColorArgb(selection));

        Assert.Equal(foreground | 0xFF000000u, colors.Foreground.Argb);
        Assert.Equal(background | 0xFF000000u, colors.Background.Argb);
        Assert.Equal((selection == 0 ? foreground : selection) | 0xFF000000u, colors.FocusedBackground.Argb);
    }
}
