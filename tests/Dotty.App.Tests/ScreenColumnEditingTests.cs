using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScreenColumnEditingTests
{
    [Fact]
    public void InsertAndDeleteMoveHotColdAndProtectionDataAndRefreshMetadata()
    {
        using var screen = new Screen(2, 8, scrollbackCapacity: 3);
        Set(screen, 0, 1, 'A', 5);
        Set(screen, 0, 2, 'B', 9);
        Set(screen, 0, 3, 'C', 7);
        screen.SetColdHyperlink(0, 2, 31);
        screen.SetColdGraphemeIndex(0, 2, 6);
        screen.GetCellRef(0, 2).IsProtected = true;
        screen.SetColdGraphemeIndex(0, 3, -8); // placeholder metadata
        int head = screen.Head;

        screen.InsertColumns(0, 2, 6, 2);

        Assert.Equal((uint)'A', screen.GetRowCells(0)[1].Rune);
        Assert.Equal(0u, screen.GetRowCells(0)[2].Rune);
        Assert.Equal((uint)'B', screen.GetRowCells(0)[4].Rune);
        Assert.Equal((ushort)9, screen.GetRowCells(0)[4].StyleId);
        Assert.True(screen.GetRowCells(0)[4].IsProtected);
        Assert.Equal((ushort)31, screen.GetRowColdCells(0)[4].HyperlinkId);
        Assert.Equal((short)6, screen.GetRowColdCells(0)[4].GraphemeIndex);
        Assert.Equal((short)-8, screen.GetRowColdCells(0)[5].GraphemeIndex);
        Assert.Equal((uint)'C', screen.GetRowCells(0)[5].Rune);
        Assert.Equal(5, screen.GetRowMaxCol(0));
        Assert.Equal(5, screen.GetRowEndCol(0));
        Assert.Equal(head, screen.Head);

        screen.DeleteColumns(0, 2, 6, 2);
        Assert.Equal((ushort)31, screen.GetRowColdCells(0)[2].HyperlinkId);
        Assert.True(screen.GetRowCells(0)[2].IsProtected);
        Assert.Equal((uint)'C', screen.GetRowCells(0)[3].Rune);
        Assert.Equal((short)-8, screen.GetRowColdCells(0)[3].GraphemeIndex);
        Assert.Equal(3, screen.GetRowMaxCol(0));
        Assert.Equal(3, screen.GetRowEndCol(0));
        Assert.True(screen.RowColdFlags[screen.GetPhysicalRow(0)]);
        Assert.Equal(head, screen.Head);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void WideGlyphCrossingEditAndTruncationCutsIsClearedAsAWholeBeforeRepairAccessors()
    {
        using var screen = new Screen(1, 8, scrollbackCapacity: 0);
        Set(screen, 0, 1, 'x');
        Set(screen, 0, 2, '界', 1, 2);
        Set(screen, 0, 3, 0, 1, continuation: true);
        Set(screen, 0, 4, 'y');
        screen.SetColdHyperlink(0, 2, 12);
        screen.SetColdHyperlink(0, 3, 12);

        screen.InsertColumns(0, 3, 6, 1); // starts on continuation

        var raw = screen.GetRowCells(0);
        var cold = screen.GetRowColdCells(0);
        Assert.Equal(0u, raw[2].Rune);
        Assert.False(raw[2].IsContinuation);
        Assert.Equal(0u, raw[3].Rune);
        Assert.False(raw[3].IsContinuation);
        Assert.Equal((ushort)0, cold[2].HyperlinkId);
        Assert.Equal((ushort)0, cold[3].HyperlinkId);
        Assert.Empty(screen.ValidateInvariants());
    }

    [Fact]
    public void InvalidAndSaturatedEditsAreSafeAndNeverRotateRing()
    {
        using var screen = new Screen(2, 5, scrollbackCapacity: 4);
        Set(screen, 0, 1, 'a');
        Set(screen, 0, 2, 'b');
        int head = screen.Head;
        screen.InsertColumns(0, 1, 3, int.MaxValue);
        Assert.Equal(0u, screen.GetRowCells(0)[1].Rune);
        Assert.Equal(head, screen.Head);
        Set(screen, 0, 1, 'a');
        Set(screen, 0, 2, 'b');
        screen.DeleteColumns(0, 1, 3, int.MaxValue);
        Assert.Equal(0u, screen.GetRowCells(0)[1].Rune);
        screen.InsertColumns(-1, 0, 2, 1);
        screen.DeleteColumns(0, 4, int.MaxValue, 1);
        Assert.Equal(head, screen.Head);
        Assert.Empty(screen.ValidateInvariants());
    }


    [Fact]
    public void ColumnMovesPreserveInteriorWideGlyphsAndClearEachTruncatedCutWithoutRepairAccessors()
    {
        using var retained = new Screen(1, 8, scrollbackCapacity: 0);
        Set(retained, 0, 2, '界', width: 2);
        Set(retained, 0, 3, 0, continuation: true);
        retained.InsertColumns(0, 1, 6, 1);
        Assert.True(retained.GetRowCells(0)[3].Width > 1);
        Assert.True(retained.GetRowCells(0)[4].IsContinuation);
        Assert.Empty(retained.ValidateInvariants());

        using var insertTail = new Screen(1, 8, scrollbackCapacity: 0);
        Set(insertTail, 0, 5, '界', width: 2);
        Set(insertTail, 0, 6, 0, continuation: true);
        insertTail.InsertColumns(0, 1, 6, 1);
        AssertClearedRaw(insertTail, 5, 6);

        using var rightEdge = new Screen(1, 8, scrollbackCapacity: 0);
        Set(rightEdge, 0, 5, '界', width: 2);
        Set(rightEdge, 0, 6, 0, continuation: true);
        rightEdge.InsertColumns(0, 1, 5, 1);
        AssertClearedRaw(rightEdge, 5, 6);

        using var deleteHead = new Screen(1, 8, scrollbackCapacity: 0);
        Set(deleteHead, 0, 1, '界', width: 2);
        Set(deleteHead, 0, 2, 0, continuation: true);
        deleteHead.DeleteColumns(0, 1, 6, 1);
        AssertClearedRaw(deleteHead, 1, 2);
    }


    [Fact]
    public void ClearingAnEditedRowDetachesTheFollowingSoftWrappedRow()
    {
        using var screen = new Screen(2, 5, scrollbackCapacity: 0);
        Set(screen, 0, 0, 'a');
        Set(screen, 1, 0, 'b');
        screen.SetRowContinuation(1, true);

        screen.DeleteColumns(0, 0, 4, int.MaxValue);

        Assert.Equal(-1, screen.GetRowEndCol(0));
        Assert.False(screen.GetRowContinuesPrevious(1));
        Assert.Empty(screen.ValidateInvariants());
    }
    private static void AssertClearedRaw(Screen screen, int start, int end)
    {
        var raw = screen.GetRowCells(0);
        for (int col = start; col <= end; col++)
        {
            Assert.Equal(0u, raw[col].Rune);
            Assert.False(raw[col].IsContinuation);
        }
        Assert.Empty(screen.ValidateInvariants());
    }
    private static void Set(Screen screen, int row, int col, uint rune, ushort style = 0, byte width = 1, bool continuation = false)
    {
        ref var cell = ref screen.GetCellRef(row, col);
        cell.Rune = rune;
        cell.StyleId = style;
        cell.Width = width;
        cell.IsContinuation = continuation;
        screen.UpdateRowMaxCol(row, col);
        screen.MarkRowEnd(row, col);
    }
}
