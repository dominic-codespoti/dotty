using System;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Lease lifecycle of <see cref="RenderSnapshot.Graphics"/>; the capture that fills it is a later stage.</summary>
public sealed class RenderSnapshotGraphicsTests
{
    private static ImagePixels NewPixels() => ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgba8Premultiplied);

    private static KittyRenderPlacement Placement(ImagePixels pixels) => new()
    {
        Pixels = pixels,
        ImageSerial = pixels.Serial,
        Cols = 1,
        Rows = 1,
        U1 = 1f,
        V1 = 1f,
    };

    private static void AssertAlive(ImagePixels pixels) => pixels.Retain().Release();

    private static void AssertFreed(ImagePixels pixels) =>
        Assert.Throws<ObjectDisposedException>(() => pixels.Retain());

    [Fact]
    public void FreshSnapshotHasNoGraphics()
    {
        using var buffer = new TerminalBuffer(4, 10);
        using RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();

        Assert.Null(snapshot.Graphics);
    }

    [Fact]
    public void Dispose_ReleasesTheLeasesAndClearsGraphics()
    {
        using var buffer = new TerminalBuffer(4, 10);
        ImagePixels pixels = NewPixels();
        RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();
        KittyRenderSnapshot graphics = snapshot.BeginGraphics();
        graphics.Add(Placement(pixels));
        pixels.Dispose(); // the capture's lease is now the only reference

        Assert.Same(graphics, snapshot.Graphics);
        Assert.Equal(1, graphics.Placements.Length);
        AssertAlive(pixels);

        snapshot.Dispose();

        Assert.Null(snapshot.Graphics);
        Assert.Equal(0, graphics.Placements.Length);
        AssertFreed(pixels);
    }

    [Fact]
    public void BeginGraphics_ReusesOnePooledInstanceAndReleasesWhatItHeld()
    {
        using var buffer = new TerminalBuffer(4, 10);
        using RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();
        ImagePixels pixels = NewPixels();
        KittyRenderSnapshot first = snapshot.BeginGraphics();
        first.Add(Placement(pixels));
        pixels.Dispose();

        KittyRenderSnapshot second = snapshot.BeginGraphics();

        Assert.Same(first, second);
        Assert.Equal(0, second.Placements.Length);
        AssertFreed(pixels);
    }

    [Fact]
    public void AttachGraphics_ReplacingReleasesThePreviousLeases()
    {
        using var buffer = new TerminalBuffer(4, 10);
        RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();
        ImagePixels firstPixels = NewPixels();
        ImagePixels secondPixels = NewPixels();
        var first = new KittyRenderSnapshot();
        first.Add(Placement(firstPixels));
        firstPixels.Dispose();
        var second = new KittyRenderSnapshot();
        second.Add(Placement(secondPixels));
        secondPixels.Dispose();

        snapshot.AttachGraphics(first);
        AssertAlive(firstPixels);
        snapshot.AttachGraphics(second);

        Assert.Same(second, snapshot.Graphics);
        AssertFreed(firstPixels);
        AssertAlive(secondPixels);

        snapshot.Dispose();

        Assert.Null(snapshot.Graphics);
        AssertFreed(secondPixels);
    }

    [Fact]
    public void AttachGraphics_SameInstanceAgainKeepsItsLeases()
    {
        using var buffer = new TerminalBuffer(4, 10);
        RenderSnapshot snapshot = buffer.CaptureRenderSnapshotVisible();
        ImagePixels pixels = NewPixels();
        var graphics = new KittyRenderSnapshot();
        graphics.Add(Placement(pixels));
        pixels.Dispose();
        snapshot.AttachGraphics(graphics);

        snapshot.AttachGraphics(graphics);

        AssertAlive(pixels);
        Assert.Equal(1, graphics.Placements.Length);
        snapshot.Dispose();
        AssertFreed(pixels);
    }
}
