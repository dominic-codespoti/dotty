using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>Command routing, id handling, quota effects, lifecycle and fault containment of <see cref="KittyGraphicsController"/>.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyControllerTests
{
    private const string OnePixel = "AAAA"; // 3 zero bytes: a 1x1 f=24 image
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static string ThousandBytes => B64(new byte[1000]);

    // ------------------------------------------------------------------------------------------------ routing

    [Fact]
    public void TransmitAndPutStoresTheImageAndAnswersOk()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=8,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=8") }, rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(8));
    }

    [Theory]
    [InlineData("a=p,i=1,p=2")]
    [InlineData("a=d,d=i,i=1")]
    [InlineData("a=d")]
    [InlineData("a=f,i=1,s=1,v=1")]
    [InlineData("a=a,i=1,s=3")]
    [InlineData("a=c,i=1,r=1,c=2")]
    public void OtherActionsAreParsedAndIgnoredForNow(string control)
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Replies.Clear();

        rig.Apc(control, "AAAAAAAA"); // frame loads carry a payload: it is dropped
        Assert.Empty(rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(1)); // not even delete does anything yet
        Assert.Equal(1, rig.Store.Count);
    }

    [Fact]
    public void UnknownActionIsAnErrorOnlyWhenAnIdIsKnown()
    {
        using var rig = new KittyRig();
        rig.Apc("a=z,i=3");
        rig.Apc("a=z");
        rig.Apc("a=z,i=3,q=2");
        rig.Apc("a=z,I=4,p=2");
        Assert.Equal(new[]
        {
            Err("i=3", "EINVAL:Unknown action"),
            Err("I=4,p=2", "EINVAL:Unknown action"),
        }, rig.Replies);
    }

    [Fact]
    public void ACommandWithAnUnusableKeyValueIsRejectedAndLeavesStoredImagesAlone()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=3,s=1,v=1,f=24", OnePixel);
            rig.Replies.Clear();

            rig.Apc("i=3,s=99999999999,v=1", OnePixel);
            rig.Apc("s=99999999999");                      // no id: silent
            Assert.Equal(new[] { Err("i=3", "EINVAL:Invalid key value") }, rig.Replies);
            Assert.NotNull(rig.Store.FindByClientId(3));
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AnUnparseableCommandAbortsTheOpenLoad()
    {
        using var rig = new KittyRig();
        rig.Apc("i=4,s=2,v=1,f=24,m=1", OnePixel);
        rig.Apc("i=4,s=99999999999");
        rig.Apc("m=0", OnePixel);
        Assert.Equal(new[] { Err("i=4", "EINVAL:Invalid key value") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void StrayContinuationChunksAreDroppedSilently()
    {
        using var rig = new KittyRig();
        rig.Apc("m=1", OnePixel);
        rig.Apc("m=0,q=0", OnePixel);
        rig.Apc("", OnePixel);
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void ACommandWithKeysBesidesMAndQIsANewTransmissionNotAContinuation()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24,m=1", OnePixel);
        // "a=t" makes it a new command: it aborts the open load and, having no dimensions and no id, fails silently.
        rig.Apc("a=t,m=0", OnePixel);
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    // ------------------------------------------------------------------------------------------ ids and hints

    [Fact]
    public void ImageNumberAllocatesAFreshUpperRangeIdForEveryTransmission()
    {
        using var rig = new KittyRig();
        rig.Apc("I=7,s=1,v=1,f=24", OnePixel);
        rig.Apc("I=7,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=2147483648,I=7"), Ok("i=2147483649,I=7") }, rig.Replies);
        Assert.Equal(2, rig.Store.Count);
        Assert.Equal(2147483649u, rig.Store.FindByNumber(7)!.ClientId); // the newest one
    }

    [Fact]
    public void AllocatedIdsSkipIdsTheClientAlreadyUses()
    {
        using var rig = new KittyRig();
        rig.Apc("i=2147483648,s=1,v=1,f=24", OnePixel);
        rig.Apc("I=1,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=2147483648"), Ok("i=2147483649,I=1") }, rig.Replies);
    }

    [Fact]
    public void BothIdAndNumberIsRejectedBeforeAnythingHappens()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Replies.Clear();
        var existing = rig.Store.FindByClientId(1);

        rig.Apc("i=1,I=2,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Err("i=1,I=2", "EINVAL:Cannot specify both image id and image number") }, rig.Replies);
        Assert.Same(existing, rig.Store.FindByClientId(1)); // the old image is not touched by the rejected command
        Assert.Equal(1, rig.Store.Count);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]    // only bit 0 is the transient hint
    [InlineData(255u, true)]
    public void TheUsageHintMarksTransientImages(uint hint, bool expectedTransient)
    {
        using var rig = new KittyRig();
        rig.Apc($"i=1,s=1,v=1,f=24,N={hint}", OnePixel);
        Assert.Equal(expectedTransient, rig.Store.FindByClientId(1)!.Transient);
    }

    [Fact]
    public void AnonymousImagesAreDroppedRightAfterTheCommand()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("s=1,v=1,f=24", OnePixel);
            rig.Apc("a=T,s=1,v=1,f=24", OnePixel);
            rig.Apc("a=t,s=1,v=1,f=24,q=0", OnePixel);
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, rig.Store.UsedBytes);
            baseline.AssertRestored();
        }
    }

    [Fact]
    public void AnAnonymousPlainTransmitNeverEvictsImagesThatCanStillBeUsed()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1500 }))
        {
            rig.Apc("i=1,s=250,v=1,f=32", ThousandBytes);
            rig.Apc("s=250,v=1,f=32", ThousandBytes);      // nobody can address it again: it must not push i=1 out
            rig.Apc("s=250,v=1,f=32,q=2", ThousandBytes);

            Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
            Assert.NotNull(rig.Store.FindByClientId(1));
            Assert.Equal(1, rig.Store.Count);
            Assert.Equal(1000, baseline.LiveBytesDelta);
            rig.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------------------- retransmission

    [Fact]
    public void RetransmittingAnIdReplacesTheImageAndFreesTheOldPixels()
    {
        var baseline = new GraphicsBaseline();
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24", B64(1, 2, 3, 4, 5, 6));
        var old = rig.Store.FindByClientId(1)!;
        rig.Apc("i=1,s=1,v=1,f=24", B64(9, 9, 9));

        var now = rig.Store.FindByClientId(1)!;
        Assert.NotSame(old, now);
        Assert.True(old.IsDeleted);
        Assert.Equal(new byte[] { 9, 9, 9 }, now.Root.AsSpan().ToArray());
        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(3, baseline.LiveBytesDelta);
        Assert.Equal(new[] { Ok("i=1"), Ok("i=1") }, rig.Replies);
        rig.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData("i=1,s=1,v=1,f=7", "AAAA")]                  // fails validation
    [InlineData("i=1,s=2,v=1,f=24", "AAAA")]                 // ENODATA
    [InlineData("i=1,s=1,v=1,f=24", "AA!A")]                 // bad base64
    [InlineData("i=1,s=1,v=1,f=24,o=z", "AAAA")]             // bad zlib
    [InlineData("i=1,s=10000,v=10000,f=32", "AAAA")]         // over the quota
    public void AFailedRetransmissionLeavesNoStaleImage(string control, string payload)
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        Assert.NotNull(rig.Store.FindByClientId(1));

        rig.Apc(control, payload);
        Assert.Null(rig.Store.FindByClientId(1));
        Assert.Equal(0, rig.Store.Count);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void AnInterruptedRetransmissionLeavesNoStaleImageEither()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=1,s=2,v=1,f=24,m=1", OnePixel);   // retransmission starts: the old image is gone already
        Assert.Null(rig.Store.FindByClientId(1));
        rig.Apc("a=d,d=a");                           // ...and the load is abandoned
        rig.Apc("m=0", OnePixel);
        Assert.Equal(0, rig.Store.Count);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void ACancelledRetransmissionLeavesNoStaleImage()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.CancelledApc("i=1,s=1,v=1,f=24", OnePixel);
        Assert.Null(rig.Store.FindByClientId(1));
    }

    // ------------------------------------------------------------------------------------------------ quota

    [Fact]
    public void TransmissionsEvictTheOldestImagesToStayWithinTheQuota()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 2500 });
        for (int id = 1; id <= 3; id++)
            rig.Apc($"i={id},s=250,v=1,f=32", ThousandBytes);

        Assert.Equal(new[] { Ok("i=1"), Ok("i=2"), Ok("i=3") }, rig.Replies);
        Assert.Null(rig.Store.FindByClientId(1));
        Assert.NotNull(rig.Store.FindByClientId(2));
        Assert.NotNull(rig.Store.FindByClientId(3));
        Assert.Equal(2000, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void TransientImagesAreEvictedBeforeOlderOnes()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 2500 });
        rig.Apc("i=1,s=250,v=1,f=32", ThousandBytes);
        rig.Apc("i=2,s=250,v=1,f=32,N=1", ThousandBytes);
        rig.Apc("i=3,s=250,v=1,f=32", ThousandBytes);

        Assert.NotNull(rig.Store.FindByClientId(1));
        Assert.Null(rig.Store.FindByClientId(2));
        Assert.NotNull(rig.Store.FindByClientId(3));
    }

    [Fact]
    public void AnImageExactlyAsLargeAsTheQuotaDisplacesEverythingElse()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1000 });
        rig.Apc("i=1,s=100,v=1,f=32", B64(new byte[400]));
        rig.Apc("i=2,s=250,v=1,f=32", ThousandBytes);
        Assert.Equal(new[] { Ok("i=1"), Ok("i=2") }, rig.Replies);
        Assert.Equal(new uint[] { 2 }, rig.Store.Images.Select(i => i.ClientId).ToArray());
        Assert.Equal(1000, rig.Store.UsedBytes);
    }

    [Fact]
    public void LoweringTheLimitAtRuntimeEvictsImmediately()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            for (int id = 1; id <= 3; id++)
                rig.Apc($"i={id},s=250,v=1,f=32", ThousandBytes);
            Assert.Equal(3000, rig.Store.UsedBytes);

            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1500 }, rig.Decoder);
            Assert.Equal(new uint[] { 3 }, rig.Store.Images.Select(i => i.ClientId).ToArray());
            Assert.Equal(1000, baseline.LiveBytesDelta);
            Assert.Equal(1500, rig.Store.StorageLimitBytes);
            Assert.True(rig.Host.GraphicsChangedCalls > 0);
        }
        baseline.AssertRestored();
    }

    // -------------------------------------------------------------------------------------------- lifecycle

    [Fact]
    public void DisablingDeletesEveryImageAndAbortsTheOpenLoad_ThenCallbacksAreIgnored()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
            rig.Apc("i=2,s=2,v=1,f=24,o=z,m=1", OnePixel);
            Assert.True(rig.Controller.HasOpenTransfer);

            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = false }, null);
            Assert.False(rig.Controller.Enabled);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
            baseline.AssertRestored();

            rig.Replies.Clear();
            rig.Apc("i=3,s=1,v=1,f=24", OnePixel);
            rig.Apc("i=3,s=1,v=1,f=7", OnePixel);
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);

            // Enabling again starts from a clean slate.
            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true }, rig.Decoder);
            rig.Apc("i=3,s=1,v=1,f=24", OnePixel);
            Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
        }
    }

    [Fact]
    public void ClearImagesKeepsTheOptionsAndAbortsTheOpenLoad()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 12345 });
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=2,s=2,v=1,f=24,m=1", OnePixel);

        rig.Controller.ClearImages();

        Assert.True(rig.Controller.Enabled);
        Assert.Equal(12345, rig.Controller.Options.StorageLimitBytes);
        Assert.Equal(12345, rig.Store.StorageLimitBytes);
        Assert.Equal(0, rig.Store.Count);
        Assert.False(rig.Controller.HasOpenTransfer);

        rig.Replies.Clear();
        rig.Apc("m=0", OnePixel); // the rest of the aborted load is a stray
        Assert.Empty(rig.Replies);
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void DisposeMidTransferReleasesEverythingAndIsIdempotent()
    {
        var baseline = new GraphicsBaseline();
        var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=2,s=1,v=1,f=24,o=z,m=1", OnePixel);
        Assert.True(baseline.PendingDelta > 0);

        rig.Dispose();
        rig.Dispose();
        baseline.AssertRestored();
        Assert.False(rig.Controller.Enabled);

        rig.Apc("i=3,s=1,v=1,f=24", OnePixel); // ignored: no exception, no reply
        Assert.Empty(rig.Replies);
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------ the lock is released while decoding

    private static byte[] LargePngPayloadApc => ApcBytes("i=1,f=100", B64(TestPng.Build(256, 256, new byte[256 * 256 * 4])));

    private static string LargePngPayload => B64(TestPng.Build(256, 256, new byte[256 * 256 * 4]));

    [Fact]
    public void ADisposedHostDuringTheUnlockedDecodeDropsTheResult()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Decoder.BeforeDecode = (_, _) => rig.Host.Disposed = true;
            rig.Apc("i=1,f=100", LargePngPayload);
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void DisablingDuringTheUnlockedDecodeDropsTheResult()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=9,s=1,v=1,f=24", OnePixel);
            rig.Replies.Clear();
            rig.Decoder.BeforeDecode = (_, _) =>
                rig.Controller.Configure(new KittyGraphicsOptions { Enabled = false }, null);
            rig.Apc("i=1,f=100", LargePngPayload);

            Assert.Empty(rig.Replies);
            Assert.False(rig.Controller.Enabled);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ClearingImagesDuringTheUnlockedDecodeCancelsTheLoad()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Decoder.BeforeDecode = (_, _) => rig.Controller.ClearImages();
            rig.Apc("i=1,f=100", LargePngPayload);
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.True(rig.Controller.Enabled);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void DisposingTheControllerDuringTheUnlockedDecodeReleasesThePixels()
    {
        var baseline = new GraphicsBaseline();
        var rig = new KittyRig();
        rig.Decoder.BeforeDecode = (_, _) => rig.Controller.Dispose();
        rig.Apc("i=1,f=100", LargePngPayload);
        Assert.Empty(rig.Replies);
        baseline.AssertRestored();
        rig.Dispose();
    }

    [Fact]
    public void ALimitLoweredDuringTheUnlockedDecodeIsHonouredAtCommit()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Decoder.BeforeDecode = (_, _) =>
                rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1000 }, rig.Decoder);
            rig.Apc("i=1,f=100", LargePngPayload);
            Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ALimitRaisedDuringTheUnlockedDecodeDoesNotDisturbTheCommit()
    {
        using var rig = new KittyRig();
        rig.Decoder.BeforeDecode = (_, _) =>
            rig.Controller.Configure(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1L << 30 }, rig.Decoder);
        rig.Apc("i=1,f=100", LargePngPayload);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(256 * 256 * 4, rig.Store.UsedBytes);
    }

    [Fact]
    public void AnImageStoredBeforeTheDecodeSurvivesAndStaysConsistentWhenTheLoadIsCancelled()
    {
        using var rig = new KittyRig();
        rig.Apc("i=9,s=1,v=1,f=24", OnePixel);
        rig.Decoder.BeforeDecode = (_, _) => rig.Controller.AbortTransfer();
        rig.Apc("i=1,f=100", LargePngPayload);

        Assert.Equal(new[] { Ok("i=9") }, rig.Replies); // nothing for the aborted load
        Assert.Equal(new uint[] { 9 }, rig.Store.Images.Select(i => i.ClientId).ToArray());
        rig.Store.ValidateInvariants();
    }

    // -------------------------------------------------------------------------------------- fault containment

    [Fact]
    public void AFailureWhileCommittingFreesThePixelsAndAnswersEinvalWhenAnIdIsKnown()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Host.ThrowOnNow = true;
            rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
            rig.Apc("s=1,v=1,f=24", OnePixel);       // no id: nothing to answer
            Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid key value") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
            baseline.AssertRestored();

            rig.Host.ThrowOnNow = false;
            rig.Apc("i=2,s=1,v=1,f=24", OnePixel);
            Assert.Equal(Ok("i=2"), rig.Replies[^1]);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AFailingReplySinkNeverBreaksParsing()
    {
        using var rig = new KittyRig();
        rig.Host.ThrowOnReply = true;
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);   // stored although the reply cannot be delivered
        rig.Apc("i=2,s=1,v=1,f=7", OnePixel);
        Assert.NotNull(rig.Store.FindByClientId(1));

        rig.Host.ThrowOnReply = false;
        rig.Apc("i=3,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
    }

    // ----------------------------------------------------------------------------- through the adapter + parser

    [Fact]
    public void AdapterHasNoControllerUntilGraphicsAreEnabled()
    {
        using var adapter = new TerminalAdapter(2, 8);
        Assert.Null(adapter.KittyGraphics);
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = false }, null);
        Assert.Null(adapter.KittyGraphics);
        Assert.Throws<ArgumentNullException>(() => adapter.ConfigureGraphics(null!, null));
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, null);
        Assert.NotNull(adapter.KittyGraphics);
        Assert.True(adapter.KittyGraphics!.Enabled);
    }

    [Fact]
    public void WhenDisabledTheParserDiscardsTheApcWithoutPrintingOrReplying()
    {
        var replies = new System.Collections.Generic.List<string>();
        using var adapter = new TerminalAdapter(2, 20);
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(ApcBytes("i=1,s=1,v=1,a=q,f=24", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("X"));
        Assert.Empty(replies);
        Assert.StartsWith("X", adapter.Buffer.GetRowText(0));

        // Explicitly configured but disabled behaves the same, also after having been enabled.
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
        parser.Feed(ApcBytes("i=2,s=1,v=1,f=24", OnePixel));
        Assert.Equal(new[] { Ok("i=2") }, replies);
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = false }, null);
        Assert.Equal(0, adapter.KittyGraphics!.Store.Count);
        parser.Feed(ApcBytes("i=3,s=1,v=1,a=q,f=24", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("Y"));
        Assert.Single(replies);
        Assert.Contains("Y", adapter.Buffer.GetRowText(0));
        Assert.DoesNotContain("AAAA", adapter.Buffer.GetRowText(0));
    }

    [Fact]
    public void EnabledPayloadIsNeverPrintedAsText()
    {
        using var adapter = KittyTransferTests.NewAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=1,v=1,f=24", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("ok"));
        Assert.StartsWith("ok", adapter.Buffer.GetRowText(0));
    }

    [Fact]
    public void FullResetDeletesTheImagesAndAbortsTheLoadButKeepsTheConfiguredLimit()
    {
        var replies = new System.Collections.Generic.List<string>();
        using var adapter = KittyTransferTests.NewAdapter(
            options: new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 5000 }, replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=250,v=1,f=32", ThousandBytes));
        parser.Feed(ApcBytes("i=2,s=1,v=1,f=24,m=1", OnePixel));
        Assert.Equal(1, adapter.KittyGraphics!.Store.Count);
        Assert.True(adapter.KittyGraphics.HasOpenTransfer);

        parser.Feed(Encoding.ASCII.GetBytes("\u001bc"));

        Assert.Equal(0, adapter.KittyGraphics.Store.Count);
        Assert.False(adapter.KittyGraphics.HasOpenTransfer);
        Assert.True(adapter.KittyGraphics.Enabled);
        Assert.Equal(5000, adapter.KittyGraphics.Store.StorageLimitBytes);
        Assert.Equal(5000, adapter.KittyGraphics.Options.StorageLimitBytes);

        replies.Clear();
        parser.Feed(ApcBytes("m=0", OnePixel));                 // stray
        parser.Feed(ApcBytes("i=1,s=250,v=1,f=32", ThousandBytes));
        parser.Feed(ApcBytes("i=3,s=1250,v=1,f=32", B64(new byte[5000])));
        parser.Feed(ApcBytes("i=4,s=1251,v=1,f=32", B64(new byte[5004])));
        Assert.Equal(new[] { Ok("i=1"), Ok("i=3"), Err("i=4", "ENOSPC:Storage quota exceeded") }, replies);
    }

    [Fact]
    public void SoftResetKeepsTheImages()
    {
        using var adapter = KittyTransferTests.NewAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=1,v=1,f=24", OnePixel));
        parser.Feed(Encoding.ASCII.GetBytes("\u001b[!p"));
        Assert.NotNull(adapter.KittyGraphics!.Store.FindByClientId(1));
    }

    [Fact]
    public void DisposingTheAdapterReleasesAllImagePixels()
    {
        var baseline = new GraphicsBaseline();
        var adapter = KittyTransferTests.NewAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=250,v=1,f=32", ThousandBytes));
        parser.Feed(ApcBytes("i=2,s=1,v=1,f=24,o=z,m=1", OnePixel));
        Assert.True(baseline.LiveBytesDelta >= 1000);

        adapter.Dispose();
        adapter.Dispose();
        baseline.AssertRestored();
    }

    [Fact]
    public void LoweringTheLimitThroughTheAdapterEvicts()
    {
        using var adapter = KittyTransferTests.NewAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        for (int id = 1; id <= 3; id++)
            parser.Feed(ApcBytes($"i={id},s=250,v=1,f=32", ThousandBytes));
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1500 }, new FakeImageDecoder());
        Assert.Equal(new uint[] { 3 }, adapter.KittyGraphics!.Store.Images.Select(i => i.ClientId).ToArray());
    }

    [Fact]
    public void ReconfiguringTheDecoderTakesEffectForTheNextPng()
    {
        var replies = new System.Collections.Generic.List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] png = TestPng.Build(1, 1, new byte[4]);
        parser.Feed(ApcBytes("i=1,f=100", B64(png)));
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, null);
        parser.Feed(ApcBytes("i=2,f=100", B64(png)));
        Assert.Equal(new[] { Ok("i=1"), Err("i=2", "EINVAL:Unknown image format") }, replies);
    }

    // -------------------------------------------------------------------------- real threads, real SyncRoot

    private sealed class ThreadedDecode : IDisposable
    {
        public readonly ConcurrentQueue<string> Replies = new();
        public readonly FakeImageDecoder Decoder = new();
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly TerminalAdapter Adapter = new(4, 20);
        public Exception? WorkerFailure;
        public readonly Thread Worker;

        public ThreadedDecode()
        {
            Decoder.BeforeDecode = (_, _) =>
            {
                Entered.Set();
                Release.Wait(Patience);
            };
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, Decoder);
            Adapter.ReplyRequested += reply => Replies.Enqueue(reply.ToString());
            var parser = new BasicAnsiParser { Handler = Adapter };
            byte[] apc = LargePngPayloadApc;
            Worker = new Thread(() =>
            {
                try
                {
                    lock (Adapter.Buffer.SyncRoot) // what the PTY consumer does around every parsed chunk
                        parser.Feed(apc);
                }
                catch (Exception ex)
                {
                    WorkerFailure = ex;
                }
            })
            { IsBackground = true };
        }

        public void StartAndWaitForDecoder()
        {
            Worker.Start();
            Assert.True(Entered.Wait(Patience), "the decoder was never reached");
        }

        public void FinishWorker()
        {
            Release.Set();
            Assert.True(Worker.Join(Patience), "the worker did not finish");
            Assert.Null(WorkerFailure);
        }

        public void Dispose()
        {
            Release.Set();
            Adapter.Dispose();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    [Fact]
    public void TheRenderThreadCanTakeSyncRootWhileTheConsumerDecodesALargePng()
    {
        using var t = new ThreadedDecode();
        t.StartAndWaitForDecoder();

        bool gotLock = Monitor.TryEnter(t.Adapter.Buffer.SyncRoot, TimeSpan.FromSeconds(10));
        if (gotLock)
            Monitor.Exit(t.Adapter.Buffer.SyncRoot);
        t.FinishWorker();

        Assert.True(gotLock, "SyncRoot stayed held during the decode");
        Assert.Equal(new[] { Ok("i=1") }, t.Replies.ToArray());
        Assert.NotNull(t.Adapter.KittyGraphics!.Store.FindByClientId(1));
        bool freeAgain = Monitor.TryEnter(t.Adapter.Buffer.SyncRoot, 0);
        Assert.True(freeAgain);
        Monitor.Exit(t.Adapter.Buffer.SyncRoot);
    }

    [Fact]
    public void DisposingTheAdapterWhileTheConsumerDecodesReleasesThePixelsAndSendsNothing()
    {
        var baseline = new GraphicsBaseline();
        using (var t = new ThreadedDecode())
        {
            t.StartAndWaitForDecoder();
            t.Adapter.Dispose(); // from another thread, while the decoder is blocked outside the lock
            t.FinishWorker();

            Assert.Empty(t.Replies);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void DisablingGraphicsWhileTheConsumerDecodesDropsTheImage()
    {
        var baseline = new GraphicsBaseline();
        using (var t = new ThreadedDecode())
        {
            t.StartAndWaitForDecoder();
            t.Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = false }, null);
            t.FinishWorker();

            Assert.Empty(t.Replies);
            Assert.False(t.Adapter.KittyGraphics!.Enabled);
            Assert.Equal(0, t.Adapter.KittyGraphics.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void LoweringTheLimitWhileTheConsumerDecodesAnswersNoSpace()
    {
        var baseline = new GraphicsBaseline();
        using (var t = new ThreadedDecode())
        {
            t.StartAndWaitForDecoder();
            t.Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 4096 }, t.Decoder);
            t.FinishWorker();

            Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, t.Replies.ToArray());
            Assert.Equal(0, t.Adapter.KittyGraphics!.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ConfigureGraphicsCalledFromTheDecoderWhileTheLockIsReleasedTakesEffectAtCommit()
    {
        var baseline = new GraphicsBaseline();
        var replies = new System.Collections.Generic.List<string>();
        var decoder = new FakeImageDecoder();
        using (var adapter = KittyTransferTests.NewAdapter(decoder: decoder, replies: replies))
        {
            var parser = new BasicAnsiParser { Handler = adapter };
            bool lockHeldInsideDecoder = true;
            decoder.BeforeDecode = (_, _) =>
            {
                // Runs on the consumer thread with SyncRoot released: re-entering the adapter must not deadlock.
                lockHeldInsideDecoder = Monitor.IsEntered(adapter.Buffer.SyncRoot);
                adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 4096 }, decoder);
            };

            lock (adapter.Buffer.SyncRoot) // what the PTY consumer does around every parsed chunk
                parser.Feed(LargePngPayloadApc);

            Assert.False(lockHeldInsideDecoder);
            Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, replies);
            Assert.Equal(0, adapter.KittyGraphics!.Store.Count);
            Assert.False(Monitor.IsEntered(adapter.Buffer.SyncRoot));
        }
        baseline.AssertRestored();
    }

    // ---------------------------------------------------------------------------- random command sequences

    private static void RandomTransmit(KittyRig rig, Random rng, bool leaveOpen)
    {
        int w = rng.Next(1, 25), h = rng.Next(1, 8);
        byte[] raw = RandomBytes(w * h * 4, rng.Next());
        bool compress = rng.Next(3) == 0;
        string b64 = B64(compress ? TestPng.Zlib(raw) : raw);
        string keys = rng.Next(4) switch
        {
            0 => "",                                  // anonymous
            1 => $"I={rng.Next(1, 4)},",
            _ => $"i={rng.Next(1, 7)},",
        };
        keys += $"s={w},v={h},f=32" + (compress ? ",o=z" : "") + (rng.Next(5) == 0 ? ",N=1" : "") + (rng.Next(6) == 0 ? ",a=T" : "");

        // Split points stay before the optional padding so that "=" always lands in the final chunk.
        int stop = b64.TrimEnd('=').Length;
        var cuts = Enumerable.Range(0, rng.Next(0, 3)).Select(_ => rng.Next(0, stop + 1)).OrderBy(c => c).ToList();
        cuts.Add(b64.Length);
        int start = 0;
        for (int i = 0; i < cuts.Count; i++)
        {
            bool more = i < cuts.Count - 1 || leaveOpen;
            string control = i == 0 ? keys + (more ? ",m=1" : "") : (more ? "m=1" : "m=0");
            rig.Apc(control, b64.Substring(start, cuts[i] - start));
            start = cuts[i];
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void SeededRandomCommandSequencesKeepTheStoreConsistentAndLeakNothing(int seed)
    {
        var baseline = new GraphicsBaseline();
        var rng = new Random(seed);
        using (var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 6000 }))
        {
            for (int step = 0; step < 300; step++)
            {
                switch (rng.Next(14))
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                    case 4:
                        RandomTransmit(rig, rng, leaveOpen: false);
                        break;
                    case 5:
                        RandomTransmit(rig, rng, leaveOpen: true);
                        break;
                    case 6:
                        rig.Apc("m=0", B64(RandomBytes(rng.Next(0, 60) * 3, rng.Next()))); // maybe a stray, maybe a finish
                        break;
                    case 7:
                        rig.Apc($"i={rng.Next(1, 7)},s=1,v=1,a=q,f=24", OnePixel);
                        break;
                    case 8:
                        rig.Apc("a=d,d=a");
                        break;
                    case 9:
                        rig.CancelledApc("m=1", OnePixel);
                        break;
                    case 10:
                        rig.Apc($"i={rng.Next(1, 7)},s=1,v=1,f=24", "AA!A");
                        break;
                    case 11:
                        rig.Controller.Configure(
                            new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = rng.Next(500, 8000) }, rig.Decoder);
                        break;
                    case 12:
                        if (rng.Next(4) == 0)
                            rig.Controller.ClearImages();
                        else
                            rig.Controller.AbortTransfer();
                        break;
                    default:
                        {
                            int pw = rng.Next(1, 6), ph = rng.Next(1, 6);
                            rig.Apc($"i={rng.Next(1, 7)},f=100", B64(TestPng.Build(pw, ph, RandomBytes(pw * ph * 4, rng.Next()))));
                            break;
                        }
                }

                rig.Store.ValidateInvariants();
                Assert.Equal(rig.Store.UsedBytes, baseline.LiveBytesDelta);   // pixels alive == pixels stored
                Assert.Equal(rig.Store.Count, baseline.LiveCountDelta);
                if (!rig.Controller.HasOpenTransfer)
                    Assert.Equal(0, baseline.PendingDelta);
                Assert.Equal(rig.Store.StorageLimitBytes, rig.Controller.Options.StorageLimitBytes);
            }

            foreach (string reply in rig.Replies)
            {
                Assert.StartsWith("\u001b_G", reply);
                Assert.EndsWith("\u001b\\", reply);
            }
        }
        baseline.AssertRestored();
    }
}
