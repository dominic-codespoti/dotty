using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class ScrollRegionTests
{
    [Fact]
    public void SetScrollRegion_UsesOneBasedCoordinates_AndHomesCursor()
    {
        var tb = new TerminalBuffer(rows: 20, columns: 40);

        tb.SetCursor(10, 12);
        tb.SetScrollRegion(2, 5);

        Assert.Equal(0, tb.CursorCol);
        Assert.Equal(0, tb.CursorRow);

        for (int row = 0; row < tb.Rows; row++)
        {
            tb.SetCursor(row, 0);
            tb.WriteText($"L{row:00}".AsSpan(), CellAttributes.Default);
        }

        tb.SetCursor(4, 0);
        tb.LineFeed();

        Assert.Equal("L00", tb.GetRowText(0).Trim());
        Assert.Equal("L02", tb.GetRowText(1).Trim());
        Assert.Equal("L03", tb.GetRowText(2).Trim());
        Assert.Equal("L04", tb.GetRowText(3).Trim());
        Assert.Equal(string.Empty, tb.GetRowText(4).Trim());
    }

    [Fact]
    public void SetScrollRegion_WithInvalidRange_ResetsToFullScreen()
    {
        var tb = new TerminalBuffer(rows: 10, columns: 20);

        tb.SetCursor(5, 5);
        tb.SetScrollRegion(1, 1);

        Assert.Equal(0, tb.CursorRow);
        Assert.Equal(0, tb.CursorCol);

        for (int row = 0; row < tb.Rows; row++)
        {
            tb.SetCursor(row, 0);
            tb.WriteText($"R{row}".AsSpan(), CellAttributes.Default);
        }

        tb.SetCursor(tb.Rows - 1, 0);
        tb.LineFeed();

        Assert.Equal("R1", tb.GetRowText(0).Trim());
    }

    [Fact]
    public void SetOriginMode_HomesCursor_ToRegionOrScreenOrigin()
    {
        var tb = new TerminalBuffer(rows: 20, columns: 40);

        tb.SetScrollRegion(5, 15);
        tb.SetCursor(12, 9);

        tb.SetOriginMode(true);

        Assert.Equal(4, tb.CursorRow);
        Assert.Equal(0, tb.CursorCol);

        tb.SetCursor(3, 7);
        tb.SetOriginMode(false);

        Assert.Equal(0, tb.CursorRow);
        Assert.Equal(0, tb.CursorCol);
    }
}
