using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public class DecscusrTests
{
    [Theory]
    [InlineData(0, TerminalCursorShape.Block, true, true)]
    [InlineData(1, TerminalCursorShape.Block, true, false)]
    [InlineData(2, TerminalCursorShape.Block, false, false)]
    [InlineData(3, TerminalCursorShape.Underline, true, false)]
    [InlineData(4, TerminalCursorShape.Underline, false, false)]
    [InlineData(5, TerminalCursorShape.Beam, true, false)]
    [InlineData(6, TerminalCursorShape.Beam, false, false)]
    [InlineData(7, TerminalCursorShape.Block, true, true)]
    public void Decscusr_Sets_CursorShape_And_Blinking_On_Buffer_And_Snapshot(
        int param,
        TerminalCursorShape expectedShape,
        bool expectedBlinking,
        bool expectedDefault)
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;
        // Start from an explicit style so DECSCUSR 0 has something to undo.
        parser.Feed(Encoding.ASCII.GetBytes("\x1b[4 q"));

        // CSI Ps SP q sequence
        byte[] seq = Encoding.ASCII.GetBytes($"\x1b[{param} q");
        parser.Feed(seq);

        Assert.Equal(expectedShape, adapter.Buffer.CursorShape);
        Assert.Equal(expectedBlinking, adapter.Buffer.CursorBlinking);
        Assert.Equal(expectedDefault, adapter.Buffer.CursorStyleIsDefault);

        using var snapVisible = adapter.Buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: 0);
        Assert.Equal(expectedShape, snapVisible.CursorShape);
        Assert.Equal(expectedBlinking, snapVisible.CursorBlinking);
        Assert.Equal(expectedDefault, snapVisible.CursorStyleIsDefault);
    }

    [Fact]
    public void SoftReset_Restores_The_Configured_Default_Style()
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;
        Assert.True(adapter.Buffer.CursorStyleIsDefault);

        parser.Feed(Encoding.ASCII.GetBytes("\x1b[6 q"));
        Assert.False(adapter.Buffer.CursorStyleIsDefault);

        parser.Feed(Encoding.ASCII.GetBytes("\x1b[!p"));
        Assert.True(adapter.Buffer.CursorStyleIsDefault);
    }

    [Fact]
    public void SetCursorStyle_Direct_Call_Updates_Buffer_And_Snapshot()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursorStyle(TerminalCursorShape.Beam, false);

        Assert.Equal(TerminalCursorShape.Beam, buffer.CursorShape);
        Assert.False(buffer.CursorBlinking);

        using var snapshot = buffer.CaptureRenderSnapshotVisible(sbStart: 0, sbEnd: 0);
        Assert.Equal(TerminalCursorShape.Beam, snapshot.CursorShape);
        Assert.False(snapshot.CursorBlinking);
    }
}
