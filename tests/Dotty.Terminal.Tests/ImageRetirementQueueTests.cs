using System.Collections.Generic;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

// Same non-parallel collection as ImagePixelsTests: the queue and its enabled flag are process-wide.
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class ImageRetirementQueueTests
{
    private static List<long> Drain()
    {
        var drained = new List<long>();
        while (ImageRetirementQueue.TryDequeue(out long serial))
            drained.Add(serial);
        return drained;
    }

    [Fact]
    public void FinalReleaseEnqueuesTheSerialExactlyOnce()
    {
        ImageRetirementQueue.Enabled = true;
        try
        {
            var px = ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgb8);
            long serial = px.Serial;
            px.Retain();

            px.Release();
            Assert.Empty(Drain());

            px.Release();
            Assert.Equal(new[] { serial }, Drain());

            Assert.Throws<ObjectDisposedException>(() => px.Release());
            Assert.Empty(Drain());
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }

    [Fact]
    public void DisposeRetiresOnceEvenWhenCalledTwice()
    {
        ImageRetirementQueue.Enabled = true;
        try
        {
            var px = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);
            long serial = px.Serial;
            px.Dispose();
            px.Dispose();
            Assert.Equal(new[] { serial }, Drain());
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }

    [Fact]
    public void NothingIsCollectedWhileDisabled()
    {
        ImageRetirementQueue.Enabled = false;
        using (ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgb8)) { }
        ImageRetirementQueue.Retire(12345);

        ImageRetirementQueue.Enabled = true;
        try
        {
            Assert.Empty(Drain());
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }

    [Fact]
    public void DisablingDropsEverythingQueued()
    {
        ImageRetirementQueue.Enabled = true;
        ImageRetirementQueue.Retire(1);
        ImageRetirementQueue.Retire(2);
        ImageRetirementQueue.Enabled = false;

        ImageRetirementQueue.Enabled = true;
        try
        {
            Assert.Empty(Drain());
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }

    [Fact]
    public void QueueStaysBoundedWhenNobodyDrainsAndRecoversAfterDraining()
    {
        ImageRetirementQueue.Enabled = true;
        try
        {
            int attempts = ImageRetirementQueue.MaxPending + 500;
            for (int i = 1; i <= attempts; i++)
                ImageRetirementQueue.Retire(i);

            List<long> first = Drain();
            Assert.Equal(ImageRetirementQueue.MaxPending, first.Count);
            // The earliest retirements are the ones kept; the overflow is what is dropped.
            Assert.Equal(1, first[0]);
            Assert.Equal(ImageRetirementQueue.MaxPending, first[^1]);

            // Draining frees the budget again.
            ImageRetirementQueue.Retire(999_999);
            Assert.Equal(new long[] { 999_999 }, Drain());
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }

    [Fact]
    public void ConcurrentRetirementsLoseNothingBelowTheBound()
    {
        ImageRetirementQueue.Enabled = true;
        try
        {
            const int perThread = 2000;
            Parallel.For(0, 4, t =>
            {
                for (int i = 0; i < perThread; i++)
                    ImageRetirementQueue.Retire(t * 1_000_000L + i);
            });

            List<long> drained = Drain();
            Assert.Equal(4 * perThread, drained.Count);
            Assert.Equal(4 * perThread, new HashSet<long>(drained).Count);
        }
        finally
        {
            ImageRetirementQueue.Enabled = false;
        }
    }
}
