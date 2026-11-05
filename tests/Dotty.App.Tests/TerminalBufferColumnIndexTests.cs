using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalBufferColumnIndexTests
{
    [Fact]
    public void InsertAndDeleteColumnsEditEveryRowOnlyInsideTheCursorAndMarginRectangle()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8, scrollbackCapacity: 3);
        buffer.WriteText("histline".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(3, 0);
        buffer.LineFeed();
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        }

        buffer.SetScrollRegion(2, 3);
        buffer.SetLeftRightMarginMode(true);
        buffer.SetHorizontalMargins(4, 7);
        buffer.SetCursor(2, 5);
        int head = buffer.ActiveBuffer.Head;
        string historyLine = buffer.GetScrollbackLineText(0);
        ulong outsideTop = buffer.GetRowGeneration(0);
        ulong outsideBottom = buffer.GetRowGeneration(3);
        ulong unchangedRow = buffer.GetRowGeneration(1);

        buffer.InsertColumns(0);
        Assert.Equal("abcdefgh", buffer.GetRowText(1));
        Assert.Equal(unchangedRow, buffer.GetRowGeneration(1));
        buffer.InsertColumns(1);
        Assert.Equal("abcdefgh", buffer.GetRowText(0));
        Assert.Equal("abcde fh", buffer.GetRowText(1));
        Assert.Equal("abcde fh", buffer.GetRowText(2));
        Assert.Equal("abcdefgh", buffer.GetRowText(3));
        Assert.Equal(outsideTop, buffer.GetRowGeneration(0));
        Assert.Equal(outsideBottom, buffer.GetRowGeneration(3));
        Assert.Equal(head, buffer.ActiveBuffer.Head);

        buffer.DeleteColumns(1);
        Assert.Equal("abcdef h", buffer.GetRowText(1));
        Assert.Equal("abcdef h", buffer.GetRowText(2));
        Assert.Equal(head, buffer.ActiveBuffer.Head);

        buffer.InsertColumns(int.MaxValue);
        Assert.Equal("abcde  h", buffer.GetRowText(1));
        Assert.Equal("abcde  h", buffer.GetRowText(2));
        Assert.Equal(head, buffer.ActiveBuffer.Head);
        Assert.Equal(historyLine, buffer.GetScrollbackLineText(0));
    }

    [Fact]
    public void ColumnEditingDoesNothingWhenCursorIsOutsideEitherMarginAxis()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8);
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        }

        buffer.SetScrollRegion(2, 3);
        buffer.SetLeftRightMarginMode(true);
        buffer.SetHorizontalMargins(3, 6);
        buffer.SetCursor(0, 4);
        ulong generation = buffer.GetRowGeneration(1);
        buffer.InsertColumns(2);
        Assert.Equal("abcdefgh", buffer.GetRowText(1));
        Assert.Equal(generation, buffer.GetRowGeneration(1));

        buffer.SetCursor(2, 1);
        buffer.DeleteColumns(2);
        Assert.Equal("abcdefgh", buffer.GetRowText(2));
    }

    [Fact]
    public void MarginIndexShiftsAllRowsAndOrdinaryMotionStopsAtPhysicalEdges()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8, scrollbackCapacity: 2);
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        }

        buffer.SetScrollRegion(2, 3);
        buffer.SetLeftRightMarginMode(true);
        buffer.SetHorizontalMargins(3, 6);
        buffer.ActiveBuffer.SetRowContinuation(1, true);
        buffer.ActiveBuffer.SetRowContinuation(2, true);
        buffer.ActiveBuffer.SetRowContinuation(3, true);
        buffer.SetCursor(2, 2);
        buffer.BackIndex();
        Assert.Equal(2, buffer.CursorCol);
        Assert.Equal("ab cdegh", buffer.GetRowText(1));
        Assert.Equal("ab cdegh", buffer.GetRowText(2));
        Assert.Equal("abcdefgh", buffer.GetRowText(0));
        Assert.True(buffer.ActiveBuffer.GetRowContinuesPrevious(1));
        Assert.True(buffer.ActiveBuffer.GetRowContinuesPrevious(2));
        Assert.True(buffer.ActiveBuffer.GetRowContinuesPrevious(3));

        ulong outsideGeneration = buffer.GetRowGeneration(3);
        buffer.SetCursor(1, 5);
        buffer.ForwardIndex();
        Assert.Equal(5, buffer.CursorCol);
        Assert.Equal("abcde gh", buffer.GetRowText(1));
        Assert.False(buffer.ActiveBuffer.GetRowContinuesPrevious(2));
        Assert.False(buffer.ActiveBuffer.GetRowContinuesPrevious(3));
        Assert.Equal(outsideGeneration + 1, buffer.GetRowGeneration(3));

        buffer.SetCursor(0, 0);
        buffer.BackIndex();
        Assert.Equal(0, buffer.CursorCol);
        buffer.SetCursor(0, 7);
        buffer.ForwardIndex();
        Assert.Equal(7, buffer.CursorCol);
        buffer.SetLeftRightMarginMode(false);
        buffer.SetCursor(1, 0);
        buffer.BackIndex();
        Assert.Equal(" abcde g", buffer.GetRowText(1));
        buffer.SetCursor(1, 7);
        buffer.ForwardIndex();
        Assert.Equal("abcde g ", buffer.GetRowText(1));
    }

    [Fact]
    public void BufferColumnInsertKeepsWideGlyphAndColdMetadataTogether()
    {
        using var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("A汉B".AsSpan(), CellAttributes.Default);
        buffer.ActiveBuffer.SetColdHyperlink(0, 3, 41);
        buffer.ActiveBuffer.SetColdGraphemeIndex(0, 3, 7);
        buffer.ActiveBuffer.GetCellRef(0, 3).IsProtected = true;
        buffer.SetCursor(0, 0);

        buffer.InsertColumns(2);

        var screen = buffer.ActiveBuffer;
        Assert.Equal((uint)'A', screen.GetRowCells(0)[2].Rune);
        Assert.Equal((uint)'汉', screen.GetRowCells(0)[3].Rune);
        Assert.Equal(2, screen.GetRowCells(0)[3].Width);
        Assert.True(screen.GetRowCells(0)[4].IsContinuation);
        Assert.Equal((uint)'B', screen.GetRowCells(0)[5].Rune);
        Assert.Equal((ushort)41, screen.GetRowColdCells(0)[5].HyperlinkId);
        Assert.Equal((short)7, screen.GetRowColdCells(0)[5].GraphemeIndex);
        Assert.True(screen.GetRowCells(0)[5].IsProtected);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void SaturatedInsertClearingBottomRowDetachesAndInvalidatesFollowingRowLink()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 4, scrollbackCapacity: 1);
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText("ABCD".AsSpan(), CellAttributes.Default);
        }

        buffer.SetScrollRegion(2, 3);
        buffer.ActiveBuffer.SetRowContinuation(3, true);
        ulong followingRowGeneration = buffer.GetRowGeneration(3);
        buffer.SetCursor(2, 0);
        buffer.InsertColumns(int.MaxValue);

        Assert.Equal("    ", buffer.GetRowText(2));
        Assert.Equal("ABCD", buffer.GetRowText(3));
        Assert.False(buffer.ActiveBuffer.GetRowContinuesPrevious(3));
        Assert.Equal(followingRowGeneration + 1, buffer.GetRowGeneration(3));
    }

    [Fact]
    public void ColumnEditsLeaveRowExtentConsistentForLaterReflow()
    {
        using var buffer = new TerminalBuffer(rows: 2, columns: 8, scrollbackCapacity: 2);
        buffer.SetCursor(0, 0);
        buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("ijklmnop".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(0, 3);

        buffer.InsertColumns(1);

        Assert.Equal("abc defg", buffer.GetRowText(0));
        Assert.Equal("ijk lmno", buffer.GetRowText(1));
        buffer.Resize(2, 10);
        Assert.Equal("abc defg  ", buffer.GetRowText(0));
        Assert.Equal("ijk lmno  ", buffer.GetRowText(1));
        Assert.Empty(buffer.ActiveBuffer.ValidateInvariants());
    }

}
