using System;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Allocation guards for the Kitty graphics hot paths, driven through <see cref="BasicAnsiParser"/> and
/// <see cref="TerminalAdapter"/> with the protocol enabled. The test class shares the allocation-sensitive collection
/// (no parallel tests perturb <c>GC.GetAllocatedBytesForCurrentThread</c>) and restores the process-wide
/// <see cref="GraphicsMemoryMetrics"/> counters by disposing every adapter it creates.
///
/// Documented exceptions to "0 bytes": a query (<c>a=q</c>) with a payload starts a transfer object, so its round trip
/// is bounded (<see cref="QueryRoundTripBoundBytes"/>), not zero. The bound is a deliberately loose tripwire, not a
/// measured figure: it only catches a regression that makes the query path copy payloads or build strings.
/// </summary>
[Collection("Allocation-sensitive tests")]
public sealed class KittyAllocationTests
{
    private const int QueryRoundTripBoundBytes = 4096;
    private const int PlacementCount = 1000;

    /// <summary>A 1x1 RGBA pixel: four zero bytes in base64.</summary>
    private const string OnePixel = "AAAAAA==";

    private static readonly TerminalReplyHandler ReplySink = OnReply;
    private static readonly StringBuilder Captured = new();
    private static bool _capture;
    private static int _replyCount;

    private static void OnReply(ReadOnlySpan<char> reply)
    {
        _replyCount++;
        if (_capture)
            Captured.Append(reply);
    }

    private static TerminalAdapter CreateAdapter(out BasicAnsiParser parser, bool withReplySink = true)
    {
        var adapter = new TerminalAdapter(rows: 24, columns: 80, scrollbackCapacity: 100);
        if (withReplySink)
            adapter.ReplyRequested += ReplySink;
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
        parser = new BasicAnsiParser { Handler = adapter };
        return adapter;
    }

    private static string CapturedAfter(Action action)
    {
        Captured.Clear();
        _capture = true;
        try { action(); }
        finally { _capture = false; }
        return Captured.ToString();
    }

    // ---------------------------------------------------------------------------------------------- APC parsing

    [Fact]
    public void SteadyStateSmallCommandStreamDoesNotAllocate()
    {
        var baseline = new GraphicsBaseline();
        var adapter = CreateAdapter(out var parser);
        try
        {
            // Commands that complete without creating an image, placement or transfer: a put/delete of an unknown
            // image (the put answers ENOENT through the stackalloc'd reply path), a stray continuation chunk with and
            // without payload (dropped silently), and a non-graphics APC (discarded).
            byte[] stream = Encoding.ASCII.GetBytes(
                Esc + "_Ga=p,i=9999" + St +
                Esc + "_Ga=p,i=9999,q=2" + St +
                Esc + "_Ga=d,d=i,i=9999" + St +
                Esc + "_Gm=1;AAAA" + St +
                Esc + "_Gm=0" + St +
                Esc + "_Xnot graphics" + St);

            // The reply bytes are part of the contract the zero-allocation claim covers.
            Assert.Equal(
                Err("i=9999", "ENOENT:Image not found"),
                CapturedAfter(() => parser.Feed(stream)));

            int repliesBefore = _replyCount;
            AllocationAssert.NoAllocations(
                () => parser.Feed(stream),
                warmupIterations: 16,
                measuredIterationsPerWindow: 64,
                windows: 5);
            Assert.True(_replyCount > repliesBefore, "the measured commands must reach the reply path");
        }
        finally
        {
            adapter.Dispose();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ChunkedPayloadFedInSlicesIsSteadyStateCheapAfterWarmUp()
    {
        // A stray (no open transfer) payload of 64 KiB is handed to the controller slice by slice and dropped: the
        // parser's bulk payload path must not copy or buffer it.
        var baseline = new GraphicsBaseline();
        var adapter = CreateAdapter(out var parser);
        try
        {
            var sb = new StringBuilder(Esc + "_Gm=1;");
            sb.Append('A', 64 * 1024).Append(St);
            byte[] stream = Encoding.ASCII.GetBytes(sb.ToString());
            AllocationAssert.NoAllocations(
                () => parser.Feed(stream),
                warmupIterations: 8,
                measuredIterationsPerWindow: 16,
                windows: 5);
        }
        finally
        {
            adapter.Dispose();
        }
        baseline.AssertRestored();
    }

    // ---------------------------------------------------------------------------------------------- query

    [Fact]
    public void QueryRoundTripAnswersAndStaysWithinTheAllocationBound()
    {
        var baseline = new GraphicsBaseline();
        var adapter = CreateAdapter(out var parser);
        try
        {
            byte[] query = Encoding.ASCII.GetBytes(Esc + "_Ga=q,i=31,s=1,v=1,f=32;" + OnePixel + St);

            Assert.Equal(Ok("i=31"), CapturedAfter(() => parser.Feed(query)));
            Assert.Equal(0, adapter.Buffer.ActiveImageStore.Count); // a query never stores an image

            for (int i = 0; i < 32; i++)
                parser.Feed(query);

            long minWindowBytes = long.MaxValue;
            const int iterations = 32;
            for (int window = 0; window < 8; window++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iterations; i++)
                    parser.Feed(query);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes < minWindowBytes)
                    minWindowBytes = bytes;
            }

            // Allocates (the transfer object and its decoder state) but only O(1) small objects per query; the
            // reply itself is formatted into a stack buffer and handed to the sink as a span.
            Assert.True(
                minWindowBytes <= (long)QueryRoundTripBoundBytes * iterations,
                $"query round trip allocated {minWindowBytes / iterations} B/op, bound {QueryRoundTripBoundBytes}");
        }
        finally
        {
            adapter.Dispose();
        }
        baseline.AssertRestored();
    }

