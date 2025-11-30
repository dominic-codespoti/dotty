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
        Assert.Equal("汉", wideCell.Grapheme);
        Assert.Equal(2, wideCell.Width);
        Assert.True(buffer.GetCell(0, 2).IsContinuation);
    }
}
