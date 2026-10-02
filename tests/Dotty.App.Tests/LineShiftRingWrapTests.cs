using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class LineShiftRingWrapTests
{
    [Fact]
    public void InsertLines_AfterRingWrap_PreservesMovedRowContentAndColdMetadata()
    {
        AssertShiftAfterRingWrap(insert: true);
    }

    [Fact]
    public void DeleteLines_AfterRingWrap_PreservesMovedRowContentAndColdMetadata()
    {
        AssertShiftAfterRingWrap(insert: false);
    }

    private static void AssertShiftAfterRingWrap(bool insert)
    {
        const int rows = 5;
        const int columns = 24;
        var tb = new TerminalBuffer(rows, columns, scrollbackCapacity: 4);
        ushort linkId = tb.GetOrCreateHyperlinkId("https://shift.example.test");
        var linked = new CellAttributes { HyperlinkId = linkId };

        // Advance the circular screen head across its physical wrap boundary.
        for (int i = 0; i < 7; i++)
        {
            tb.SetCursor(rows - 1, 0);
            tb.LineFeed();
        }

        for (int row = 0; row < rows; row++)
        {
            tb.SetCursor(row, 0);
            if (row == 2)
                tb.WriteText("ab❤️cdef".AsSpan(), linked);
            else
                tb.WriteText($"ROW{row}".AsSpan(), CellAttributes.Default);
        }

        var beforeScreen = tb.ActiveScreenForTests;
        int beforePhysicalRow = beforeScreen.GetPhysicalRow(2);
        int beforeMaxCol = beforeScreen.RowMaxCol[beforePhysicalRow];
        int beforeEndCol = beforeScreen.RowEndCol[beforePhysicalRow];
        Assert.True(tb.GetCell(2, 2).HasGrapheme);
        Assert.Equal(linkId, tb.GetColdCell(2, 2).HyperlinkId);

        tb.SetCursor(1, 0);
        if (insert)
            tb.InsertLines(1);
        else
            tb.DeleteLines(1);

        int movedRow = insert ? 3 : 1;
        Assert.StartsWith("ab❤️ cdef", tb.GetRowText(movedRow));
        Assert.Equal("❤️", GraphemeHelper.Resolve(tb.GetCell(movedRow, 2).Rune,
            tb.GetColdCell(movedRow, 2).GraphemeIndex));
        Assert.Equal(linkId, tb.GetColdCell(movedRow, 2).HyperlinkId);

        var afterScreen = tb.ActiveScreenForTests;
        int afterPhysicalRow = afterScreen.GetPhysicalRow(movedRow);
        Assert.Equal(beforeMaxCol, afterScreen.RowMaxCol[afterPhysicalRow]);
        Assert.Equal(beforeEndCol, afterScreen.RowEndCol[afterPhysicalRow]);
        Assert.Empty(tb.ValidateInvariants());
    }
}
