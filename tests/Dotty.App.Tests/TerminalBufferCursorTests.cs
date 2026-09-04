using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalBufferCursorTests
{
    [Fact]
    public void CursorAdvanceReflectsGraphemeWidths()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 10);
        buffer.WriteText("A汉", null, null, false);

        Assert.Equal(3, buffer.CursorCol);

        var wideCell = buffer.GetCell(0, 1);
        var wideCold = buffer.GetColdCell(0, 1);
        var grapheme = GraphemeHelper.Resolve(wideCell.Rune, wideCold.GraphemeIndex);
        Assert.Equal("汉", grapheme);
        Assert.Equal(2, wideCell.Width);
        Assert.True(buffer.GetCell(0, 2).IsContinuation);
    }

    [Fact]
    public void Resize_ShrinkingWidth_PreservesEachRow()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 6);

        buffer.SetCursor(0, 0);
        buffer.WriteText("ABCDEF".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("123456".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 4);

        Assert.Equal("1234", buffer.GetRowText(0));
        Assert.Equal("56  ", buffer.GetRowText(1));
        Assert.Equal("ABCD", buffer.GetScrollbackLineText(0));
    }

    [Fact]
    public void Resize_GrowingWidth_PreservesRowPlacement()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);

        buffer.SetCursor(0, 0);
        buffer.WriteText("ABCD".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("1234".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 6);

        Assert.Equal("ABCD  ", buffer.GetRowText(0));
        Assert.Equal("1234  ", buffer.GetRowText(1));
    }

    [Fact]
    public void Resize_ClampsCursorToNewBounds()
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 8);

        buffer.SetCursor(4, 7);

        buffer.Resize(3, 5);

        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
    }

    [Fact]
    public void CarriageReturn_DoesNotClearDifferentRowOnNextWrite()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 10);

        buffer.SetCursor(0, 0);
        buffer.WriteText("abcdef".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("uvwxyz".AsSpan(), CellAttributes.Default);

        buffer.SetCursor(0, 6);
        buffer.CarriageReturn();
        buffer.SetCursor(1, 0);
        buffer.WriteText("xy".AsSpan(), CellAttributes.Default);

        Assert.Equal("abcdef", buffer.GetRowText(0).TrimEnd());
        Assert.Equal("xywxyz", buffer.GetRowText(1).TrimEnd());
    }

    [Fact]
    public void AlternateScreen_RestoresMainCursorPosition()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 20);

        buffer.SetCursor(1, 2);
        buffer.SetAlternateScreen(true);
        buffer.SetCursor(9, 0);
        buffer.SetAlternateScreen(false);

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void AlternateScreen_RestoresCursorSeparatelyFromDecSaveCursor()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 20);

        buffer.SetCursor(1, 2);
        buffer.SetAlternateScreen(true);
        buffer.SetCursor(5, 6);
        buffer.SaveCursor();
        buffer.SetCursor(8, 9);
        buffer.RestoreCursor();
        buffer.SetAlternateScreen(false);

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void AlternateScreen_DoesNotLeakScrollbackCountToMainScreen()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 10);
        buffer.SetCursor(2, 0);
        buffer.LineFeed();
        Assert.Equal(1, buffer.ScrollbackCount);

        buffer.SetAlternateScreen(true);
        buffer.SetCursor(2, 0);
        for (int i = 0; i < 5; i++)
            buffer.LineFeed();
        Assert.Equal(5, buffer.ScrollbackCount);

        buffer.SetAlternateScreen(false);

        Assert.Equal(1, buffer.ScrollbackCount);
    }
    [Fact]
    public void Resize_SoftWrap_ReflowsAndGrowsBackLosslessly()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abcdefghij".AsSpan(), CellAttributes.Default);
        var before = buffer.ActiveScreenForTests;
        Assert.Equal(7, before.RowEndCol[before.GetPhysicalRow(0)]);
        Assert.True(before.RowContinuesPrevious[before.GetPhysicalRow(1)]);
        buffer.Resize(3, 4);

        Assert.Equal(
            "abcd|efgh|ij  ",
            $"{buffer.GetRowText(0)}|{buffer.GetRowText(1)}|{buffer.GetRowText(2)}");
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);

        buffer.Resize(3, 8);

        Assert.Equal("abcdefgh", buffer.GetRowText(0));
        Assert.Equal("ij      ", buffer.GetRowText(1));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void Resize_HardNewline_DoesNotJoinLogicalLines()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abcd\r\nef".AsSpan(), CellAttributes.Default);

        buffer.Resize(3, 2);

        Assert.Equal("ab", buffer.GetRowText(0));
        Assert.Equal("cd", buffer.GetRowText(1));
        Assert.Equal("ef", buffer.GetRowText(2));
    }

    [Fact]
    public void Resize_PreservesExplicitTrailingSpaces()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 8);
        buffer.WriteText("ab  ".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 2);

        Assert.Equal("ab", buffer.GetRowText(0));
        Assert.Equal("  ", buffer.GetRowText(1));
    }

    [Fact]
    public void Resize_PendingWrapCursorRemainsAtEdge()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        buffer.WriteText("abcd".AsSpan(), CellAttributes.Default);

        buffer.Resize(3, 2);
        buffer.WriteText("X".AsSpan(), CellAttributes.Default);

        Assert.Equal("ab", buffer.GetRowText(0));
        Assert.Equal("cd", buffer.GetRowText(1));
        Assert.Equal("X ", buffer.GetRowText(2));
    }
    [Fact]
    public void Resize_WideGlyphAtNewBoundaryPreservesUnit()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("a汉b".AsSpan(), CellAttributes.Default);
        buffer.Resize(2, 3);

        Assert.Equal('a', (char)buffer.GetCell(0, 0).Rune);
        Assert.Equal(2, buffer.GetCell(0, 1).Width);
        Assert.True(buffer.GetCell(0, 2).IsContinuation);
        Assert.Equal('b', (char)buffer.GetCell(1, 0).Rune);
        buffer.Resize(2, 4);
        Assert.Equal('b', (char)buffer.GetCell(0, 3).Rune);
    }

    [Fact]
    public void Resize_PreservesGraphemeHyperlinkAndStyle()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        ushort linkId = buffer.GetOrCreateHyperlinkId("https://example.test");
        var attributes = CellAttributes.Default;
        attributes.Bold = true;
        attributes.HyperlinkId = linkId;
        buffer.WriteText("e\u0301x".AsSpan(), attributes);

        var before = buffer.GetCell(0, 0);
        var beforeCold = buffer.GetColdCell(0, 0);
        buffer.Resize(2, 2);

        var after = buffer.GetCell(0, 0);
        var afterCold = buffer.GetColdCell(0, 0);
        Assert.Equal(before.StyleId, after.StyleId);
        Assert.Equal(linkId, afterCold.HyperlinkId);
        Assert.True(after.HasGrapheme);
        Assert.Equal("e\u0301", GraphemeHelper.Resolve(after.Rune, afterCold.GraphemeIndex));
        Assert.Equal(beforeCold.GraphemeIndex, afterCold.GraphemeIndex);
    }

    [Fact]
    public void Resize_ScrollbackOrderAndCapacityRemainBounded()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4, scrollbackCapacity: 2);
        buffer.WriteText("L0\r\nL1\r\nL2\r\nL3".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 2);

        Assert.True(buffer.ScrollbackCount <= 2);
        Assert.Equal("L3", buffer.GetRowText(1).TrimEnd());
        Assert.Contains("L0", string.Join("|", buffer.GetScrollbackLines()));
    }

    [Fact]
    public void Resize_AlternateScreenReflowsWithoutMainScrollbackLeak()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 4);
        buffer.WriteText("main".AsSpan(), CellAttributes.Default);
        buffer.SetAlternateScreen(true);
        buffer.WriteText("alt!".AsSpan(), CellAttributes.Default);

        Assert.Equal("    |alt!", $"{buffer.GetRowText(0)}|{buffer.GetRowText(1)}");
        buffer.Resize(3, 2);
        Assert.Empty(buffer.ValidateInvariants());

        Assert.Equal(
            "al|t!|  ",
            $"{buffer.GetRowText(0)}|{buffer.GetRowText(1)}|{buffer.GetRowText(2)}");
        buffer.SetAlternateScreen(false);
        Assert.Equal("ma", buffer.GetRowText(0));
        Assert.Equal("in", buffer.GetRowText(1));
    }

    [Fact]
    public void Resize_MapsDecSavedCursorAndPromptMarks()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        buffer.SaveCursor();
        buffer.AddPromptMark(PromptKind.Prompt);

        buffer.Resize(3, 4);
        buffer.SetCursor(0, 0);
        buffer.RestoreCursor();

        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorCol);
        Assert.NotEmpty(buffer.GetPromptMarks());
        Assert.All(buffer.GetPromptMarks(), mark => Assert.InRange(mark.AbsoluteRow, 0, buffer.Rows + buffer.ScrollbackCount - 1));
    }

    [Fact]
    public void Resize_PartialScrollRegionClampsBoundsAndOriginCursor()
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 8);
        buffer.SetScrollRegion(2, 4);
        buffer.SetOriginMode(true);
        buffer.SetCursor(2, 7);

        buffer.Resize(3, 4);

        Assert.InRange(buffer.CursorRow, 0, 2);
        Assert.InRange(buffer.CursorCol, 0, 3);
    }
    [Fact]
    public void Resize_RebasesPromptMarksFromScrollback()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8, scrollbackCapacity: 3);
        buffer.SetCursor(0, 0);
        buffer.AddPromptMark(PromptKind.Prompt);
        buffer.SetCursor(1, 0);
        buffer.WriteText("L1\r\nL2".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 4);

        Assert.NotEmpty(buffer.GetPromptMarks());
        Assert.All(buffer.GetPromptMarks(), mark =>
            Assert.InRange(mark.AbsoluteRow, 0, buffer.ScrollbackCount + buffer.Rows - 1));
    }
    [Fact]
    public void Resize_EmptyBufferDoesNotInventScrollback()
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 8);

        buffer.Resize(3, 4);

        Assert.Equal(0, buffer.ScrollbackCount);
    }
    [Fact]
    public void Resize_NonzeroRingHeadPreservesChronologicalContent()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5, scrollbackCapacity: 3);
        for (int i = 0; i < 5; i++)
            buffer.WriteText($"L{i}\r\n".AsSpan(), CellAttributes.Default);

        Assert.NotEqual(0, buffer.ActiveScreenForTests.Head);
        buffer.Resize(2, 3);

        Assert.True(buffer.ScrollbackCount <= 3);
        Assert.Equal("L1", buffer.GetScrollbackLine(0).Text.TrimEnd());
        Assert.Equal("L2", buffer.GetScrollbackLine(1).Text.TrimEnd());
        Assert.Equal("L3", buffer.GetScrollbackLine(2).Text.TrimEnd());
        Assert.Contains("L4", buffer.GetRowText(0));
    }
    [Fact]
    public void Resize_RowOnlyChangeKeepsContentAtCursorViewport()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abc".AsSpan(), CellAttributes.Default);

        buffer.Resize(4, 8);
        buffer.Resize(2, 8);

        Assert.Equal("abc", buffer.GetRowText(0).TrimEnd());
        Assert.Equal(0, buffer.ScrollbackCount);
    }
}

