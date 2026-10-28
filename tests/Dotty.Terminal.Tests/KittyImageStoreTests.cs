using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Id model, byte accounting and quota eviction of <see cref="KittyImageStore"/> (no controller, no parser).</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyImageStoreTests
{
    /// <summary>RGBA pixels of exactly <paramref name="bytes"/> bytes (multiple of 4).</summary>
    private static ImagePixels Px(int bytes) => ImagePixels.Allocate(bytes / 4, 1, ImagePixelFormat.Rgba8Premultiplied);

    private static KittyImage Add(KittyImageStore store, int bytes, uint clientId = 0, uint number = 0, bool transient = false, long now = 0)
    {
        Assert.True(store.TryAdd(Px(bytes), clientId, number, transient, now, out var image));
        Assert.NotNull(image);
        return image!;
    }

    private static List<uint> ClientIds(KittyImageStore store) =>
        store.Images.Select(i => i.ClientId).OrderBy(i => i).ToList();

    [Fact]
    public void AddIndexesByAllThreeIdsAndAccountsBytes()
    {
        var baseline = new GraphicsBaseline();
        using (var store = new KittyImageStore(10_000))
        {
            var a = Add(store, 400, clientId: 7, number: 3);
            var b = Add(store, 800);

            Assert.Equal(2, store.Count);
            Assert.Equal(1200, store.UsedBytes);
            Assert.Same(a, store.FindByClientId(7));
            Assert.Same(a, store.FindByNumber(3));
            Assert.Same(a, store.FindByInternalId(a.InternalId));
            Assert.Same(b, store.FindByInternalId(b.InternalId));
            Assert.Null(store.FindByClientId(0));
            Assert.Null(store.FindByClientId(8));
            Assert.Null(store.FindByNumber(0));
            Assert.Null(store.FindByNumber(4));
            Assert.Equal(1200, baseline.LiveBytesDelta);
            store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void InternalIdsAreMonotonicAndNeverReused()
    {
        using var store = new KittyImageStore(10_000);
        var a = Add(store, 40, clientId: 1);
        var b = Add(store, 40, clientId: 2);
        Assert.True(a.InternalId < b.InternalId);
        Assert.True(store.Delete(b));
        var c = Add(store, 40, clientId: 2); // same client id again: a different internal id
        Assert.True(c.InternalId > b.InternalId);
        store.Clear();
        var d = Add(store, 40);
        Assert.True(d.InternalId > c.InternalId);
    }

    [Fact]
    public void AddingAnExistingClientIdReplacesTheOldImage()
    {
        var baseline = new GraphicsBaseline();
        using var store = new KittyImageStore(10_000);
        var old = Add(store, 400, clientId: 5);
        var replacement = Add(store, 80, clientId: 5);

        Assert.True(old.IsDeleted);
        Assert.False(replacement.IsDeleted);
        Assert.Same(replacement, store.FindByClientId(5));
        Assert.Equal(1, store.Count);
        Assert.Equal(80, store.UsedBytes);
        Assert.Equal(80, baseline.LiveBytesDelta);
        store.ValidateInvariants();
    }

    [Fact]
    public void NumberResolvesToTheNewestImageAndFallsBackOnDelete()
    {
        using var store = new KittyImageStore(10_000);
        var a = Add(store, 40, clientId: 1, number: 5);
        var b = Add(store, 40, clientId: 2, number: 5);
        var c = Add(store, 40, clientId: 3, number: 5);
        Assert.Same(c, store.FindByNumber(5));

        store.Delete(b); // not the newest: nothing changes
        Assert.Same(c, store.FindByNumber(5));
        store.ValidateInvariants();

        store.Delete(c); // newest goes: next newest takes over
        Assert.Same(a, store.FindByNumber(5));
        store.ValidateInvariants();

        store.Delete(a);
        Assert.Null(store.FindByNumber(5));
        store.ValidateInvariants();
    }

    [Fact]
    public void AllocateClientIdStartsInTheUpperRangeAndAdvances()
    {
        using var store = new KittyImageStore(10_000);
        Assert.Equal(0x8000_0000u, store.AllocateClientId());
        Assert.Equal(0x8000_0001u, store.AllocateClientId()); // not reused until the range wraps
    }

    [Fact]
    public void AllocateClientIdSkipsIdsInUse()
    {
        using var store = new KittyImageStore(10_000);
        Add(store, 40, clientId: 0x8000_0000u);
        Add(store, 40, clientId: 0x8000_0001u);
        Add(store, 40, clientId: 0x8000_0003u);
        Assert.Equal(0x8000_0002u, store.AllocateClientId());
        Assert.Equal(0x8000_0004u, store.AllocateClientId()); // 0x80000003 is taken
    }

    [Fact]
    public void ImageLargerThanTheLimitIsRejectedAndKeepsOwnershipWithTheCaller()
    {
        var baseline = new GraphicsBaseline();
        using var store = new KittyImageStore(100);
        var px = Px(104);
        Assert.False(store.TryAdd(px, 1, 0, false, 0, out var image));
        Assert.Null(image);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.UsedBytes);
        Assert.Equal(104, px.AsSpan().Length); // not released by the store
        px.Dispose();
        baseline.AssertRestored();
    }

    [Fact]
    public void ImageExactlyAtTheLimitIsAccepted()
    {
        using var store = new KittyImageStore(100);
        var image = Add(store, 100, clientId: 1);
        Assert.False(image.IsDeleted);
        Assert.Equal(100, store.UsedBytes);
        store.ValidateInvariants();
    }

    [Fact]
    public void NewImageEvictsOlderOnesButIsNeverEvictedByItsOwnAdd()
    {
        using var store = new KittyImageStore(100);
        var first = Add(store, 100, clientId: 1, now: 1);
        var second = Add(store, 100, clientId: 2, transient: true, now: 2);
        Assert.True(first.IsDeleted); // oldest goes although the new one is transient (and so sorts first)
        Assert.False(second.IsDeleted);
        Assert.Equal(new uint[] { 2 }, ClientIds(store));
    }

    [Fact]
    public void EvictionOrderIsUnreferencedThenTransientThenOldest()
    {
        using var store = new KittyImageStore(300);
        var referenced = Add(store, 100, clientId: 1, now: 1);
        store.Place(referenced);
        var plain = Add(store, 100, clientId: 2, now: 2);
        var transient = Add(store, 100, clientId: 3, transient: true, now: 3);

        // 400 > 300: unreferenced first -> {plain(t2), transient(t3)}, transient goes first although newer.
        Add(store, 100, clientId: 4, now: 4);
        Assert.True(transient.IsDeleted);
        Assert.Equal(new uint[] { 1, 2, 4 }, ClientIds(store));

        // Unreferenced {plain(t2), 4(t4)}: oldest first.
        Add(store, 100, clientId: 5, now: 5);
        Assert.True(plain.IsDeleted);
        Assert.Equal(new uint[] { 1, 4, 5 }, ClientIds(store));

        Add(store, 100, clientId: 6, now: 6);
        Assert.Equal(new uint[] { 1, 5, 6 }, ClientIds(store));

        // The referenced image stays for as long as unreferenced ones exist.
        Add(store, 100, clientId: 7, now: 7);
        Assert.Equal(new uint[] { 1, 6, 7 }, ClientIds(store));
        Assert.False(referenced.IsDeleted);
        store.ValidateInvariants();
    }

    [Fact]
    public void ReferencedImagesAreEvictedOnlyAfterAllUnreferencedOnes_TransientFirstThenOldest()
    {
        using var store = new KittyImageStore(200);
        var older = Add(store, 100, clientId: 1, now: 1);
        store.Place(older);
        var newerTransient = Add(store, 100, clientId: 2, transient: true, now: 2);
        store.Place(newerTransient);

        Add(store, 100, clientId: 3, now: 3); // nothing unreferenced to give up: transient referenced goes first
        Assert.True(newerTransient.IsDeleted);
        Assert.False(older.IsDeleted);
        Assert.Equal(new uint[] { 1, 3 }, ClientIds(store));
    }

    [Fact]
    public void TouchAccessProtectsAnImageFromBeingTheOldest()
    {
        using var store = new KittyImageStore(200);
        var a = Add(store, 100, clientId: 1, now: 1);
        var b = Add(store, 100, clientId: 2, now: 2);
        store.TouchAccess(a, 10); // a is now more recent than b
        Add(store, 100, clientId: 3, now: 11);
        Assert.True(b.IsDeleted);
        Assert.False(a.IsDeleted);
    }

    [Fact]
    public void TouchAccessNeverMovesTimeBackwards()
    {
        using var store = new KittyImageStore(10_000);
        var a = Add(store, 40, clientId: 1, now: 50);
        store.TouchAccess(a, 10);
        Assert.Equal(50, a.LastAccessTicks);
        store.TouchAccess(a, 60);
        Assert.Equal(60, a.LastAccessTicks);
    }

    [Fact]
    public void EqualTimesFallBackToTheLowerInternalId()
    {
        using var store = new KittyImageStore(200);
        var a = Add(store, 100, clientId: 1, now: 5);
        var b = Add(store, 100, clientId: 2, now: 5);
        Add(store, 100, clientId: 3, now: 5);
        Assert.True(a.IsDeleted);
        Assert.False(b.IsDeleted);
    }

    [Fact]
    public void UnreferencedAnonymousImagesAreDroppedWhenAnotherImageIsAdded_EvenUnderTheLimit()
    {
        using var store = new KittyImageStore(10_000);
        var anonymous = Add(store, 40);
        Assert.False(anonymous.IsDeleted); // its own add never removes it: the caller decides
        var other = Add(store, 40, clientId: 1);
        Assert.True(anonymous.IsDeleted);
        Assert.False(other.IsDeleted);
        Assert.Equal(40, store.UsedBytes);
        store.ValidateInvariants();
    }

    [Fact]
    public void AnonymousImageWithPlacementsIsKept()
    {
        using var store = new KittyImageStore(10_000);
        var anonymous = Add(store, 40);
        store.Place(anonymous);
        Add(store, 40, clientId: 1);
        Assert.False(anonymous.IsDeleted);
        Assert.Equal(2, store.Count);
        store.ValidateInvariants();
    }

    [Fact]
    public void LoweringTheLimitEvictsImmediatelyIncludingTheNewest()
    {
        using var store = new KittyImageStore(1000);
        for (uint i = 1; i <= 5; i++)
            Add(store, 100, clientId: i, now: i);
        store.SetStorageLimit(250);
        Assert.Equal(new uint[] { 4, 5 }, ClientIds(store));
        Assert.Equal(200, store.UsedBytes);

        store.SetStorageLimit(50); // smaller than any single image: all of them go
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.UsedBytes);
        store.ValidateInvariants();
    }

    [Fact]
    public void RaisingTheLimitEvictsNothing()
    {
        using var store = new KittyImageStore(300);
        Add(store, 100, clientId: 1);
        store.SetStorageLimit(5000);
        Assert.Equal(1, store.Count);
        Assert.Equal(5000, store.StorageLimitBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLimitsAreRejected(long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KittyImageStore(limit));
        using var store = new KittyImageStore(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStorageLimit(limit));
    }

    [Fact]
    public void DeleteIsIdempotentAndIgnoresImagesOfOtherStores()
    {
        var baseline = new GraphicsBaseline();
        using var store = new KittyImageStore(10_000);
        using var other = new KittyImageStore(10_000);
        var a = Add(store, 40, clientId: 1);
        var foreign = Add(other, 40, clientId: 1);

        Assert.False(store.Delete(foreign));
        Assert.False(foreign.IsDeleted);
        Assert.True(store.Delete(a));
        Assert.False(store.Delete(a));
        Assert.False(store.DeleteByClientId(1));
        Assert.True(other.DeleteByClientId(1));
        baseline.AssertRestored();
    }

    [Fact]
    public void ClearAndDisposeReleaseEverythingAndRestoreTheMetrics()
    {
        var baseline = new GraphicsBaseline();
        var store = new KittyImageStore(10_000);
        for (uint i = 1; i <= 10; i++)
            Add(store, 400, clientId: i, number: i % 3);
        Assert.Equal(4000, baseline.LiveBytesDelta);
        Assert.Equal(10, baseline.LiveCountDelta);

        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.UsedBytes);
        baseline.AssertRestored();
        store.ValidateInvariants();

        Add(store, 400, clientId: 1);
        store.Dispose();
        baseline.AssertRestored();
        store.Dispose(); // idempotent
        var rejected = Px(4);
        Assert.Throws<ObjectDisposedException>(() => store.TryAdd(rejected, 1, 0, false, 0, out _));
        rejected.Dispose();
        baseline.AssertRestored();
    }

    [Fact]
    public void ReleasedPixelsStayAliveForRetainers()
    {
        var baseline = new GraphicsBaseline();
        using var store = new KittyImageStore(10_000);
        var image = Add(store, 400, clientId: 1);
        var pixels = image.Root.Retain(); // e.g. a render snapshot lease
        store.Delete(image);
        Assert.Equal(400, baseline.LiveBytesDelta);
        Assert.Equal(400, pixels.AsSpan().Length);
        pixels.Release();
        baseline.AssertRestored();
    }

    [Fact]
    public void ValidateInvariantsDetectsCorruptedAccounting()
    {
        using var store = new KittyImageStore(10_000);
        var image = Add(store, 400, clientId: 1);
        store.ValidateInvariants();

        var placement = store.Place(image);
        store.ValidateInvariants();
        image.PlacementList!.Clear(); // the image forgets a placement the store still indexes
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
        image.PlacementList.Add(placement);
        store.ValidateInvariants();

        var extraFrame = Px(40); // bypasses the store: its bytes are not in UsedBytes
        image.Frames.Add(extraFrame);
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
        image.Frames.Remove(extraFrame);
        extraFrame.Dispose();
        store.ValidateInvariants();
    }

    [Fact]
    public void FramesCountAgainstTheQuotaOfTheirImage()
    {
        using var store = new KittyImageStore(10_000);
        var image = Add(store, 400, clientId: 1);
        Assert.Equal(1, image.Frames.Count);
        Assert.Same(image.Frames[0], image.Root);
        Assert.Equal(400, image.ByteLength);
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
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void SeededRandomOperationSequencesKeepTheInvariantsAndTheMetricsInStep(int seed)
    {
        var baseline = new GraphicsBaseline();
        var rng = new Random(seed);
        var store = new KittyImageStore(5000);
        long now = 0;
        for (int step = 0; step < 400; step++)
        {
            switch (rng.Next(9))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    {
                        var px = Px(4 * rng.Next(1, 400));
                        uint clientId = (uint)rng.Next(0, 12);
                        uint number = (uint)rng.Next(0, 4);
                        bool added = store.TryAdd(px, clientId, number, rng.Next(4) == 0, ++now, out var image);
                        if (added)
                        {
                            Assert.NotNull(image);
                            if (rng.Next(3) == 0)
                                store.Place(image!);
                        }
                        else
                        {
                            Assert.True(px.ByteLength > store.StorageLimitBytes);
                            px.Dispose();
                        }
                        break;
                    }
                case 4:
                    store.DeleteByClientId((uint)rng.Next(0, 12));
                    break;
                case 5:
                    {
                        var all = store.Images.ToList();
                        if (all.Count > 0)
                            store.Delete(all[rng.Next(all.Count)]);
                        break;
                    }
                case 6:
                    {
                        var all = store.Images.ToList();
                        if (all.Count > 0)
                            store.TouchAccess(all[rng.Next(all.Count)], ++now);
                        break;
                    }
                case 7:
                    store.SetStorageLimit(rng.Next(100, 8000));
                    break;
                default:
                    if (rng.Next(10) == 0)
                        store.Clear();
                    else
                        Assert.True(store.AllocateClientId() >= KittyImageStore.FirstAllocatedClientId);
                    break;
            }

            store.ValidateInvariants();
            Assert.True(store.UsedBytes <= store.StorageLimitBytes);
            Assert.Equal(store.UsedBytes, baseline.LiveBytesDelta);
            Assert.Equal(store.Count, baseline.LiveCountDelta);
        }

        store.Dispose();
        baseline.AssertRestored();
    }
}
