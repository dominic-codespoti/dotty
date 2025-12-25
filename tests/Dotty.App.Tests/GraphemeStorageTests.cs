using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class GraphemeStorageTests
{
    [Fact]
    public void Stores_BoxDrawing_Grapheme()
    {
        var buffer = new TerminalBuffer(rows:1, columns:4);
        buffer.WriteText("│".AsSpan(), CellAttributes.Default);
        Assert.Equal("│", buffer.GetCell(0,0).Grapheme);
    }

    [Fact]
    public void Stores_Powerline_PUA_Grapheme()
    {
        var buffer = new TerminalBuffer(rows:1, columns:4);
        buffer.WriteText("\uE0B0".AsSpan(), CellAttributes.Default);
        Assert.Equal("\uE0B0", buffer.GetCell(0,0).Grapheme);
    }
}
