using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary><c>a=q</c>: runs the whole load path, answers once, never stores and never touches an existing image.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyQueryTests
{
    private const string OnePixel = "AAAA";
    private const string Da1 = "\u001b[?1;0c";

    [Fact]
    public void TheDocumentedSupportProbeIsAnsweredOk()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,s=1,v=1,a=q,t=d,f=24", OnePixel);
        Assert.Equal(new[] { "\u001b_Gi=31;OK\u001b\\" }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void TheProbeReplyIsQueuedBeforeAFollowingDa1Reply()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        // printf '\e_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\e\\\e[c'
        parser.Feed(Encoding.ASCII.GetBytes("\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\\u001b[c"));

        Assert.Equal(new[] { "\u001b_Gi=31;OK\u001b\\", Da1 }, replies);
    }

    [Fact]
    public void RepliesStayInStreamOrderAroundOtherQueries()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.ASCII.GetBytes(
            "\u001b[c\u001b_Gi=1,s=1,v=1,a=q,f=24;AAAA\u001b\\\u001b[c\u001b_Gi=2,s=1,v=1,a=q,f=7;AAAA\u001b\\\u001b[c"));

        Assert.Equal(new[]
        {
            Da1,
            "\u001b_Gi=1;OK\u001b\\",
            Da1,
            "\u001b_Gi=2;EINVAL:Unknown image format\u001b\\",
            Da1,
        }, replies);
    }

    [Fact]
    public void WhenGraphicsAreDisabledTheProbeIsSilentSoClientsDetectNoSupport()
    {
        var replies = new List<string>();
        using var adapter = new TerminalAdapter(rows: 2, columns: 20);
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.ASCII.GetBytes("\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\\u001b[c"));

        Assert.Equal(new[] { Da1 }, replies);
    }

    [Fact]
    public void AQueryNeedsAnImageIdAndIsIgnoredWithoutOne()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("s=1,v=1,a=q,f=24", OnePixel);
            rig.Apc("I=5,s=1,v=1,a=q,f=24", OnePixel);         // an image number does not count
            rig.Apc("s=1,v=1,a=q,f=7", OnePixel);              // errors are not reported either
            rig.Apc("p=3,s=1,v=1,a=q,f=24", OnePixel);
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, rig.Decoder.Calls);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ABothIdsQueryIsAnError()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,I=5,s=1,v=1,a=q,f=24", OnePixel);
        Assert.Equal(new[] { Err("i=31,I=5", "EINVAL:Cannot specify both image id and image number") }, rig.Replies);
    }

    [Theory]
    [InlineData("i=31,s=1,v=1,a=q,f=7", "AAAA", "EINVAL:Unknown image format")]
    [InlineData("i=31,s=1,v=1,a=q,f=24,t=f", "AAAA", "EINVAL:Unsupported transmission type")]
    [InlineData("i=31,s=0,v=1,a=q,f=24", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=31,s=10001,v=1,a=q,f=24", "AAAA", "EFBIG:Image too large")]
    [InlineData("i=31,s=10000,v=10000,a=q,f=32", "AAAA", "ENOSPC:Storage quota exceeded")]
    [InlineData("i=31,s=2,v=1,a=q,f=24", "AAAA", "ENODATA:Insufficient image data")]
    [InlineData("i=31,s=1,v=1,a=q,f=24", "AAAAAAAA", "EFBIG:Payload too large")]
    [InlineData("i=31,s=1,v=1,a=q,f=24", "AA!A", "EINVAL:Invalid base64 data")]
    [InlineData("i=31,s=1,v=1,a=q,f=24,o=z", "////", "EILSEQ:Failed to inflate image data")]
    public void QueriesReportTheSameErrorsAsATransmission(string control, string payload, string message)
    {
        using var rig = new KittyRig();
        rig.Apc(control, payload);
        Assert.Equal(new[] { Err("i=31", message) }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Theory]
    [InlineData(0, "ok", true)]
    [InlineData(1, "ok", false)]
    [InlineData(2, "ok", false)]
    [InlineData(0, "error", true)]
    [InlineData(1, "error", true)]
    [InlineData(2, "error", false)]
    public void QuietLevelsApplyToQueries(int quiet, string kind, bool expectReply)
    {
        using var rig = new KittyRig();
        string format = kind == "ok" ? "24" : "7";
        rig.Apc($"i=31,s=1,v=1,a=q,f={format},q={quiet}", OnePixel);
        Assert.Equal(expectReply ? 1 : 0, rig.Replies.Count);
    }

    [Fact]
    public void AQueryNeverStoresAndReleasesEverythingItAllocated()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            for (int i = 0; i < 40; i++)
                rig.Apc($"i={i + 1},s=64,v=64,a=q,f=32", B64(new byte[64 * 64 * 4]));
            Assert.Equal(40, rig.Replies.Count);
            Assert.All(rig.Replies, r => Assert.EndsWith(";OK\u001b\\", r));
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, rig.Store.UsedBytes);
            Assert.True(baseline.AllocatedDelta >= 40 * 64 * 64 * 4); // the full decode path really ran
            baseline.AssertRestored();
        }
    }

    [Fact]
    public void AQueryDoesNotClobberAnExistingImageWithTheSameId_WhetherItSucceedsOrFails()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,s=1,v=1,f=24", B64(1, 2, 3));
        var original = rig.Store.FindByClientId(31)!;
        long serial = original.Root.Serial;
        rig.Replies.Clear();

        rig.Apc("i=31,s=1,v=1,a=q,f=24", B64(9, 9, 9));       // succeeds
        rig.Apc("i=31,s=1,v=1,a=q,f=7", B64(9, 9, 9));        // fails validation
        rig.Apc("i=31,s=2,v=1,a=q,f=24", B64(9, 9, 9));       // fails with too little data
        rig.Apc("i=31,s=1,v=1,a=q,f=24,m=1", B64(9, 9, 9));   // chunk...
        rig.Apc("a=d");                                       // ...abandoned

        Assert.Equal(new[]
        {
            Ok("i=31"),
            Err("i=31", "EINVAL:Unknown image format"),
            Err("i=31", "ENODATA:Insufficient image data"),
        }, rig.Replies);
        Assert.Same(original, rig.Store.FindByClientId(31));
        Assert.False(original.IsDeleted);
        Assert.Equal(serial, rig.Store.FindByClientId(31)!.Root.Serial);
        Assert.Equal(new byte[] { 1, 2, 3 }, original.Root.AsSpan().ToArray());
        Assert.Equal(1, rig.Store.Count);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void ChunkedQueriesAccumulateAndAnswerOnce()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,s=3,v=1,a=q,f=24,m=1", OnePixel);
        rig.Apc("m=1", OnePixel);
        Assert.Empty(rig.Replies);
        rig.Apc("m=0", OnePixel);
        Assert.Equal(new[] { Ok("i=31") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void AChunkedQueryThatFailsMidwayIsAnsweredOnce()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,s=3,v=1,a=q,f=24,m=1", OnePixel);
        rig.Apc("m=1", "A!AA");
        rig.Apc("m=0", OnePixel);
        Assert.Equal(new[] { Err("i=31", "EINVAL:Invalid base64 data") }, rig.Replies);
    }

    [Fact]
    public void AQueryOfAPngRunsTheDecoderAndStoresNothing()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=31,a=q,f=100", B64(TestPng.Build(2, 1, new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 })));
            rig.Apc("i=32,a=q,f=100", B64(RandomBytes(80, 3)));
            Assert.Equal(new[] { Ok("i=31"), Err("i=32", "EBADPNG:Invalid PNG data") }, rig.Replies);
            Assert.Equal(1, rig.Decoder.Calls);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AQueryOfAPngWithoutADecoderReportsAnUnknownFormat()
    {
        using var rig = new KittyRig(withDecoder: false);
        rig.Apc("i=31,a=q,f=100", B64(TestPng.Build(1, 1, new byte[4])));
        rig.Apc("i=32,s=1,v=1,a=q,f=24", OnePixel);
        Assert.Equal(new[] { Err("i=31", "EINVAL:Unknown image format"), Ok("i=32") }, rig.Replies);
    }

    [Fact]
    public void ACompressedQueryIsInflatedAndChecked()
    {
        using var rig = new KittyRig();
        rig.Apc("i=31,s=2,v=2,a=q,f=32,o=z", B64(TestPng.Zlib(new byte[16])));
        rig.Apc("i=32,s=2,v=2,a=q,f=32,o=z", B64(TestPng.Zlib(new byte[15])));
        Assert.Equal(new[]
        {
            Ok("i=31"),
            Err("i=32", "EINVAL:Image data size does not match declared size"),
        }, rig.Replies);
    }

    [Fact]
    public void ALargeQueryDecodesWithTheBufferLockReleasedAndStillStoresNothing()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            lock (rig.Host.SyncRoot)
                rig.Apc("i=31,a=q,f=100", B64(TestPng.Build(256, 256, new byte[256 * 256 * 4])));
            Assert.Equal(new[] { Ok("i=31") }, rig.Replies);
            Assert.Equal(1, rig.Host.ReleaseAttempts);
            Assert.Equal(1, rig.Host.ReacquireCalls);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AQueryAbortsAnOpenLoadLikeAnyOtherCommand()
    {
        using var rig = new KittyRig();
        rig.Apc("i=4,s=2,v=1,f=24,m=1", OnePixel);
        rig.Apc("i=31,s=1,v=1,a=q,f=24", OnePixel);
        rig.Apc("m=0", OnePixel);
        Assert.Equal(new[] { Ok("i=31") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }
}
