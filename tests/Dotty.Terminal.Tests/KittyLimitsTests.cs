using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Hostile-input matrix: caps (image count, payload, dimensions, quota, path, control data, reply size), extreme
/// numeric keys, cancellation, and eviction order / cost. Boundaries are tested at the limit and one beyond.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyLimitsTests
{
    private static string Px(int bytes) => B64(new byte[bytes]);

    private static KittyImage Add(KittyImageStore store, uint id, long now, bool transient = false, int width = 1, int height = 1)
    {
        Assert.True(store.TryAdd(ImagePixels.Allocate(width, height, ImagePixelFormat.Rgba8Premultiplied), id, 0, transient, now, out var image));
        Assert.NotNull(image);
        return image!;
    }

    private static KittyMediaRig Rig(Func<KittyGraphicsOptions, KittyGraphicsOptions>? tweak = null)
    {
        var options = new KittyGraphicsOptions { Enabled = true, Media = KittyMediaRig.AllMedia };
        return new KittyMediaRig(null, tweak == null ? options : tweak(options));
    }

    // ------------------------------------------------------------------------------------ image-count cap

    [Fact]
    public void DefaultImageCountCapIs4096_AndTheOldestImagesGoFirst()
    {
        using var store = new KittyImageStore(1L << 30);
        Assert.Equal(4096, store.MaxImages);
        Assert.Equal(KittyImageStore.DefaultMaxImages, store.MaxImages);
        for (uint id = 1; id <= 4100; id++)
            Add(store, id, now: id);

        Assert.Equal(4096, store.Count);
        for (uint id = 1; id <= 4; id++)
            Assert.Null(store.FindByClientId(id));
        Assert.NotNull(store.FindByClientId(5));
        Assert.NotNull(store.FindByClientId(4100));
        store.ValidateInvariants();
    }

    [Fact]
    public void ExactlyTheCapFitsAndOneMoreEvictsExactlyOne()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(5);
        for (uint id = 1; id <= 5; id++)
            Add(store, id, now: id);
        Assert.Equal(5, store.Count);
        Assert.All(Enumerable.Range(1, 5), id => Assert.NotNull(store.FindByClientId((uint)id)));

        Add(store, 6, now: 6);
        Assert.Equal(5, store.Count);
        Assert.Null(store.FindByClientId(1));
        Assert.NotNull(store.FindByClientId(6));
        store.ValidateInvariants();
    }

    [Fact]
    public void CountEvictionFollowsTheQuotaOrder_LeastRecentlyUsedAmongEquals()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(3);
        Add(store, 1, now: 10);
        Add(store, 2, now: 5);
        Add(store, 3, now: 20);
        Add(store, 4, now: 30);

        Assert.Null(store.FindByClientId(2));        // lowest access ticks
        Assert.Equal(new uint[] { 1, 3, 4 }, store.Images.Select(i => i.ClientId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CountEvictionPrefersTransientImages()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(3);
        Add(store, 1, now: 1);
        Add(store, 2, now: 99, transient: true);
        Add(store, 3, now: 2);
        Add(store, 4, now: 3);

        Assert.Null(store.FindByClientId(2));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void CountEvictionKeepsImagesThatHavePlacements()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(3);
        var placed = Add(store, 1, now: 1);
        store.Place(placed);
        Add(store, 2, now: 50);
        Add(store, 3, now: 60);
        Add(store, 4, now: 70);

        Assert.NotNull(store.FindByClientId(1));     // oldest, but displayed
        Assert.Null(store.FindByClientId(2));        // oldest unreferenced
        Assert.Equal(1, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void TheNewImageIsNeverEvictedByItsOwnAdd()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(1);
        Add(store, 1, now: 100);
        var second = Add(store, 2, now: 1);          // older clock than the first: still the survivor

        Assert.Equal(1, store.Count);
        Assert.Same(second, store.FindByClientId(2));
        Assert.Null(store.FindByClientId(1));
    }

    [Fact]
    public void ShrinkingTheCapEvictsImmediately_KeepingTheMostRecentlyUsed()
    {
        using var store = new KittyImageStore(1L << 20);
        for (uint id = 1; id <= 50; id++)
            Add(store, id, now: id);
        store.SetMaxImages(1);

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.FindByClientId(50));
        store.ValidateInvariants();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMaxImages(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMaxImages(-1));
    }

    [Fact]
    public void ValidateInvariantsDetectsNothingWhenTheCapIsRespected_AndTheCountAccountsForEveryDelete()
    {
        using var store = new KittyImageStore(1L << 20);
        store.SetMaxImages(10);
        for (uint id = 1; id <= 25; id++)
        {
            Add(store, id, now: id);
            if (id % 4 == 0)
                store.DeleteByClientId(id);
            store.ValidateInvariants();
        }
        Assert.True(store.Count <= 10);
        Assert.Equal(store.Images.Sum(i => i.ByteLength), store.UsedBytes);
    }

    // ------------------------------------------------------------------- quota eviction cost and order

    [Theory]
    [InlineData(1, 1)]    // one victim: found by a linear scan
    [InlineData(8, 8)]    // the last victim found by scanning
    [InlineData(9, 9)]    // the ninth victim switches to the single sort
    [InlineData(20, 20)]  // everything goes
    public void QuotaEvictsTheOldestImagesInOrderAcrossTheScanToSortHandover(int victims, int expectedGone)
    {
        // 20 images of 4 bytes fill an 80-byte quota; the new image takes 4 * victims bytes beyond one image's room.
        using var store = new KittyImageStore(80);
        for (uint id = 1; id <= 20; id++)
            Add(store, id, now: id);
        Assert.Equal(80, store.UsedBytes);

        Add(store, 100, now: 100, width: victims, height: 1);     // 4 * victims bytes

        for (uint id = 1; id <= 20; id++)
            Assert.Equal(id > (uint)expectedGone, store.FindByClientId(id) != null);
        Assert.NotNull(store.FindByClientId(100));
        Assert.True(store.UsedBytes <= 80);
        store.ValidateInvariants();
    }

    [Fact]
    public void ScanAndSortPathsAgreeOnTheOrderWithTransientAndPlacedImages()
    {
        using var store = new KittyImageStore(120);                // 30 images of 4 bytes
        var kept = Add(store, 1, now: 1);                          // oldest, but displayed
        store.Place(kept);
        for (uint id = 2; id <= 30; id++)
            Add(store, id, now: id, transient: id % 5 == 0);
        Assert.Equal(30, store.Count);

        // 60 more bytes: 15 victims. Transients (5,10,...,30) go first, then the oldest plain images; the displayed
        // image goes last of all. Fifteen victims cross the scan-to-sort handover.
        Add(store, 100, now: 100, width: 15, height: 1);

        var gone = new uint[] { 5, 10, 15, 20, 25, 30, 2, 3, 4, 6, 7, 8, 9, 11, 12 };
        for (uint id = 1; id <= 30; id++)
            Assert.Equal(!gone.Contains(id), store.FindByClientId(id) != null);
        Assert.NotNull(store.FindByClientId(100));
        Assert.Equal(120, store.UsedBytes);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------- floods and queries

    [Fact]
    public void TransmitFloodRespectsTheImageCap()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig())
        {
            rig.Store.SetMaxImages(5);
            for (int id = 1; id <= 20; id++)
                rig.Apc("i=" + id + ",s=1,v=1,f=24", Px(3));

            Assert.Equal(20, rig.Replies.Count);
            Assert.All(rig.Replies, r => Assert.EndsWith(";OK\u001b\\", r));
            Assert.Equal(5, rig.Store.Count);
            for (uint id = 16; id <= 20; id++)
                Assert.NotNull(rig.Store.FindByClientId(id));
            Assert.Equal(15, rig.Store.UsedBytes);
            rig.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ImageNumberFloodRespectsTheImageCap()
    {
        using var rig = Rig();
        rig.Store.SetMaxImages(4);
        for (int n = 1; n <= 30; n++)
            rig.Apc("I=" + n + ",s=1,v=1,f=24", Px(3));

        Assert.Equal(4, rig.Store.Count);
        Assert.Equal(30, rig.Replies.Count);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void QueryFloodNeverStoresAndEveryQueryIsAnswered()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig())
        {
            for (int i = 0; i < 500; i++)
                rig.Apc("i=" + (1 + i % 3) + ",a=q,s=1,v=1,f=24", Px(3));

            Assert.Equal(500, rig.Replies.Count);
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, rig.Store.UsedBytes);
            Assert.Equal(0, baseline.LiveCountDelta);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AnonymousTransmitsWithoutIdOrNumberAreNeverKept()
    {
        using var rig = Rig();
        for (int i = 0; i < 100; i++)
            rig.Apc("s=1,v=1,f=24", Px(3));
        Assert.Equal(0, rig.Store.Count);
        Assert.Empty(rig.Replies);
    }

    // -------------------------------------------------------------------------- payload / dimension / quota

    [Fact]
    public void PayloadCapBoundary_Uncompressed()
    {
        using var ok = Rig(o => o with { MaxPayloadBytes = 12 });
        ok.Apc("i=1,s=2,v=2,f=24", Px(12));
        Assert.Equal(new[] { Ok("i=1") }, ok.Replies);

        using var over = Rig(o => o with { MaxPayloadBytes = 11 });
        over.Apc("i=1,s=2,v=2,f=24", Px(12));
        Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, over.Replies);
        Assert.Equal(0, over.Store.Count);
    }

    [Fact]
    public void PayloadCapBoundary_Compressed()
    {
        byte[] z = TestPng.Zlib(new byte[] { 1, 2, 3 });
        using var ok = Rig(o => o with { MaxPayloadBytes = z.Length });
        ok.Apc("i=1,s=1,v=1,f=24,o=z", B64(z));
        Assert.Equal(new[] { Ok("i=1") }, ok.Replies);

        using var over = Rig(o => o with { MaxPayloadBytes = z.Length - 1 });
        over.Apc("i=1,s=1,v=1,f=24,o=z", B64(z));
        Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, over.Replies);
    }

    [Fact]
    public void MoreDataThanDeclaredIsAnOverflowAndStoresNothing()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig())
        {
            rig.Apc("i=1,s=2,v=2,f=24", Px(13));
            Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ImageDimensionBoundary()
    {
        using var rig = Rig(o => o with { MaxImageDimension = 16 });
        rig.Apc("i=1,s=16,v=1,f=24", Px(48));
        rig.Apc("i=2,s=1,v=16,f=24", Px(48));
        rig.Apc("i=3,s=17,v=1,f=24", Px(51));
        rig.Apc("i=4,s=1,v=17,f=24", Px(51));

        Assert.Equal(new[]
        {
            Ok("i=1"), Ok("i=2"),
            Err("i=3", "EFBIG:Image too large"), Err("i=4", "EFBIG:Image too large"),
        }, rig.Replies);
    }

    [Fact]
    public void PngHeaderDimensionBoundary()
    {
        using var over = Rig(o => o with { MaxImageDimension = 16 });
        over.Apc("i=1,f=100", B64(TestPng.HeaderOnly(17, 1)));
        Assert.Equal(new[] { Err("i=1", "EFBIG:Image too large") }, over.Replies);

        using var at = Rig(o => o with { MaxImageDimension = 16 });
        at.Apc("i=1,f=100", B64(TestPng.HeaderOnly(16, 1)));
        Assert.NotEqual(Err("i=1", "EFBIG:Image too large"), Assert.Single(at.Replies));   // passes the limit; fails later as a truncated PNG
    }

    [Fact]
    public void StorageQuotaBoundary()
    {
        using var ok = Rig(o => o with { StorageLimitBytes = 12 });
        ok.Apc("i=1,s=2,v=2,f=24", Px(12));
        Assert.Equal(new[] { Ok("i=1") }, ok.Replies);
        Assert.Equal(12, ok.Store.UsedBytes);

        using var over = Rig(o => o with { StorageLimitBytes = 11 });
        over.Apc("i=1,s=2,v=2,f=24", Px(12));
        Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, over.Replies);
        Assert.Equal(0, over.Store.Count);

        using var rgba = Rig(o => o with { StorageLimitBytes = 12 });
        rgba.Apc("i=1,s=4,v=1,f=32", Px(16));                        // 16 bytes
        Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, rgba.Replies);
    }

    [Fact]
    public void HugeDimensionsAreRejectedBeforeAnyMemoryExists()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig(o => o with { MaxImageDimension = KittyGraphicsOptions.HardMaxImageDimension, StorageLimitBytes = KittyGraphicsOptions.HardMaxStorageLimitBytes }))
        {
            rig.Apc("i=1,s=4294967295,v=4294967295,f=32");           // beyond MaxImageDimension; s*v*4 would wrap 32 bits
            rig.Apc("i=2,s=100000,v=100000,f=32");                   // 4e10 bytes: over the 4 GiB quota
            rig.Apc("i=3,s=30000,v=30000,f=32");                     // 3.6e9: inside the quota, beyond int.MaxValue
            rig.Apc("i=4,s=100001,v=1,f=24");
            rig.Apc("i=5,s=1,v=4294967295,f=24");

            Assert.Equal(new[]
            {
                Err("i=1", "EFBIG:Image too large"),
                Err("i=2", "ENOSPC:Storage quota exceeded"),
                Err("i=3", "EFBIG:Image too large"),
                Err("i=4", "EFBIG:Image too large"),
                Err("i=5", "EFBIG:Image too large"),
            }, rig.Replies);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void FourGibDataSizeAndOffsetOnADirectPayloadAreHarmless()
    {
        using var rig = Rig();
        rig.Apc("i=1,s=1,v=1,f=24,S=4294967295,O=4294967295", Px(3));
        rig.Apc("i=2,f=100,o=z,S=4294967295", B64(new byte[] { 1, 2, 3 }));       // inflated size beyond the payload cap
        rig.Apc("i=3,f=100,o=z,S=4294967296", B64(new byte[] { 1, 2, 3 }));       // not a 32-bit value: invalid key

        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=2", "EFBIG:Payload too large"),
            Err("i=3", "EINVAL:Invalid key value"),
        }, rig.Replies);
    }

    [Fact]
    public void FrameLoadsObeyThePayloadCapAndTheDimensionCap()
    {
        using var rig = Rig(o => o with { MaxPayloadBytes = 12, MaxImageDimension = 4 });
        rig.Apc("i=1,s=2,v=2,f=24", Px(12));                 // the root frame: exactly at the payload cap
        rig.Replies.Clear();

        rig.Apc("a=f,i=1,s=2,v=2,f=24", Px(12));             // a frame at the cap is loaded
        rig.Apc("a=f,i=1,s=2,v=2,f=32", Px(16));             // 16 bytes are beyond it
        rig.Apc("a=f,i=1,s=5,v=1,f=24", Px(15));             // wider than MaxImageDimension
        rig.Apc("a=f,i=1,s=1,v=5,f=24", Px(15));             // taller

        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=1", "EFBIG:Payload too large"),
            Err("i=1", "EFBIG:Image too large"),
            Err("i=1", "EFBIG:Image too large"),
        }, rig.Replies);
        Assert.Equal(2, rig.Store.FindByClientId(1)!.Frames.Count);
        Assert.Equal(12 + 16, rig.Store.UsedBytes);          // the root plus one 2x2 RGBA canvas
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void HugeFrameDimensionsAreRejectedBeforeAnyMemoryExists()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig(o => o with { MaxImageDimension = KittyGraphicsOptions.HardMaxImageDimension, StorageLimitBytes = KittyGraphicsOptions.HardMaxStorageLimitBytes }))
        {
            rig.Apc("i=1,s=1,v=1,f=24", Px(3));
            rig.Replies.Clear();

            rig.Apc("a=f,i=1,s=4294967295,v=4294967295,f=32");       // beyond MaxImageDimension
            rig.Apc("a=f,i=1,s=100000,v=100000,f=32");               // 4e10 bytes: over the 4 GiB quota
            rig.Apc("a=f,i=1,s=30000,v=30000,f=32");                 // 3.6e9: inside the quota, beyond int.MaxValue
            rig.Apc("a=f,i=1,s=100001,v=1,f=24");

            Assert.Equal(new[]
            {
                Err("i=1", "EFBIG:Image too large"),
                Err("i=1", "ENOSPC:Storage quota exceeded"),
                Err("i=1", "EFBIG:Image too large"),
                Err("i=1", "EFBIG:Image too large"),
            }, rig.Replies);
            Assert.Equal(1, rig.Store.FindByClientId(1)!.Frames.Count);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------------- extreme placement keys

    [Fact]
    public void ExtremeZIndexAndParentOffsetsDoNotBreakPlacement()
    {
        using var rig = Rig();
        rig.Apc("i=1,s=1,v=1,f=24", Px(3));
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,z=-2147483648,C=1");
        rig.Apc("a=p,i=1,z=2147483647,C=1");
        rig.Apc("a=p,i=1,z=-2147483648,H=-2147483648,V=-2147483648,C=1");
        rig.Apc("a=p,i=1,z=2147483647,H=2147483647,V=2147483647,C=1");

        Assert.Equal(4, rig.Replies.Count);
        Assert.All(rig.Replies, r => Assert.NotEqual(Err("i=1", "EINVAL:Invalid key value"), r));
        Assert.Equal(Ok("i=1"), rig.Replies[0]);
        Assert.Equal(Ok("i=1"), rig.Replies[1]);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void ExtremeSourceRectAndCellCountsAreAnsweredNotCrashed()
    {
        using var rig = Rig();
        rig.Apc("i=1,s=1,v=1,f=24", Px(3));
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,x=4294967295,y=4294967295,C=1");
        rig.Apc("a=p,i=1,w=4294967295,h=4294967295,C=1");
        rig.Apc("a=p,i=1,c=4294967295,r=4294967295,C=1");
        rig.Apc("a=p,i=1,X=4294967295,Y=4294967295,C=1");
        rig.Apc("a=T,i=2,s=1,v=1,f=24,c=4294967295,r=4294967295", Px(3));      // cursor moves by a huge box

        Assert.Equal(5, rig.Replies.Count);
        Assert.All(rig.Replies, r => Assert.NotEqual(Err("i=1", "EINVAL:Invalid key value"), r));
        Assert.NotEqual(Err("i=2", "EINVAL:Invalid key value"), rig.Replies[4]);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void RelativePlacementWithAnUnknownOrEvictedParentIsRefusedCleanly()
    {
        using var rig = Rig();
        rig.Apc("i=1,s=1,v=1,f=24", Px(3));
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,P=999,Q=1,H=1,V=1");

        string reply = Assert.Single(rig.Replies);
        Assert.Contains(reply, new[] { Err("i=1", "EINVAL:Invalid key value"), Err("i=1", "ENOPARENT:Parent placement not found") });
        Assert.Equal(0, rig.Store.PlacementCount);
    }

    // ------------------------------------------------------------------------------- cancellation

    [Fact]
    public void CancelledChunkKillsTheLoad_AndLaterChunksAreDroppedSilently()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Rig())
        {
            rig.Apc("i=1,s=2,v=1,f=24,m=1", "AAAA");
            rig.CancelledApc("m=1", "AAAA");
            rig.Apc("m=0", "AAAA");

            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void CancelledFirstChunkCommitsNothingAndSendsNoReply()
    {
        using var rig = Rig();
        rig.CancelledApc("i=1,s=1,v=1,f=24", Px(3));
        rig.CancelledApc("i=2,s=1,v=1,f=24,m=1", "AAAA");
        rig.Apc("m=0", "AAAA");

        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void CancelledPutAndDeleteDoNothing_ButTheCompletedOnesDo()
    {
        using var rig = Rig();
        rig.Apc("i=1,s=1,v=1,f=24", Px(3));
        rig.Replies.Clear();

        rig.CancelledApc("a=p,i=1", "junk");
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.Empty(rig.Replies);

        rig.CancelledApc("a=d,d=I,i=1", "junk");
        Assert.NotNull(rig.Store.FindByClientId(1));
        Assert.Empty(rig.Replies);

        rig.Apc("a=p,i=1", "junk");
        Assert.Equal(1, rig.Store.PlacementCount);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);

        rig.Apc("a=d,d=I,i=1", "junk");
        Assert.Null(rig.Store.FindByClientId(1));
    }

    [Fact]
    public void CancelledQueryNeverAnswers()
    {
        using var rig = Rig();
        rig.CancelledApc("i=1,a=q,s=1,v=1,f=24", Px(3));
        Assert.Empty(rig.Replies);
    }

    // -------------------------------------------------------------------------------- reply size

    [Fact]
    public void EveryReplyFitsTheBufferEvenWithMaximalIds()
    {
        int longest = 0;
        Span<char> buffer = stackalloc char[KittyResponse.MaxLength];
        foreach (var status in Enum.GetValues<KittyStatus>())
        {
            int n = KittyResponse.Format(buffer, uint.MaxValue, uint.MaxValue, uint.MaxValue, status, 0);
            Assert.InRange(n, 1, KittyResponse.MaxLength);
            string text = buffer.Slice(0, n).ToString();
            Assert.StartsWith("\u001b_Gi=4294967295,I=4294967295,p=4294967295;", text);
            Assert.EndsWith("\u001b\\", text);
            longest = Math.Max(longest, n);
        }
        Assert.True(longest < KittyResponse.MaxLength, "there is headroom for a longer message: " + longest);
    }

    // ------------------------------------------------------------------- parser-level hostile input

    private sealed class Term : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(6, 40);
        public BasicAnsiParser Parser { get; }
        public List<string> Replies { get; } = new();

        public Term()
        {
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, Media = KittyMediaRig.AllMedia }, new FakeImageDecoder());
            Adapter.ReplyRequested += reply => Replies.Add(reply.ToString());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public int Images => (Adapter.Buffer.MainImageStore?.Count ?? 0) + (Adapter.Buffer.AlternateImageStore?.Count ?? 0);

        public void Feed(string text) => Parser.Feed(Encoding.Latin1.GetBytes(text));

        public string Row0 => Adapter.Buffer.GetRowText(0).TrimEnd();

        public void Dispose() => Adapter.Dispose();
    }

    [Theory]
    [InlineData("\u0018")]
    [InlineData("\u001a")]
    public void CanOrSubInsideAnImagePayloadCommitsNothing(string cancel)
    {
        var baseline = new GraphicsBaseline();
        using (var t = new Term())
        {
            t.Feed("\u001b_Ga=T,i=1,s=1,v=1,f=24;AAAA" + cancel + "after");

            Assert.Empty(t.Replies);
            Assert.Equal(0, t.Images);
            Assert.Equal("after", t.Row0);
            Assert.Equal(0, baseline.LiveCountDelta);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void CancelInTheSecondChunkKillsTheWholeImage_AndTheThirdChunkIsIgnored()
    {
        using var t = new Term();
        t.Feed("\u001b_Ga=t,i=1,s=2,v=1,f=24,m=1;AAAA\u001b\\");
        t.Feed("\u001b_Gm=1;AAAA\u0018");
        t.Feed("\u001b_Gm=0;AAAA\u001b\\");
        t.Feed("ok");

        Assert.Empty(t.Replies);
        Assert.Equal(0, t.Images);
        Assert.Equal("ok", t.Row0);
    }

    [Fact]
    public void ControlDataBeyondTheParserCapIsDiscardedWithoutAReply()
    {
        using var t = new Term();
        t.Feed("\u001b_Ga=q,i=1,s=1,v=1,f=24," + new string('z', 600) + ";AAAA\u001b\\ok");

        Assert.Empty(t.Replies);
        Assert.Equal(0, t.Images);
        Assert.Equal("ok", t.Row0);
    }

    [Fact]
    public void ShortControlDataThroughTheParserIsAnswered()
    {
        using var t = new Term();
        string control = "a=q,i=1,s=1,v=1,f=24";
        t.Feed("\u001b_G" + control + ";AAAA\u001b\\");
        Assert.Equal(new[] { Ok("i=1") }, t.Replies);
    }

    [Fact]
    public void EscapeSpamInsideAPayloadNeverThrows_AndTheParserRecovers()
    {
        using var t = new Term();
        t.Feed("\u001b_Ga=t,i=1,s=1,v=1,f=24;" + new string('\u001b', 5000) + "AAAA" + new string('\u001b', 3001));
        t.Feed("\u0018\u001bc\u001b[1;1Hok");

        Assert.Equal("ok", t.Row0);
        Assert.Equal(0, t.Images);
    }

    [Fact]
    public void ManyUnterminatedImagesBackToBackDoNotAccumulatePendingMemory()
    {
        var baseline = new GraphicsBaseline();
        using (var t = new Term())
        {
            for (int i = 0; i < 300; i++)
                t.Feed("\u001b_Ga=t,i=" + (i % 7 + 1) + ",s=2,v=1,f=24,m=1;AAAA");   // never terminated: the next ESC _ G abandons it
            Assert.True(baseline.PendingDelta < 64 * 1024, "pending bytes bounded: " + baseline.PendingDelta);
            t.Feed("\u0018ok");
            Assert.Equal(0, t.Images);
        }
        baseline.AssertRestored();
    }
}
