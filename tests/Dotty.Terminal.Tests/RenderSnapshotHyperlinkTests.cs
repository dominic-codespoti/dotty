using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class RenderSnapshotHyperlinkTests
{
    [Fact]
    public void Snapshot_ResolvesSameUrlAsBuffer()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 20, scrollbackCapacity: 10);
        ushort id = buffer.GetOrCreateHyperlinkId("https://e.test/one");

        using RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();

        Assert.Equal(buffer.GetHyperlinkUrl(id), snapshot.GetHyperlinkUrl(id));
        Assert.Equal("https://e.test/one", snapshot.GetHyperlinkUrl(id));
        Assert.Null(snapshot.GetHyperlinkUrl(0));
        Assert.Null(snapshot.GetHyperlinkUrl(500));
    }

    [Fact]
    public void Snapshot_KeepsResolvingAfterBufferAppendsMoreLinks()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 20, scrollbackCapacity: 10);
        ushort first = buffer.GetOrCreateHyperlinkId("https://e.test/first");

        using RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();
        ushort last = 0;
        for (int i = 0; i < 200; i++)
            last = buffer.GetOrCreateHyperlinkId("https://e.test/later" + i);

        Assert.Equal("https://e.test/first", snapshot.GetHyperlinkUrl(first));
        // Entries added after capture are not part of the snapshot.
        Assert.Null(snapshot.GetHyperlinkUrl(last));
        Assert.Equal("https://e.test/later199", buffer.GetHyperlinkUrl(last));
    }

    [Fact]
    public void DisposedSnapshot_ReturnsNull()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 20, scrollbackCapacity: 10);
        ushort id = buffer.GetOrCreateHyperlinkId("https://e.test/one");
        RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();

        snapshot.Dispose();

        Assert.Null(snapshot.GetHyperlinkUrl(id));
    }
}
