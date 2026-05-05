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

        Assert.Equal("ABCD", buffer.GetRowText(0));
        Assert.Equal("1234", buffer.GetRowText(1));
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
}
