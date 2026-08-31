using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public class DecscusrTests
{
    [Theory]
    [InlineData(0, TerminalCursorShape.Block, true)]
    [InlineData(1, TerminalCursorShape.Block, true)]
    [InlineData(2, TerminalCursorShape.Block, false)]
    [InlineData(3, TerminalCursorShape.Underline, true)]
    [InlineData(4, TerminalCursorShape.Underline, false)]
    [InlineData(5, TerminalCursorShape.Beam, true)]
    [InlineData(6, TerminalCursorShape.Beam, false)]
    public void Decscusr_Sets_CursorShape_And_Blinking_On_Buffer_And_Snapshot(
        int param,
        TerminalCursorShape expectedShape,
        bool expectedBlinking)
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;

        // CSI Ps SP q sequence
        byte[] seq = Encoding.ASCII.GetBytes($"\x1b[{param} q");
        parser.Feed(seq);

        Assert.Equal(expectedShape, adapter.Buffer.CursorShape);
        Assert.Equal(expectedBlinking, adapter.Buffer.CursorBlinking);

        using var snapFull = adapter.Buffer.CaptureRenderSnapshot(0, 0);
        Assert.Equal(expectedShape, snapFull.CursorShape);
        Assert.Equal(expectedBlinking, snapFull.CursorBlinking);

        using var snapVisible = adapter.Buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: 0);
        Assert.Equal(expectedShape, snapVisible.CursorShape);
        Assert.Equal(expectedBlinking, snapVisible.CursorBlinking);
    }

    [Fact]
    public void SetCursorStyle_Direct_Call_Updates_Buffer_And_Snapshot()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursorStyle(TerminalCursorShape.Beam, false);

        Assert.Equal(TerminalCursorShape.Beam, buffer.CursorShape);
        Assert.False(buffer.CursorBlinking);

        using var snapshot = buffer.CaptureRenderSnapshot(0, 0);
        Assert.Equal(TerminalCursorShape.Beam, snapshot.CursorShape);
        Assert.False(snapshot.CursorBlinking);
    }
}
