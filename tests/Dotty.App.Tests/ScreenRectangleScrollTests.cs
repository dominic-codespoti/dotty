using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScreenRectangleScrollTests
{
    [Fact]
    public void ScrollUpMovesHotAndColdCellsWithoutRotatingHistory()
    {
        using var screen = new Screen(3, 6, scrollbackCapacity: 4);
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                ref var cell = ref screen.GetCellRef(row, col);
                cell.Rune = (uint)('A' + row * 6 + col);
                cell.StyleId = (ushort)(row + 1);
                screen.UpdateRowMaxCol(row, col);
                screen.MarkRowEnd(row, col);
            }
        }
        screen.SetColdHyperlink(1, 2, 7);
        screen.SetColdGraphemeIndex(2, 3, 4);
        int head = screen.Head;

        Assert.Equal(1, screen.ScrollRectangle(0, 2, 1, 4, 1));

        Assert.Equal(head, screen.Head);
        Assert.Equal((uint)'H', screen.GetRowCells(0)[1].Rune);
        Assert.Equal((uint)'J', screen.GetRowCells(0)[3].Rune);
        Assert.Equal(0u, screen.GetRowCells(2)[4].Rune);
        Assert.Equal((ushort)7, screen.GetRowColdCells(0)[2].HyperlinkId);
        Assert.True(screen.RowColdFlags[screen.GetPhysicalRow(0)]);
        Assert.False(screen.RowColdFlags[screen.GetPhysicalRow(2)]);
        Assert.Equal((short)4, screen.GetRowColdCells(1)[3].GraphemeIndex);
        Assert.Empty(screen.ValidateInvariants());
        Assert.Equal(3, screen.ScrollRectangle(0, 2, 1, 4, int.MaxValue));
    }

    [Fact]
    public void RectangleScrollWorksAcrossWrappedRingHeadWithoutMovingIt()
    {
        using var screen = new Screen(2, 4, scrollbackCapacity: 2);
        Assert.Equal(2, screen.ScrollUpRegion(0, 1, 3));
        int head = screen.Head;
        Set(screen, 0, 1, 'a');
        Set(screen, 1, 1, 'b');

        Assert.Equal(1, screen.ScrollRectangle(0, 1, 1, 2, 1));

        Assert.Equal(head, screen.Head);
        Assert.Equal((uint)'b', screen.GetRowCells(0)[1].Rune);
        Assert.Equal(0u, screen.GetRowCells(1)[1].Rune);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void ScrollDownMovesInteriorWideGlyphAsAWholeAndClearsOnlyExposedCells()
    {
        using var screen = new Screen(3, 7, scrollbackCapacity: 0);
        Set(screen, 0, 1, 'a');
        Set(screen, 0, 2, '界', width: 2);
        Set(screen, 0, 3, 0, continuation: true);
        Set(screen, 0, 4, 'b');
        Set(screen, 1, 1, 'x');

        Assert.Equal(1, screen.ScrollRectangle(0, 1, 1, 5, -1));

        var row = screen.GetRowCells(1);
        Assert.Equal((uint)'a', row[1].Rune);
        Assert.Equal((uint)'界', row[2].Rune);
        Assert.True(row[3].IsContinuation);
        Assert.Equal((uint)'b', row[4].Rune);
        Assert.Equal(0u, screen.GetRowCells(0)[1].Rune);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void RectangleEdgeCrossingWideGlyphClearsWholeGlyphWithoutGetCellRepair()
    {
        using var screen = new Screen(2, 6, scrollbackCapacity: 0);
        Set(screen, 0, 1, 'p');
        Set(screen, 0, 2, '界', width: 2);
        Set(screen, 0, 3, 0, continuation: true);
        Set(screen, 0, 4, 'q');

        Assert.Equal(1, screen.ScrollRectangle(0, 0, 3, 5, 1));

        var row = screen.GetRowCells(0);
        Assert.Equal((uint)'p', row[1].Rune);
        Assert.Equal(0u, row[2].Rune);
        Assert.False(row[3].IsContinuation);
        Assert.Equal(0u, row[3].Rune);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void SingleRowRectangleClearsSelectedColumnsAndInvalidBoundsDoNothing()
    {
        using var screen = new Screen(2, 5, scrollbackCapacity: 0);
        for (int col = 0; col < 5; col++) Set(screen, 0, col, (uint)('a' + col));
        int head = screen.Head;

        Assert.Equal(0, screen.ScrollRectangle(1, 0, 0, 4, 1));
        Assert.Equal(1, screen.ScrollRectangle(0, 0, 1, 3, int.MinValue));
        Assert.Equal((uint)'a', screen.GetRowCells(0)[0].Rune);
        Assert.Equal(0u, screen.GetRowCells(0)[1].Rune);
        Assert.Equal(0u, screen.GetRowCells(0)[3].Rune);
        Assert.Equal((uint)'e', screen.GetRowCells(0)[4].Rune);
        Assert.Equal(head, screen.Head);
    }

    private static void Set(Screen screen, int row, int col, uint rune, byte width = 1, bool continuation = false)
    {
        ref var cell = ref screen.GetCellRef(row, col);
        cell.Rune = rune;
        cell.Width = width;
        cell.IsContinuation = continuation;
        screen.UpdateRowMaxCol(row, col);
        screen.MarkRowEnd(row, col);
    }
}
