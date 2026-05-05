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
        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal("│", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
    }

    [Fact]
    public void Stores_Powerline_PUA_Grapheme()
    {
        var buffer = new TerminalBuffer(rows:1, columns:4);
        buffer.WriteText("\uE0B0".AsSpan(), CellAttributes.Default);
        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal("\uE0B0", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
    }
}
