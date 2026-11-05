using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// The animation commands end to end through the parser, the adapter and the controller: reply ordering against
/// later terminal replies, chunked frame loads interleaved with other sequences, runtime configuration changes,
/// tear-down while frames are being decoded or displayed, and the resource counters on all of those paths.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyAnimationFlowTests
{
    private const string Da1Reply = "\u001b[?1;0c";
    private const string OnePixel = "AAAA";        // 3 zero bytes: one f=24 pixel
    private const string TwoPixels = "AAAAAAAA";   // 6 zero bytes: a 2x1 f=24 image

    private static string LargePng => B64(TestPng.Build(256, 256, new byte[256 * 256 * 4]));

    /// <summary>A rig holding image 1, 256x256 (262144 bytes, the size from which decoding runs without the lock), replies cleared.</summary>
    private static KittyRig LargeImageRig()
    {
        var rig = new KittyRig();
        rig.Apc("i=1,s=256,v=256,f=32", B64(new byte[256 * 256 * 4]));
        rig.Replies.Clear();
        return rig;
    }

    /// <summary>
    /// Loads a 256x256 PNG frame into image 1 of <see cref="LargeImageRig"/>; <paramref name="duringDecode"/> runs inside
    /// the decoder, where the buffer lock is released on the real host, and <paramref name="after"/> checks the outcome.
    /// The resource counters must be back at their baseline once the rig is gone.
    /// </summary>
    private static void FrameDecodeWith(Action<KittyRig> duringDecode, Action<KittyRig> after)
    {
        var baseline = new GraphicsBaseline();
        var rig = LargeImageRig();
        try
        {
            rig.Decoder.BeforeDecode = (_, _) => duringDecode(rig);
            rig.Apc("a=f,i=1,f=100", LargePng);
            after(rig);
        }
        finally
        {
            rig.Dispose();
        }
        baseline.AssertRestored();
    }

    // ---------------------------------------------------------------------------------- reply ordering

    [Fact]
    public void RepliesOfEveryAnimationCommandArriveBeforeAFollowingDa1Reply()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.ASCII.GetBytes(
            "\u001b_Ga=T,i=1,s=1,v=1,f=24;AAAA\u001b\\" +
            "\u001b_Ga=f,i=1,s=1,v=1,f=24;AAAA\u001b\\" +
            "\u001b_Ga=a,i=1,s=3\u001b\\" +
            "\u001b_Ga=c,i=1,r=1,c=2\u001b\\" +
            "\u001b[c"));

        Assert.Equal(new[] { Ok("i=1"), Ok("i=1"), Ok("i=1"), Ok("i=1"), Da1Reply }, replies);
        var image = adapter.KittyGraphics!.Store.FindByClientId(1)!;
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(KittyAnimationState.Running, image.AnimationState);
    }

    [Fact]
    public void ARejectedAnimationCommandStillAnswersBeforeTheNextDa1Reply()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.ASCII.GetBytes(
            "\u001b_Ga=T,i=1,s=1,v=1,f=24;AAAA\u001b\\" +
            "\u001b_Ga=c,i=1,r=1,c=9\u001b\\" +
            "\u001b_Ga=a,i=7,s=3\u001b\\" +
            "\u001b[c"));

        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=1", "ENOENT:Frame not found"),
            Err("i=7", "ENOENT:Image not found"),
            Da1Reply,
        }, replies);
    }

    [Fact]
    public void AChunkedFrameLoadSurvivesAnInterleavedDa1AndAnswersAfterIt()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(ApcBytes("a=T,i=1,s=2,v=1,f=24", TwoPixels));
        parser.Feed(ApcBytes("a=f,i=1,s=2,v=1,f=24,m=1", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("\u001b[c"));
        Assert.True(adapter.KittyGraphics!.HasOpenTransfer);
        parser.Feed(ApcBytes("m=0", OnePixel));

        Assert.Equal(new[] { Ok("i=1"), Da1Reply, Ok("i=1") }, replies);
        Assert.Equal(2, adapter.KittyGraphics.Store.FindByClientId(1)!.Frames.Count);
        Assert.False(adapter.KittyGraphics.HasOpenTransfer);
    }

    [Fact]
    public void ASwitchToTheAlternateScreenEndsAnOpenFrameLoadAndLeavesTheMainImageAlone()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("a=T,i=1,s=2,v=1,f=24", TwoPixels));
        parser.Feed(ApcBytes("a=f,i=1,s=2,v=1,f=24,m=1", OnePixel));
        Assert.True(adapter.KittyGraphics!.HasOpenTransfer);

        parser.Feed(Encoding.ASCII.GetBytes("\u001b[?1049h"));
        Assert.False(adapter.KittyGraphics.HasOpenTransfer);
        parser.Feed(ApcBytes("m=0", OnePixel)); // the rest of the abandoned load: dropped without a reply
        parser.Feed(Encoding.ASCII.GetBytes("\u001b[?1049l"));

        Assert.Equal(new[] { Ok("i=1") }, replies);
        Assert.Single(adapter.Buffer.MainImageStore!.FindByClientId(1)!.Frames);
    }

    [Fact]
    public void ErrorRepliesToANumberAddressedFrameLoadCarryTheImageIdAndTheNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=7,s=1,v=1,f=32", B64(AnimKit.Red));   // gets the first allocated client id, 2147483648
        rig.Replies.Clear();

        rig.Apc("a=f,I=7,r=9,s=1,v=1,f=32", B64(AnimKit.Red));       // frame 9 does not exist: refused up front
        rig.Apc("a=f,I=7,s=1,v=1,f=7", B64(AnimKit.Red));            // unknown format: refused by the transfer
        rig.Apc("a=f,I=7,s=1,v=1,f=32,m=1", B64(1, 2, 3));           // a chunked load whose last chunk is not base64
        rig.Apc("m=0", "!!!!");

        Assert.Equal(new[]
        {
            Err("i=2147483648,I=7", "ENOENT:Frame not found"),
            Err("i=2147483648,I=7", "EINVAL:Unknown image format"),
            Err("i=2147483648,I=7", "EINVAL:Invalid base64 data"),
        }, rig.Replies);
        Assert.Single(rig.Store.FindByNumber(7)!.Frames);
    }

    [Theory]
    [InlineData("s=3,v=1")]            // wider than the 2x2 image
    [InlineData("s=2,v=3")]            // taller
    [InlineData("s=2,v=1,x=1")]        // fits only at x=0
    [InlineData("s=2,v=1,y=2")]        // starts below the image
    public void ARawFrameThatCannotFitIsRefusedByItsFirstChunkAndTheRestIsDropped(string size)
    {
        var baseline = new GraphicsBaseline();
        using (var rig = AnimKit.RedRig())
        {
            rig.Apc($"a=f,i=1,{size},f=24,m=1", B64(1, 2, 3));

            Assert.Equal(new[] { Err("i=1", "EINVAL:Rectangle out of bounds") }, rig.Replies);
            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, baseline.PendingDelta);          // nothing was staged for it

            rig.Apc("m=0", B64(4, 5, 6));                      // the rest of the refused load
            Assert.Single(rig.Replies);
            Assert.Single(AnimKit.Img(rig, 1).Frames);
            rig.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    // ----------------------------------------------------------------- runtime enable / disable

    [Fact]
    public void DisablingWithAnAnimatedImageAndAnOpenFrameLoadDropsEverythingAndLaterFramesAreRejected()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Store.TicksPerSecond = 1000;
            AnimKit.Transmit(rig, 1, 2, 1, AnimKit.Cat(AnimKit.Red, AnimKit.Red));
            AnimKit.LoadFrame(rig, 1, 2, 1, AnimKit.Cat(AnimKit.Red, AnimKit.Red));
            rig.Apc("a=a,i=1,r=1,z=10,s=3");
            rig.Store.Place(AnimKit.Img(rig, 1), "p=1", 0);
            Assert.NotEqual(0, rig.Store.AdvanceAnimations(5000, 24, 0)); // playing: a deadline is armed
            rig.Apc("a=f,i=1,s=2,v=1,f=24,m=1", B64(1, 2, 3));            // half of a frame in flight
            Assert.True(rig.Controller.HasOpenTransfer);
            Assert.True(baseline.PendingDelta > 0);
            rig.Replies.Clear();

            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = false }, null);

            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, rig.Store.UsedBytes);
            Assert.Equal(0, rig.Store.PlacementCount);
            Assert.Equal(0, baseline.PendingDelta);
            Assert.Equal(0, rig.Store.AdvanceAnimations(9000, 24, 0));

            rig.Apc("m=0", B64(4, 5, 6));       // the stray rest of the load
            rig.Apc("a=a,i=1,s=3");             // and a command: both vanish silently while disabled
            Assert.Empty(rig.Replies);

            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true }, rig.Decoder);
            AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Red);
            Assert.Equal(new[] { Err("i=1", "ENOENT:Image not found") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void TogglingTheProtocolThroughTheAdapterDropsTheAnimationAndTheOpenLoadAndTheParserSwallowsTheRest()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("a=T,i=1,s=2,v=1,f=24", TwoPixels));
        parser.Feed(ApcBytes("a=f,i=1,s=2,v=1,f=24,m=1", OnePixel));
        Assert.True(adapter.KittyGraphics!.HasOpenTransfer);

        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = false }, null);
        Assert.False(adapter.KittyGraphics.HasOpenTransfer);
        Assert.Equal(0, adapter.Buffer.MainImageStore!.Count);
        replies.Clear();

        parser.Feed(ApcBytes("m=0", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("ok"));
        Assert.Empty(replies);
        Assert.True(Enumerable.Range(0, 4).Any(row => adapter.Buffer.GetRowText(row).Contains("ok")), "the text after the dropped APC is printed");

        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
        parser.Feed(ApcBytes("a=f,i=1,s=1,v=1,f=24", OnePixel));
        Assert.Equal(new[] { Err("i=1", "ENOENT:Image not found") }, replies);
    }

    // -------------------------------------------------------- tear-down while a frame is being decoded

    [Fact]
    public void DisablingDuringAFrameDecodeDropsTheFrameWithoutAReply() =>
        FrameDecodeWith(
            rig => rig.Controller.Configure(new KittyGraphicsOptions { Enabled = false }, null),
            rig =>
            {
                Assert.Empty(rig.Replies);
                Assert.False(rig.Controller.Enabled);
                Assert.Equal(0, rig.Store.Count);
                Assert.False(rig.Controller.HasOpenTransfer);
            });

    [Fact]
    public void ClearingImagesDuringAFrameDecodeDropsTheFrameWithoutAReply() =>
        FrameDecodeWith(
            rig => rig.Controller.ClearImages(),
            rig =>
            {
                Assert.Empty(rig.Replies);
                Assert.Equal(0, rig.Store.Count);
                Assert.True(rig.Controller.Enabled);
            });

    [Fact]
    public void DisposingTheControllerDuringAFrameDecodeReleasesEverything() =>
        FrameDecodeWith(
            rig => rig.Controller.Dispose(),
            rig => Assert.Empty(rig.Replies));

    [Fact]
    public void ADisposedHostDuringAFrameDecodeDropsTheFrame() =>
        FrameDecodeWith(
            rig => rig.Host.Disposed = true,
            rig =>
            {
                Assert.Empty(rig.Replies);
                Assert.Single(rig.Store.FindByClientId(1)!.Frames);
            });

    [Fact]
    public void AnImageDeletedDuringAFrameDecodeAnswersImageNotFound() =>
        FrameDecodeWith(
            rig => rig.Store.DeleteByClientId(1),
            rig =>
            {
                Assert.Equal(new[] { Err("i=1", "ENOENT:Image not found") }, rig.Replies);
                Assert.Equal(0, rig.Store.Count);
            });

    [Fact]
    public void AnImageReplacedByASmallerOneDuringAFrameDecodeRejectsTheFrameAsOutOfBounds() =>
        FrameDecodeWith(
            rig =>
            {
                rig.Store.DeleteByClientId(1);
                rig.Store.AddImage(2, 2, clientId: 1);
            },
            rig =>
            {
                Assert.Equal(new[] { Err("i=1", "EINVAL:Rectangle out of bounds") }, rig.Replies);
                Assert.Single(rig.Store.FindByClientId(1)!.Frames);
                rig.Store.ValidateInvariants();
            });

    [Fact]
    public void AQuotaLoweredDuringAFrameDecodeRejectsTheFrameAndKeepsTheImage() =>
        FrameDecodeWith(
            rig => rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 300_000 }, rig.Decoder),
            rig =>
            {
                Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
                var image = rig.Store.FindByClientId(1)!;
                Assert.Single(image.Frames);
                Assert.Equal(256 * 256 * 4, rig.Store.UsedBytes);
                rig.Store.ValidateInvariants();
            });

    // ------------------------------------------------------------------ tear-down while displayed

    private sealed class ManualClock : TimeProvider
    {
        private long _now = 1_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref _now);
    }

    [Fact]
    public void DisposingTheTerminalWhileAnAnimationRunsKeepsOnlyTheFrameASnapshotStillShowsUntilItIsReleased()
    {
        const int FrameBytes = 10 * 20 * 4;
        string pixels(int fill) => Convert.ToBase64String(Enumerable.Repeat((byte)fill, FrameBytes).ToArray());

        var baseline = new GraphicsBaseline();
        var adapter = new TerminalAdapter(6, 10, 100, new ManualClock());
        adapter.SetCellPixelSize(10, 20);
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("a=T,i=1,s=10,v=20,f=32,c=1,r=1,C=1", pixels(10)));
        parser.Feed(ApcBytes("a=f,i=1,s=10,v=20,f=32,z=2", pixels(20)));
        parser.Feed(ApcBytes("a=a,i=1,s=3,r=1,z=2"));

        var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible(0, -1, 0);
        Assert.Single(snapshot.Graphics!.Placements.ToArray());
        Assert.Equal(2 * FrameBytes, baseline.LiveBytesDelta);

        adapter.Dispose();

        Assert.Equal(FrameBytes, baseline.LiveBytesDelta);   // the frame the snapshot leases survives, the other is freed
        Assert.Equal(1, baseline.LiveCountDelta);
        snapshot.Dispose();
        baseline.AssertRestored();
    }

    // ----------------------------------------------------------------------------- tick arithmetic

    [Fact]
    public void DeadlinesStayExactAtTheLargestGapAndHugeTimestamps()
    {
        using var rig = new KittyRig();
        rig.Store.TicksPerSecond = 1_000_000_000;
        AnimKit.Transmit(rig, 1, 1, 1, AnimKit.Red);
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Red, $",z={int.MaxValue}");
        rig.Apc($"a=a,i=1,r=1,z={int.MaxValue}");
        var image = AnimKit.Img(rig, 1);
        rig.Store.Place(image, "p=1", 0);
        rig.Apc("a=a,i=1,s=3");

        const long gapTicks = 2_147_483_647_000_000L; // int.MaxValue ms at 1e9 ticks per second
        long now = 1L << 60;
        Assert.Equal(now + gapTicks, rig.Store.AdvanceAnimations(now, 24, 0));
        Assert.Equal(now + gapTicks, rig.Store.AdvanceAnimations(now + gapTicks - 1, 24, 0));
        Assert.Equal(0, image.CurrentFrameIndex);

        long due = now + gapTicks;
        Assert.Equal(due + gapTicks, rig.Store.AdvanceAnimations(due, 24, 0));
        Assert.Equal(1, image.CurrentFrameIndex);
        rig.Store.ValidateInvariants();
    }
}
