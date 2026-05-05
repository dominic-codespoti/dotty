using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class BufferWriterTests
{
    [Fact]
    public void BackspaceErasesWideGlyph()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 8);
        buffer.WriteText("漢".AsSpan(), CellAttributes.Default);
        Assert.Equal(2, buffer.CursorCol);

        buffer.WriteText("\b".AsSpan(), CellAttributes.Default);

        Assert.Equal(0, buffer.CursorCol);
        var cell0 = buffer.GetCell(0, 0);
        var cold0 = buffer.GetColdCell(0, 0);
        Assert.True(cell0.IsEmpty);
        var cell1 = buffer.GetCell(0, 1);
        var cold1 = buffer.GetColdCell(0, 1);
        Assert.True(cell1.IsEmpty);
    }

    [Fact]
    public void TabAdvancesToNextStop()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 16);
        buffer.WriteText("A\tB".AsSpan(), CellAttributes.Default);

        var cell = buffer.GetCell(0, 8);
        var cold = buffer.GetColdCell(0, 8);
        Assert.Equal("B", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void CombiningMarkAttachesToBase()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("a\u0301".AsSpan(), CellAttributes.Default);

        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal("a\u0301", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
        Assert.Equal(1, buffer.CursorCol);
    }
}
