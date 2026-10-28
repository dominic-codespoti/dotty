using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

[CollectionDefinition(nameof(GraphicsMemoryMetricsCollection), DisableParallelization = true)]
public sealed class GraphicsMemoryMetricsCollection { }

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class ImagePixelsTests
{
    [Fact]
    public void AllocateReportsLayoutAndZeroFills()
    {
        using var rgba = ImagePixels.Allocate(5, 3, ImagePixelFormat.Rgba8Premultiplied);
        Assert.Equal(5, rgba.Width);
        Assert.Equal(3, rgba.Height);
        Assert.Equal(20, rgba.Stride);
        Assert.Equal(60, rgba.ByteLength);
        Assert.Equal(60, rgba.AsSpan().Length);
        Assert.True(rgba.AsSpan().IndexOfAnyExcept((byte)0) < 0);

        using var rgb = ImagePixels.Allocate(5, 3, ImagePixelFormat.Rgb8);
        Assert.Equal(15, rgb.Stride);
        Assert.Equal(45, rgb.ByteLength);
        Assert.Equal(ImagePixelFormat.Rgb8, rgb.Format);
    }

    [Fact]
    public unsafe void WritableSpanAndPointerAliasTheSameMemory()
    {
        using var px = ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgba8Premultiplied);
        px.AsWritableSpan()[7] = 0xAB;
        Assert.Equal(0xAB, px.Pointer[7]);
        Assert.Equal(0xAB, px.AsSpan()[7]);
    }

    [Fact]
    public void SerialsAreUniqueAndIncreasing()
    {
        using var a = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgb8);
        using var b = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgb8);
        using var c = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgb8);
        Assert.True(a.Serial < b.Serial);
        Assert.True(b.Serial < c.Serial);
        // A retained reference is the same object, hence the same GL cache key.
        Assert.Same(a, a.Retain());
        Assert.Equal(a.Serial, a.Serial);
        a.Release();
    }

    [Fact]
    public void SerialsStayUniqueUnderConcurrentAllocation()
    {
        var serials = new long[2000];
        Parallel.For(0, serials.Length, i =>
        {
            using var px = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgb8);
            serials[i] = px.Serial;
        });
        Assert.Equal(serials.Length, serials.Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    [InlineData(int.MinValue, 1)]
    public void NonPositiveDimensionsThrowWithoutTouchingMetrics(int w, int h)
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePixels.Allocate(w, h, ImagePixelFormat.Rgba8Premultiplied));
        Assert.Equal(before, GraphicsMemoryMetrics.GetSnapshot());
    }

    [Theory]
    [InlineData(int.MaxValue, 1, ImagePixelFormat.Rgba8Premultiplied)] // stride > int.MaxValue
    [InlineData(int.MaxValue, 1, ImagePixelFormat.Rgb8)]
    [InlineData(46341, 46341, ImagePixelFormat.Rgba8Premultiplied)]   // > 2 GiB total, 64-bit product still exact
    [InlineData(1, int.MaxValue, ImagePixelFormat.Rgba8Premultiplied)] // 4 * int.MaxValue
    [InlineData(int.MaxValue, int.MaxValue, ImagePixelFormat.Rgb8)]
    public void OversizedImagesThrowWithoutAllocating(int w, int h, ImagePixelFormat format)
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePixels.Allocate(w, h, format));
        Assert.Equal(before, GraphicsMemoryMetrics.GetSnapshot());
    }

    [Fact]
    public void UndefinedFormatThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePixels.Allocate(2, 2, (ImagePixelFormat)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePixels.Allocate(2, 2, (ImagePixelFormat)0));
    }

    [Fact]
    public void MetricsTrackAllocationAndLastRelease()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        var px = ImagePixels.Allocate(10, 10, ImagePixelFormat.Rgba8Premultiplied);
        var live = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Equal(400, live.LiveImagePixelBytes - before.LiveImagePixelBytes);
        Assert.Equal(1, live.LiveImageCount - before.LiveImageCount);
        Assert.Equal(400, live.ImagePixelBytesAllocated - before.ImagePixelBytesAllocated);

        px.Retain();
        px.Retain();
        px.Release();
        px.Release();
        Assert.Equal(400, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes - before.LiveImagePixelBytes);

        px.Release(); // creator's reference
        var after = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Equal(before.LiveImagePixelBytes, after.LiveImagePixelBytes);
        Assert.Equal(before.LiveImageCount, after.LiveImageCount);
        // The cumulative counter never goes down.
        Assert.Equal(400, after.ImagePixelBytesAllocated - before.ImagePixelBytesAllocated);
    }

    [Fact]
    public void RetainedReferenceKeepsDataReadableAfterCreatorDisposes()
    {
        var px = ImagePixels.Allocate(2, 1, ImagePixelFormat.Rgb8);
        px.AsWritableSpan().Fill(0x5A);
        var lease = px.Retain();
        px.Dispose();
        Assert.Equal(6, lease.AsSpan().Length);
        Assert.All(lease.AsSpan().ToArray(), b => Assert.Equal(0x5A, b));
        lease.Release();
    }

    [Fact]
    public void ReleasePastZeroThrowsAndDoesNotCorruptMetrics()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        var px = ImagePixels.Allocate(4, 4, ImagePixelFormat.Rgb8);
        px.Release();
        Assert.Throws<ObjectDisposedException>(() => px.Release());
        Assert.Throws<ObjectDisposedException>(() => px.Retain());
        Assert.Throws<ObjectDisposedException>(() => px.AsSpan().Length);
        Assert.Throws<ObjectDisposedException>(() => px.AsWritableSpan().Length);
        Assert.Equal(before.LiveImagePixelBytes, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes);
        Assert.Equal(before.LiveImageCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
    }

    [Fact]
    public void DisposeReleasesOnlyOnce()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        var px = ImagePixels.Allocate(4, 4, ImagePixelFormat.Rgb8);
        px.Retain();
        px.Dispose();
        px.Dispose(); // must not consume the lease's reference
        Assert.Equal(48, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes - before.LiveImagePixelBytes);
        px.Release();
        Assert.Equal(before.LiveImagePixelBytes, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes);
    }

    [Fact]
    public void ConcurrentRetainReleaseBalances()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        var px = ImagePixels.Allocate(8, 8, ImagePixelFormat.Rgba8Premultiplied);
        px.AsWritableSpan().Fill(1);
        Parallel.For(0, 50_000, _ =>
        {
            var lease = px.Retain();
            // The memory must stay valid while any lease is held.
            if (lease.AsSpan()[0] != 1) throw new InvalidOperationException("data changed");
            lease.Release();
        });
        Assert.Equal(256, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes - before.LiveImagePixelBytes);
        px.Release();
        Assert.Equal(before.LiveImagePixelBytes, GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes);
        Assert.Equal(before.LiveImageCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
    }

    [Fact]
    public void ConcurrentLeasesFreeExactlyOnceWhenCreatorReleasesFirst()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        for (int round = 0; round < 200; round++)
        {
            var px = ImagePixels.Allocate(3, 3, ImagePixelFormat.Rgb8);
            var leases = new ImagePixels[8];
            for (int i = 0; i < leases.Length; i++) leases[i] = px.Retain();
            px.Release();
            Parallel.For(0, leases.Length, i => leases[i].Release());
        }
        var after = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Equal(before.LiveImagePixelBytes, after.LiveImagePixelBytes);
        Assert.Equal(before.LiveImageCount, after.LiveImageCount);
    }

    [Fact]
    public void PendingTransferAndPlacementCountersRoundTrip()
    {
        var before = GraphicsMemoryMetrics.GetSnapshot();
        GraphicsMemoryMetrics.ChangePendingTransferBytes(1234);
        GraphicsMemoryMetrics.ChangeLivePlacements(3);
        var mid = GraphicsMemoryMetrics.GetSnapshot();
        Assert.Equal(1234, mid.PendingTransferBytes - before.PendingTransferBytes);
        Assert.Equal(3, mid.LivePlacementCount - before.LivePlacementCount);
        // Pending/placement counters do not feed the live pixel numbers.
        Assert.Equal(before.LiveImagePixelBytes, mid.LiveImagePixelBytes);
        GraphicsMemoryMetrics.ChangePendingTransferBytes(-1234);
        GraphicsMemoryMetrics.ChangeLivePlacements(-3);
        Assert.Equal(before, GraphicsMemoryMetrics.GetSnapshot());
    }
}