    // ---------------------------------------------------------------------------------------------- snapshot

    private static TerminalAdapter CreateAdapterWithPlacements(out BasicAnsiParser parser)
    {
        var adapter = CreateAdapter(out parser);
        var sb = new StringBuilder();
        sb.Append(Esc).Append("_Ga=T,f=32,s=1,v=1,i=1,C=1,q=2;").Append(OnePixel).Append(St);
        for (int p = 1; p <= PlacementCount; p++)
            sb.Append(Esc).Append("_Ga=p,i=1,p=").Append(p).Append(",C=1,q=2").Append(St);
        parser.Feed(Encoding.ASCII.GetBytes(sb.ToString()));
        Assert.Equal(PlacementCount + 1, adapter.Buffer.ActiveImageStore.PlacementCount); // a=T places once too
        return adapter;
    }

    [Fact]
    public void BufferSnapshotCaptureWithPlacementsDoesNotAllocateAfterWarmUp()
    {
        var baseline = new GraphicsBaseline();
        var adapter = CreateAdapterWithPlacements(out _);
        try
        {
            var buffer = adapter.Buffer;
            using (var probe = buffer.CaptureRenderSnapshotVisible())
            {
                Assert.NotNull(probe.Graphics);
                Assert.Equal(PlacementCount + 1, probe.Graphics!.Placements.Length);
            }

            // The pooled RenderSnapshot and its pooled KittyRenderSnapshot keep their storage across Dispose, so the
            // steady-state capture (cell copy + placement capture + lease retain/release) allocates nothing.
            AllocationAssert.NoAllocations(
                () =>
                {
                    using var snapshot = buffer.CaptureRenderSnapshotVisible();
                },
                warmupIterations: 16,
                measuredIterationsPerWindow: 32,
                windows: 5);
        }
        finally
        {
            adapter.Dispose();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void StoreCaptureIntoReusedSnapshotDoesNotAllocateAfterWarmUp()
    {
        var baseline = new GraphicsBaseline();
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = PlacementCount + 8 };
        var image = store.AddImage(8, 8, 1);
        for (int p = 1; p <= PlacementCount; p++)
            store.Place(image, "c=1,r=1,p=" + p, anchorRow: p % 20);

        var snapshot = new KittyRenderSnapshot();
        try
        {
            Assert.Equal(PlacementCount, store.CaptureVisible(snapshot, 24, 0));
            snapshot.Reset();

            AllocationAssert.NoAllocations(
                () =>
                {
                    store.CaptureVisible(snapshot, 24, 0);
                    snapshot.Reset();
                },
                warmupIterations: 8,
                measuredIterationsPerWindow: 32,
                windows: 5);
        }
        finally
        {
            snapshot.Dispose();
        }
        store.Clear();
        baseline.AssertRestored();
    }
}
