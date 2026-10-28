using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class ReflowResizeTests
{
    [Fact]
    public void SoftWrappedLineRoundTripsAcrossResize()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abcdefghij".AsSpan(), CellAttributes.Default);

        buffer.Resize(3, 4);

        Assert.Equal("abcd", buffer.GetRowText(0));
        Assert.Equal("efgh", buffer.GetRowText(1));
        Assert.Equal("ij  ", buffer.GetRowText(2));

        buffer.Resize(3, 8);

        Assert.Equal("abcdefgh", buffer.GetRowText(0));
        Assert.Equal("ij      ", buffer.GetRowText(1));
    }

    [Fact]
    public void ResizePreservesScrollbackOrderAfterRingRotation()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5, scrollbackCapacity: 3);
        for (int i = 0; i < 5; i++)
            buffer.WriteText($"L{i}\r\n".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 3);

        Assert.True(buffer.ScrollbackCount <= 3);
        Assert.Contains("L", string.Join("|", buffer.GetScrollbackLines()));
        Assert.Contains("L4", buffer.GetRowText(0));
    }

    [Fact]
    public void ResizeAlternateScreenPreservesBothScreenContentAndSavedCursor()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        buffer.WriteText("MAIN".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(0, 3);

        buffer.SetAlternateScreen(true);
        buffer.SetCursor(0, 0);
        buffer.WriteText("abcdef".AsSpan(), CellAttributes.Default);
        buffer.AddPromptMark(PromptKind.Prompt);

        buffer.Resize(3, 2);

        Assert.Equal("ab", buffer.GetRowText(0));
        Assert.Equal("cd", buffer.GetRowText(1));
        Assert.Equal("ef", buffer.GetRowText(2));
        Assert.Single(buffer.GetPromptMarks());

        buffer.SetAlternateScreen(false);

        Assert.Equal("MA", buffer.GetRowText(0));
        Assert.Equal("IN", buffer.GetRowText(1));
        Assert.Equal("  ", buffer.GetRowText(2));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(1, buffer.CursorCol);
    }

    // The tests below characterize today's reflow on the public API (plan 08, WI-R1). They pin behavior that the
    // anchor-only mapping / rows-only / streaming rewrites (WI-R2..R5) must preserve; none of them is a fix.

    [Fact]
    public void SingleColumnResizeKeepsEveryCharacterInReadingOrder()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 4, scrollbackCapacity: 16);
        buffer.WriteText("abcd".AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteText("efgh".AsSpan(), CellAttributes.Default);

        buffer.Resize(3, 1);

        // Eight one-column rows: the oldest five become scrollback (oldest first), the newest three stay visible.
        Assert.Equal(1, buffer.Columns);
        Assert.Equal(5, buffer.ScrollbackCount);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, buffer.GetScrollbackLines().ToArray());
        Assert.Equal("f", buffer.GetRowText(0));
        Assert.Equal("g", buffer.GetRowText(1));
        Assert.Equal("h", buffer.GetRowText(2));
    }

    [Fact]
    public void WideCharactersWrapWholeAtTwoColumnsAndNarrowAtOneColumn()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        buffer.WriteText("\u4e2d\u6587".AsSpan(), CellAttributes.Default);

        // Two columns: each wide glyph fills a row on its own (the continuation cell renders as a space).
        buffer.Resize(2, 2);
        Assert.Equal("\u4e2d ", buffer.GetRowText(0));
        Assert.Equal("\u6587 ", buffer.GetRowText(1));

        // Back to four columns: the soft-wrapped pair rejoins on one row.
        buffer.Resize(2, 4);
        Assert.Equal("\u4e2d \u6587 ", buffer.GetRowText(0));

        // One column cannot hold a wide glyph: reflow narrows it to a single cell per row.
        buffer.Resize(2, 1);
        Assert.Equal("\u4e2d", buffer.GetRowText(0));
        Assert.Equal("\u6587", buffer.GetRowText(1));
    }

    [Fact]
    public void RowOnlyResizeKeepsLineContentAndCursorPosition()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 5);
        buffer.WriteText("abc".AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteText("de".AsSpan(), CellAttributes.Default);

        buffer.Resize(6, 5);
        Assert.Equal("abc  ", buffer.GetRowText(0));
        Assert.Equal("de   ", buffer.GetRowText(1));
        Assert.Equal("     ", buffer.GetRowText(2));
        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);

        buffer.Resize(2, 5);
        Assert.Equal("abc  ", buffer.GetRowText(0));
        Assert.Equal("de   ", buffer.GetRowText(1));
        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void PromptMarksFollowTheirLinesAcrossColumnResizes()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 6, scrollbackCapacity: 10);
        buffer.AddPromptMark(PromptKind.Prompt);
        buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.AddPromptMark(PromptKind.Command);

        buffer.Resize(3, 4); // "abcdefgh" now needs two rows; the Command mark's blank line is pushed to row 2
        AssertMarks(buffer, (0, PromptKind.Prompt, 0), (2, PromptKind.Command, 0));

        buffer.Resize(3, 8); // "abcdefgh" fits one row again; the Command mark moves up to row 1
        AssertMarks(buffer, (0, PromptKind.Prompt, 0), (1, PromptKind.Command, 0));
    }

    [Fact]
    public void OneByOneResizeAndBackKeepsBuffersConsistent()
    {
        var buffer = new TerminalBuffer(rows: 4, columns: 6, scrollbackCapacity: 8);
        buffer.WriteText("ab\u4e2d\u6587cd".AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteText("xyz".AsSpan(), CellAttributes.Default);

        buffer.Resize(1, 1);
        Assert.Equal(1, buffer.Rows);
        Assert.Equal(1, buffer.Columns);
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(0, buffer.CursorCol);
        Assert.True(buffer.ScrollbackCount <= 8);

        buffer.Resize(5, 9);
        Assert.Equal(5, buffer.Rows);
        Assert.Equal(9, buffer.Columns);
        Assert.InRange(buffer.CursorRow, 0, 4);
        Assert.InRange(buffer.CursorCol, 0, 8);
        Assert.True(buffer.ScrollbackCount <= 8);
    }

    private static void AssertMarks(TerminalBuffer buffer, params (int Row, PromptKind Kind, int Column)[] expected)
    {
        var marks = buffer.GetPromptMarks();
        Assert.Equal(expected.Length, marks.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Row, buffer.GetPromptVisibleRow(marks[i]));
            Assert.Equal(expected[i].Kind, marks[i].Kind);
            Assert.Equal(expected[i].Column, marks[i].AbsoluteColumn);
        }
    }
}
