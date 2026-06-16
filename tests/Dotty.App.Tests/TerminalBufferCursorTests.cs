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
}
