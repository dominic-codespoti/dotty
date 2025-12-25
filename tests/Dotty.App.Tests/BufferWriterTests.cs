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
        Assert.True(string.IsNullOrEmpty(buffer.GetCell(0, 0).Grapheme));
        Assert.True(string.IsNullOrEmpty(buffer.GetCell(0, 1).Grapheme));
    }

    [Fact]
    public void TabAdvancesToNextStop()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 16);
        buffer.WriteText("A\tB".AsSpan(), CellAttributes.Default);

        Assert.Equal("B", buffer.GetCell(0, 8).Grapheme);
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void CombiningMarkAttachesToBase()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("a\u0301".AsSpan(), CellAttributes.Default);

        var cell = buffer.GetCell(0, 0);
            Assert.Equal("a\u0301", cell.Grapheme);
        Assert.Equal(1, buffer.CursorCol);
    }
}
