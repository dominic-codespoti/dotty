using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScreenRectangleEditingTests
{
    [Theory]
    [InlineData(0, 0, 1, 1, 3, 4)]
    [InlineData(0, 1, 1, 0, 3, 4)]
    [InlineData(1, 0, 0, 1, 3, 4)]
    [InlineData(1, 1, 0, 0, 3, 4)]
    [InlineData(0, 0, 0, 1, 4, 5)]
    [InlineData(0, 1, 0, 0, 4, 5)]
    [InlineData(0, 0, 1, 0, 3, 6)]
    [InlineData(1, 0, 0, 0, 3, 6)]
    [InlineData(0, 0, 0, 0, 4, 6)]
    public void CopyRectangleMatchesASnapshotInEveryOverlapDirection(
        int sourceTop, int sourceLeft, int destinationTop, int destinationLeft, int height, int width)
    {
        using var screen = new Screen(4, 6, scrollbackCapacity: 5);
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 6; column++)
            {
                Set(screen, row, column, (uint)('A' + row * 6 + column));
                screen.GetCellRef(row, column).StyleId = (ushort)(row * 6 + column + 1);
                screen.GetCellRef(row, column).IsProtected = (row + column) % 2 == 0;
            }
            screen.SetRowContinuation(row, row % 2 == 1);
        }
        screen.SetColdHyperlink(1, 1, 9);
        screen.SetColdGraphemeIndex(2, 2, 5);
        screen.SetColdGraphemeIndex(1, 2, -8); // Kitty placeholder metadata.
        var originalHot = new CellHot[4, 6];
        var originalCold = new ColdCell[4, 6];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 6; column++)
            {
                originalHot[row, column] = screen.GetRowCells(row)[column];
                originalCold[row, column] = screen.GetRowColdCells(row)[column];
            }
        int head = screen.Head;

        screen.CopyRectangle(sourceTop, sourceLeft, destinationTop, destinationLeft, height, width);

        for (int row = 0; row < 4; row++)
        {
            bool expectedCold = false;
            for (int column = 0; column < 6; column++)
            {
                bool copied = row >= destinationTop && row < destinationTop + height
                    && column >= destinationLeft && column < destinationLeft + width;
                int oldRow = copied ? sourceTop + row - destinationTop : row;
                int oldColumn = copied ? sourceLeft + column - destinationLeft : column;
                Assert.Equal(originalHot[oldRow, oldColumn], screen.GetRowCells(row)[column]);
                Assert.Equal(originalCold[oldRow, oldColumn], screen.GetRowColdCells(row)[column]);
                expectedCold |= originalCold[oldRow, oldColumn].HyperlinkId != 0
                    || originalCold[oldRow, oldColumn].GraphemeIndex != -1;
            }
            Assert.Equal(expectedCold, screen.RowColdFlags[screen.GetPhysicalRow(row)]);
            Assert.Equal(row % 2 == 1, screen.GetRowContinuesPrevious(row));
        }
        Assert.Equal(head, screen.Head);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void FillAndWipePreserveContinuationsAndWipeRetainsStyleAndProtection()
    {
        using var screen = new Screen(3, 6, scrollbackCapacity: 3);
        for (int r = 0; r < 3; r++)
        {
            Set(screen, r, 0, 'X');
            screen.SetRowContinuation(r, r > 0);
        }
        ref CellHot protectedCell = ref screen.GetCellRef(1, 4);
        protectedCell.Rune = 'P'; protectedCell.StyleId = 7; protectedCell.IsProtected = true;
        screen.MarkRowEnd(1, 4);
        screen.SetColdHyperlink(1, 4, 11);
        screen.FillRectangle(0, 1, 2, 3, new CellHot { Rune = 'F', StyleId = 4 });
        Assert.Equal((uint)'F', screen.GetRowCells(0)[1].Rune);
        Assert.Equal((ushort)11, screen.GetRowColdCells(1)[4].HyperlinkId);
        Assert.False(screen.GetRowContinuesPrevious(0));
        Assert.True(screen.GetRowContinuesPrevious(2));

        screen.WipeRectangle(1, 4, 1, 4, true);
        Assert.Equal((uint)'P', screen.GetRowCells(1)[4].Rune);
        screen.WipeRectangle(1, 4, 1, 4, false);
        Assert.Equal((uint)32, screen.GetRowCells(1)[4].Rune);
        Assert.Equal((ushort)7, screen.GetRowCells(1)[4].StyleId);
        Assert.True(screen.GetRowCells(1)[4].IsProtected);
        Assert.Equal((ushort)0, screen.GetRowColdCells(1)[4].HyperlinkId);
        Assert.True(screen.GetRowContinuesPrevious(1));
        Assert.Empty(screen.ValidateInvariants());

        screen.FillRectangle(0, 0, 2, 5, new CellHot { Rune = 32 });
        Assert.True(screen.GetRowContinuesPrevious(1));
        Assert.True(screen.GetRowContinuesPrevious(2));
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void CopyAfterRingRotationDoesNotChangeHistoryOrHead()
    {
        using var screen = new Screen(3, 6, scrollbackCapacity: 4);
        for (int c = 0; c < 3; c++)
        {
            Set(screen, 0, c, (uint)('A' + c));
            Set(screen, 1, c, (uint)('D' + c));
            Set(screen, 2, c, (uint)('G' + c));
        }
        Assert.Equal(1, screen.ScrollUpRegion(0, 2, 1));
        int head = screen.Head;
        string history = screen.GetScrollbackRow(0);

        screen.CopyRectangle(0, 0, 2, 0, 1, 3);

        Assert.Equal(head, screen.Head);
        Assert.Equal(history, screen.GetScrollbackRow(0));
        Assert.Equal((uint)'D', screen.GetRowCells(2)[0].Rune);
        Assert.Equal(2, screen.GetRowEndCol(2));
        Assert.Empty(screen.ValidateInvariants());

        var layout = screen.BuildSourceLayout(0, new Screen.SourceLayout());
        using Screen reflowed = screen.ReflowWithOptions(
            3, 8, layout, new Screen.ReflowWorkspace(), new ReflowMapping(), 0, false, 4);
        Assert.Equal((uint)'D', reflowed.GetRowCells(0)[0].Rune);
        Assert.Equal(2, reflowed.GetRowEndCol(0));
    }

    [Fact]
    public void CopyPartialWideSourceAndDestinationCutsDoNotLeaveOrphans()
    {
        using var screen = new Screen(2, 7, scrollbackCapacity: 2);
        Set(screen, 0, 1, 0x4e00, 2);
        Set(screen, 0, 2, 0, 1, true);
        screen.GetCellRef(0, 1).StyleId = 3;
        screen.GetCellRef(0, 2).StyleId = 3;
        screen.SetColdGraphemeIndex(0, 1, 6);
        Set(screen, 0, 0, 'A');
        Set(screen, 1, 2, 0x4e00, 2);
        Set(screen, 1, 3, 0, 1, true);
        screen.GetCellRef(1, 2).StyleId = 8;
        screen.CopyRectangle(0, 0, 1, 3, 1, 1);
        Assert.Equal((uint)32, screen.GetRowCells(1)[2].Rune);
        Assert.Equal((uint)'A', screen.GetRowCells(1)[3].Rune);
        Set(screen, 0, 1, 0x4e00, 2);
        Set(screen, 0, 2, 0, 1, true);
        screen.CopyRectangle(0, 2, 1, 0, 1, 1);
        Assert.Equal((uint)32, screen.GetRowCells(1)[0].Rune);
        Assert.Equal((short)-1, screen.GetRowColdCells(1)[0].GraphemeIndex);
        screen.CopyRectangle(0, 1, 1, 3, 1, 1);
        Assert.Equal((uint)32, screen.GetRowCells(1)[2].Rune);
        Assert.Equal((uint)32, screen.GetRowCells(1)[3].Rune);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void OverlappingWideCopyDoesNotRepairItsSourceBeforeMovingIt()
    {
        using var screen = new Screen(1, 6, scrollbackCapacity: 0);
        Set(screen, 0, 0, '界', 2);
        Set(screen, 0, 1, 0, continuation: true);
        screen.GetCellRef(0, 0).StyleId = 3;
        screen.SetColdGraphemeIndex(0, 0, 5);
        screen.SetColdHyperlink(0, 0, 9);

        screen.CopyRectangle(0, 0, 0, 1, 1, 2);

        Assert.Equal(32u, screen.GetRowCells(0)[0].Rune);
        Assert.Equal((uint)'界', screen.GetRowCells(0)[1].Rune);
        Assert.Equal((byte)2, screen.GetRowCells(0)[1].Width);
        Assert.True(screen.GetRowCells(0)[2].IsContinuation);
        Assert.Equal((short)5, screen.GetRowColdCells(0)[1].GraphemeIndex);
        Assert.Equal((ushort)9, screen.GetRowColdCells(0)[1].HyperlinkId);
        Assert.Empty(screen.ValidateInvariants());

        screen.CopyRectangle(0, 1, 0, 0, 1, 2);

        Assert.Equal((uint)'界', screen.GetRowCells(0)[0].Rune);
        Assert.True(screen.GetRowCells(0)[1].IsContinuation);
        Assert.Equal(32u, screen.GetRowCells(0)[2].Rune);
        Assert.Equal((short)5, screen.GetRowColdCells(0)[0].GraphemeIndex);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public void WipeProtectsEitherHalfOfAWideGlyphAndRetainsRenditionWhenProtectionIsIgnored(int left, int right)
    {
        using var screen = new Screen(1, 6, scrollbackCapacity: 0);
        Set(screen, 0, 0, 'A');
        Set(screen, 0, 1, '界', 2);
        Set(screen, 0, 2, 0, continuation: true);
        Set(screen, 0, 3, 'B');
        for (int column = 1; column <= 2; column++)
        {
            screen.GetCellRef(0, column).StyleId = 7;
            screen.GetCellRef(0, column).IsProtected = true;
            screen.SetColdHyperlink(0, column, 11);
        }
        screen.SetColdGraphemeIndex(0, 1, 5);

        screen.WipeRectangle(0, left, 0, right, respectProtection: true);

        Assert.Equal((uint)'界', screen.GetRowCells(0)[1].Rune);
        Assert.True(screen.GetRowCells(0)[2].IsContinuation);
        Assert.Equal(32u, screen.GetRowCells(0)[left == 0 ? 0 : 3].Rune);
        Assert.Equal((ushort)11, screen.GetRowColdCells(0)[1].HyperlinkId);

        screen.WipeRectangle(0, left, 0, right, respectProtection: false);

        for (int column = 1; column <= 2; column++)
        {
            var cell = screen.GetRowCells(0)[column];
            Assert.Equal(32u, cell.Rune);
            Assert.Equal((byte)1, cell.Width);
            Assert.False(cell.IsContinuation);
            Assert.True(cell.IsProtected);
            Assert.Equal((ushort)7, cell.StyleId);
            Assert.Equal(default, screen.GetRowColdCells(0)[column]);
        }
        Assert.False(screen.RowColdFlags[screen.GetPhysicalRow(0)]);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void FillRepairsWideCutsAndClearsOldColdDataWithoutChangingOutsideRendition()
    {
        using var screen = new Screen(1, 6, scrollbackCapacity: 0);
        Set(screen, 0, 1, '界', 2);
        Set(screen, 0, 2, 0, continuation: true);
        screen.GetCellRef(0, 1).StyleId = 7;
        screen.SetColdGraphemeIndex(0, 1, 5);
        screen.SetColdHyperlink(0, 1, 11);
        Set(screen, 0, 3, 'B');

        screen.FillRectangle(0, 2, 0, 2, new CellHot { Rune = 'F', StyleId = 4, IsProtected = true });

        Assert.Equal(32u, screen.GetRowCells(0)[1].Rune);
        Assert.Equal((ushort)7, screen.GetRowCells(0)[1].StyleId);
        Assert.Equal((uint)'F', screen.GetRowCells(0)[2].Rune);
        Assert.Equal((ushort)4, screen.GetRowCells(0)[2].StyleId);
        Assert.True(screen.GetRowCells(0)[2].IsProtected);
        Assert.Equal((uint)'B', screen.GetRowCells(0)[3].Rune);
        Assert.False(screen.RowColdFlags[screen.GetPhysicalRow(0)]);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void CopyingUnwrittenCellsPreservesTheExtentOfAWrappedLine()
    {
        using var screen = new Screen(4, 6, scrollbackCapacity: 0);
        for (int column = 0; column < 6; column++)
            Set(screen, 1, column, (uint)('A' + column));
        Set(screen, 2, 0, 'Y');
        Set(screen, 3, 0, 'Z');
        screen.SetRowContinuation(2, true);
        screen.SetRowContinuation(3, true);

        screen.CopyRectangle(0, 0, 2, 0, 1, 6);

        Assert.Equal(5, screen.GetRowEndCol(2));
        Assert.True(screen.GetRowContinuesPrevious(2));
        Assert.True(screen.GetRowContinuesPrevious(3));
        Assert.Empty(screen.ValidateInvariants());
        var layout = screen.BuildSourceLayout(0, new Screen.SourceLayout());
        using Screen reflowed = screen.ReflowWithOptions(
            5, 4, layout, new Screen.ReflowWorkspace(), new ReflowMapping(), 0, false, 0);
        Assert.Equal((uint)'A', reflowed.GetRowCells(1)[0].Rune);
        Assert.Equal((uint)'E', reflowed.GetRowCells(2)[0].Rune);
        Assert.Equal((uint)'Z', reflowed.GetRowCells(4)[0].Rune);
        Assert.Empty(reflowed.ValidateInvariants());
    }

    private static void Set(Screen screen, int row, int col, uint rune, byte width = 1, bool continuation = false)
    {
        ref CellHot cell = ref screen.GetCellRef(row, col);
        cell.Rune = rune; cell.Width = width; cell.IsContinuation = continuation;
        screen.UpdateRowMaxCol(row, col); screen.MarkRowEnd(row, col);
    }
}
