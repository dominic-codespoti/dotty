using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class ReflowResizeTests
{
    [Fact]
    public void SoftWrappedLineRoundTripsAcrossResize()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 8);
        buffer.WriteText("abcdefghij".AsSpan(), CellAttributes.Default);

        buffer.Resize(3, 4);

        Assert.Equal("abcd", buffer.GetRowText(0));
        Assert.Equal("efgh", buffer.GetRowText(1));
        Assert.Equal("ij  ", buffer.GetRowText(2));

        buffer.Resize(3, 8);

        Assert.Equal("abcdefgh", buffer.GetRowText(0));
        Assert.Equal("ij      ", buffer.GetRowText(1));
    }

    [Fact]
    public void ResizePreservesScrollbackOrderAfterRingRotation()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5, scrollbackCapacity: 3);
        for (int i = 0; i < 5; i++)
            buffer.WriteText($"L{i}\r\n".AsSpan(), CellAttributes.Default);

        buffer.Resize(2, 3);

        Assert.True(buffer.ScrollbackCount <= 3);
        Assert.Contains("L", string.Join("|", buffer.GetScrollbackLines()));
        Assert.Contains("L4", buffer.GetRowText(0));
    }
}
