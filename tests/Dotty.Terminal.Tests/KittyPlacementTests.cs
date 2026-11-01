using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Parser;
using System;
using System.Linq;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Placement model of <see cref="KittyImageStore"/> (index, replace, cap, geometry, metrics), the pure cursor
/// policy, and <c>a=p</c> / <c>a=T</c> through the controller with a fake host. All cell sizes are 8x16 px.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyPlacementTests
{
    // 40x20 px image: native size is ceil(40/8) x ceil(20/16) = 5 x 2 cells.
    private static KittyImage Img40x20(KittyImageStore store, uint id = 1, uint number = 0) => store.AddImage(40, 20, id, number);

    private static string Rgb(int w, int h) => B64(new byte[w * h * 3]);

    // ------------------------------------------------------------------------------------------- geometry

    [Theory]
    [InlineData("", 5, 2)]                    // native: ceil(40/8) x ceil(20/16)
    [InlineData("c=10", 10, 3)]               // rows = ceil(10*8*20 / (40*16)) = ceil(2.5)
    [InlineData("r=4", 16, 4)]                // cols = ceil(4*16*40 / (20*8)) = 16
    [InlineData("c=3,r=3", 3, 3)]             // both: stretch to the box
    [InlineData("X=5,Y=20", 6, 3)]            // offsets 5 and min(20,15)=15: ceil(45/8)=6, ceil(35/16)=3
    [InlineData("x=10,y=5,w=20,h=10", 3, 1)]  // native crop 20x10
    [InlineData("x=30,y=10,w=100,h=100", 2, 1)] // crop clamped to the remainder 10x10
    [InlineData("w=8,h=16", 1, 1)]
    public void ResolvedCellBoxFollowsTheKeys(string keys, int cols, int rows)
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), keys);
        Assert.Equal(cols, p.EffCols);
        Assert.Equal(rows, p.EffRows);
        Assert.Equal(8, p.BasisCellW);
        Assert.Equal(16, p.BasisCellH);
        store.ValidateInvariants();
    }

    [Fact]
    public void OffsetsAreClampedBelowTheCellSize()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "X=500,Y=500");
        Assert.Equal(7, p.CellXOff);
        Assert.Equal(15, p.CellYOff);
    }

    [Fact]
    public void SourceRectangleIsClampedToTheImage()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "x=30,y=10,w=100,h=100");
        Assert.Equal((30, 10, 10, 10), (p.SrcX, p.SrcY, p.SrcW, p.SrcH));
        var whole = store.Place(Img40x20(store, 2));
        Assert.Equal((0, 0, 40, 20), (whole.SrcX, whole.SrcY, whole.SrcW, whole.SrcH));
    }

    [Theory]
    [InlineData("x=40")]       // starts exactly at the right edge: nothing left
    [InlineData("x=41")]
    [InlineData("y=20")]
    [InlineData("x=4000000000")]
    public void ASourceRectangleOutsideTheImageIsRejectedAndCreatesNothing(string keys)
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        var status = store.TryPlace(image, Parse(keys), 0, 0, 8, 16, out var p);
        Assert.Equal(KittyStatus.RectOutOfBounds, status);
        Assert.Null(p);
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(0, image.PlacementCount);
    }

    [Fact]
    public void TheLastSourcePixelStillPlaces()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "x=39,y=19");
        Assert.Equal((39, 19, 1, 1), (p.SrcX, p.SrcY, p.SrcW, p.SrcH));
        Assert.Equal((1, 1), (p.EffCols, p.EffRows));
    }

    [Fact]
    public void ZeroCellSizeIsTreatedAsOne()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        Assert.Equal(KittyStatus.Ok, store.TryPlace(image, Parse(""), 0, 0, 0, 0, out var p));
        Assert.Equal((40, 20), (p!.EffCols, p.EffRows));
        Assert.Equal((1, 1), (p.BasisCellW, p.BasisCellH));
    }

    [Fact]
    public void PlacementKeepsAnchorZAndRequestedBox()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "c=2,z=-7,p=3", anchorRow: 12, anchorCol: 4);
        Assert.Equal(12, p.AnchorRow);
        Assert.Equal(4, p.AnchorCol);
        Assert.Equal(-7, p.Z);
        Assert.Equal(3u, p.ClientId);
        Assert.Equal(2u, p.RequestedCols);
        Assert.Equal(0u, p.RequestedRows);
        Assert.Equal(PlacementKind.Normal, p.Kind);
        Assert.Equal(12 + p.EffRows, p.EndRow);
    }

    [Fact]
    public void VirtualPlacementHasNoAnchorAndTheRequestedGrid()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "U=1,c=6,r=2,p=1", anchorRow: 99, anchorCol: 9);
        Assert.Equal(PlacementKind.Virtual, p.Kind);
        Assert.Equal((0L, 0), (p.AnchorRow, p.AnchorCol));
        Assert.Equal((6, 2), (p.EffCols, p.EffRows));
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------- index and replace

    [Fact]
    public void PlacingWithAnExistingImageAndPlacementIdReplacesThePlacement()
    {
        var baseline = new GraphicsBaseline();
        long placementsBefore = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        using (var store = new KittyImageStore(1_000_000))
        {
            var image = Img40x20(store);
            var first = store.Place(image, "p=5,c=2,r=2", anchorRow: 1, anchorCol: 1);
            var second = store.Place(image, "p=5,c=3,r=1", anchorRow: 7, anchorCol: 2);

            Assert.NotSame(first, second);
            Assert.Equal(1, store.PlacementCount);
            Assert.Equal(1, image.PlacementCount);
            Assert.Same(second, store.FindPlacement(image, 5));
            Assert.Equal((7L, 2, 3, 1), (second.AnchorRow, second.AnchorCol, second.EffCols, second.EffRows));
            Assert.True(second.InternalId > first.InternalId);
            Assert.Equal(1, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore);
            store.ValidateInvariants();
        }
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore);
        baseline.AssertRestored();
    }

    [Fact]
    public void DifferentPlacementIdsAndDifferentImagesDoNotCollide()
    {
        using var store = new KittyImageStore(1_000_000);
        var a = Img40x20(store, 1);
        var b = Img40x20(store, 2);
        store.Place(a, "p=1");
        store.Place(a, "p=2");
        store.Place(b, "p=1");
        Assert.Equal(3, store.PlacementCount);
        Assert.Equal(2, a.PlacementCount);
        Assert.Equal(1, b.PlacementCount);
        Assert.NotSame(store.FindPlacement(a, 1), store.FindPlacement(b, 1));
        Assert.Null(store.FindPlacement(a, 3));
        Assert.Null(store.FindPlacement(a, 0));
        store.ValidateInvariants();
    }

    [Fact]
    public void PlacementsWithoutAnIdAreAllKept()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        store.Place(image);
        store.Place(image);
        store.Place(image, "p=0");
        Assert.Equal(3, store.PlacementCount);
        Assert.All(store.Placements, p => Assert.Equal(0u, p.ClientId));
        store.ValidateInvariants();
    }

    [Fact]
    public void ThePlacementIdIsIgnoredWhenTheImageHasNoClientId()
    {
        using var store = new KittyImageStore(1_000_000);
        var anonymous = store.AddImage(40, 20);
        var first = store.Place(anonymous, "p=7");
        var second = store.Place(anonymous, "p=7");
        Assert.Equal(0u, first.ClientId);
        Assert.Equal(0u, second.ClientId);
        Assert.Equal(2, store.PlacementCount); // not replaced: the id never applied
        Assert.Null(store.FindPlacement(anonymous, 7));
        store.ValidateInvariants();
    }

    [Fact]
    public void AnAnonymousImageWithAPlacementIsKept()
    {
        using var store = new KittyImageStore(1_000_000);
        var anonymous = store.AddImage(40, 20);
        store.Place(anonymous);
        Assert.False(anonymous.IsDeleted);
        Assert.Equal(1, anonymous.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void PlacingAnImageThatIsNotInTheStoreFails()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        store.Delete(image);
        Assert.Equal(KittyStatus.ImageNotFound, store.TryPlace(image, Parse(""), 0, 0, 8, 16, out var p));
        Assert.Null(p);

        using var other = new KittyImageStore(1_000_000);
        var foreign = Img40x20(other);
        Assert.Equal(KittyStatus.ImageNotFound, store.TryPlace(foreign, Parse(""), 0, 0, 8, 16, out _));
    }

    [Fact]
    public void DeletingAnImageDeletesItsPlacementsAndTheirMetrics()
    {
        long before = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        store.Place(image, "p=1");
        store.Place(image, "p=2");
        Assert.Equal(2, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);

        Assert.True(store.Delete(image));
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
        store.ValidateInvariants();
    }

    [Fact]
    public void ClearDeletesPlacementsAndDisposeFreesTheMetric()
    {
        long before = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        store.Place(image);
        store.Place(image);
        store.Clear();
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);

        store.Place(Img40x20(store, 2));
        store.Dispose();
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
    }

    [Fact]
    public void RetransmittingAnIdDropsTheOldPlacements()
    {
        using var store = new KittyImageStore(1_000_000);
        var old = Img40x20(store, 5);
        store.Place(old);
        var replacement = Img40x20(store, 5);
        Assert.True(old.IsDeleted);
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(0, replacement.PlacementCount);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------------ cap

    [Fact]
    public void OverTheCapWithNothingInTheHistoryFailsAndKeepsEverything()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 2 };
        var image = Img40x20(store);
        var a = store.Place(image, "p=1", anchorRow: 0);
        var b = store.Place(image, "p=2", anchorRow: 1);

        var status = store.TryPlace(image, Parse("p=3"), 2, 0, 8, 16, out var third);
        Assert.Equal(KittyStatus.TooManyPlacements, status);
        Assert.Null(third);
        Assert.Equal(new[] { a, b }.Select(p => p.InternalId), store.Placements.Select(p => p.InternalId).OrderBy(i => i));
        store.ValidateInvariants();
    }

    [Fact]
    public void OverTheCapTheOldestHistoryPlacementGoesFirst()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 3 };
        var image = Img40x20(store);
        // Three placements of two rows each (ids 1..3) at rows 0, 2, 4; then the screen scrolls by 3 rows.
        store.Place(image, "c=1,r=2,p=1", anchorRow: 0);
        var middle = store.Place(image, "c=1,r=2,p=2", anchorRow: 2);
        var visible = store.Place(image, "c=1,r=2,p=3", anchorRow: 4);
        store.OnRingRotatedUp(3, scrollbackCapacity: 100);
        // ViewTop is 3: placement 1 (end 2) is wholly in the history, placement 2 (end 4) is not.

        var fresh = store.Place(image, "c=1,r=1,p=4", anchorRow: 8);
        Assert.True(store.FindPlacement(image, 1) == null);
        Assert.Same(middle, store.FindPlacement(image, 2));
        Assert.Same(visible, store.FindPlacement(image, 3));
        Assert.Same(fresh, store.FindPlacement(image, 4));
        Assert.Equal(3, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void WhenSeveralHistoryPlacementsExistTheLowestInternalIdIsDeleted()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 3 };
        var image = Img40x20(store);
        store.Place(image, "c=1,r=1,p=1", anchorRow: 1);   // older, but placed first at a later row
        store.Place(image, "c=1,r=1,p=2", anchorRow: 0);
        store.Place(image, "c=1,r=1,p=3", anchorRow: 20);
        store.OnRingRotatedUp(10, 100);

        store.Place(image, "c=1,r=1,p=4", anchorRow: 30);
        Assert.Null(store.FindPlacement(image, 1)); // "oldest" means lowest id, not lowest row
        Assert.NotNull(store.FindPlacement(image, 2));
        store.ValidateInvariants();
    }

    [Fact]
    public void ReplacingAtTheCapNeedsNoFreeSlot()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 2 };
        var image = Img40x20(store);
        store.Place(image, "p=1");
        store.Place(image, "p=2");
        var replaced = store.Place(image, "p=2,c=9");
        Assert.Equal(2, store.PlacementCount);
        Assert.Equal(9, replaced.EffCols);
        store.ValidateInvariants();
    }

    [Fact]
    public void VirtualPlacementsCountAgainstTheCapButAreNeverEvictedAsHistory()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 1 };
        var image = Img40x20(store);
        store.Place(image, "U=1,p=1,c=2,r=2");
        store.OnRingRotatedUp(1000, 10);
        Assert.Equal(KittyStatus.TooManyPlacements, store.TryPlace(image, Parse("p=2"), 0, 0, 8, 16, out _));
        Assert.Equal(1, store.PlacementCount);
    }

    [Fact]
    public void ACapOfZeroOrLessIsRejected()
    {
        using var store = new KittyImageStore(1_000_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.MaxPlacements = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.MaxPlacements = -4);
    }

    [Fact]
    public void LoweringTheCapDeletesNothingButBlocksNewPlacements()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        for (uint i = 1; i <= 4; i++)
            store.Place(image, $"p={i}");
        store.MaxPlacements = 2;
        Assert.Equal(4, store.PlacementCount);
        Assert.Equal(KittyStatus.TooManyPlacements, store.TryPlace(image, Parse("p=9"), 0, 0, 8, 16, out _));
        // Replacing is still fine only while it does not leave the store over the cap: 3 others + 1 >= 2.
        Assert.Equal(KittyStatus.TooManyPlacements, store.TryPlace(image, Parse("p=1"), 0, 0, 8, 16, out _));
        store.ValidateInvariants();
    }

    // --------------------------------------------------------------------- eviction ordering with placements

    [Fact]
    public void ImagesWithPlacementsAreEvictedAfterUnreferencedOnes()
    {
        using var store = new KittyImageStore(3 * 40 * 20 * 4);
        var shown = store.AddImage(40, 20, 1, now: 1);
        store.Place(shown);
        var idle = store.AddImage(40, 20, 2, now: 2);
        var idle2 = store.AddImage(40, 20, 3, now: 3);
        Assert.Equal(3, store.Count);

        store.AddImage(40, 20, 4, now: 4); // over quota: the oldest unreferenced image (2) goes, not the oldest (1)
        Assert.True(idle.IsDeleted);
        Assert.False(shown.IsDeleted);
        Assert.False(idle2.IsDeleted);

        store.AddImage(40, 20, 5, now: 5);
        Assert.True(idle2.IsDeleted);
        Assert.False(shown.IsDeleted);
        store.ValidateInvariants();
    }

    [Fact]
    public void EvictingAPlacedImageTakesItsPlacementsWithIt()
    {
        long before = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        using var store = new KittyImageStore(2 * 40 * 20 * 4);
        var a = store.AddImage(40, 20, 1, now: 1);
        var b = store.AddImage(40, 20, 2, now: 2);
        store.Place(a);
        store.Place(b);
        store.AddImage(40, 20, 3, now: 3); // all referenced: the oldest (a) is evicted
        Assert.True(a.IsDeleted);
        Assert.Equal(1, store.PlacementCount);
        Assert.Equal(1, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
        store.ValidateInvariants();
    }

    [Fact]
    public void AnAnonymousImageIsDroppedWhenItsLastPlacementGoes()
    {
        using var store = new KittyImageStore(1_000_000);
        var anonymous = store.AddImage(40, 20);
        store.Place(anonymous);
        Assert.Equal(1, store.ClearAll());
        Assert.True(anonymous.IsDeleted);
        Assert.Equal(0, store.Count);
        store.ValidateInvariants();
    }

    [Fact]
    public void ANamedImageSurvivesItsLastPlacement()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        store.Place(image);
        Assert.Equal(1, store.ClearAll());
        Assert.False(image.IsDeleted);
        Assert.Equal(1, store.Count);
    }

    // ------------------------------------------------------------------------------- invariants detection

    [Fact]
    public void ValidateInvariantsDetectsPlacementCorruption()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        var p = store.Place(image, "p=1");
        store.Place(image, "p=2");
        store.ValidateInvariants();

        image.PlacementList!.Remove(p); // image no longer lists a placement the store indexes
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
        image.PlacementList.Add(p);
        store.ValidateInvariants();

        p.Parent = p; // a parent cycle
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
        p.Parent = null;
        store.ValidateInvariants();

        p.AnchorRow = -100; // below the cached minimum end row without notifying the store
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
    }

    [Fact]
    public void ValidateInvariantsFollowsAParentChainThatIsAcyclic()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Img40x20(store);
        var a = store.Place(image, "p=1");
        Assert.Equal(KittyStatus.Ok, store.TryPlaceRelative(image, Parse("p=2,P=1,Q=1"), 8, 16, out var b));
        Assert.Equal(KittyStatus.Ok, store.TryPlaceRelative(image, Parse("p=3,P=1,Q=2"), 8, 16, out var c));
        store.ValidateInvariants();
        b!.Parent = c; // b -> c -> b
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
    }

    // --------------------------------------------------------------------------------------- cursor policy

    [Theory]
    //   row col columns rows bottom effC effR   -> row col scroll
    [InlineData(0, 0, 10, 6, 5, 3, 2, 1, 3, 0)]
    [InlineData(2, 4, 10, 6, 5, 2, 1, 2, 6, 0)]
    [InlineData(0, 8, 10, 6, 5, 2, 1, 1, 0, 0)]    // 8+2 = 10 >= 10: wrap
    [InlineData(0, 8, 10, 6, 5, 1, 1, 0, 9, 0)]    // 8+1 = 9 < 10: stay
    [InlineData(2, 9, 10, 6, 5, 1, 1, 3, 0, 0)]
    [InlineData(4, 0, 10, 6, 5, 1, 3, 5, 1, 1)]    // row 4+2 = 6 > 5: scroll 1
    [InlineData(5, 0, 10, 6, 5, 1, 1, 5, 1, 0)]
    [InlineData(5, 9, 10, 6, 5, 1, 1, 5, 0, 1)]    // wrap on the last row scrolls
    [InlineData(0, 0, 10, 6, 5, 1, 100, 5, 1, 94)] // taller than the screen: the whole overshoot (99 - 5) scrolls
    [InlineData(2, 2, 10, 6, 5, 1, 0, 2, 3, 0)]    // zero rows counts as one
    [InlineData(4, 0, 10, 6, 2, 1, 3, 5, 1, 0)]    // started below the margins: clamp to the screen, no scroll
    [InlineData(2, 0, 10, 6, 2, 1, 1, 2, 1, 0)]
    [InlineData(2, 0, 10, 6, 2, 1, 2, 2, 1, 1)]    // bottom margin 2: scroll the region once
    [InlineData(0, 0, 10, 6, 5, 65535, 1, 1, 0, 0)]
    [InlineData(0, 0, 10, 3, 2, 1, 100, 2, 1, 97)] // 99 - 2
    [InlineData(5, 0, 10, 6, 5, 1, 65535, 5, 1, 65534)] // the largest cell box: the overshoot is 5 + 65534 - 5
    [InlineData(0, 9, 10, 6, 5, 2, 1, 1, 0, 0)]    // a cursor waiting on the last column: 9 + 2 >= 10, wraps
    public void CursorAdvancesPastThePlacement(int row, int col, int columns, int rows, int bottom, int effCols, int effRows,
        int expRow, int expCol, int expScroll)
    {
        var advance = KittyCursorPolicy.Advance(row, col, columns, rows, bottom, effCols, effRows);
        Assert.Equal(new CursorAdvance(expRow, expCol, expScroll), advance);
    }

    // ----------------------------------------------------------------------------- a=p through the controller

    [Fact]
    public void PutByIdPlacesAtTheCursorAndRepliesOk()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=40,v=20,f=24", Rgb(40, 20));
        rig.Replies.Clear();
        rig.Host.CursorRow = 3;
        rig.Host.CursorCol = 4;

        rig.Apc("a=p,i=1,p=9");

        Assert.Equal(new[] { Ok("i=1,p=9") }, rig.Replies);
        var placement = Assert.Single(rig.Store.Placements);
        Assert.Equal((3L, 4), (placement.AnchorRow, placement.AnchorCol));
        Assert.Equal(9u, placement.ClientId);
        Assert.Equal((5, 2), (placement.EffCols, placement.EffRows));
        Assert.Equal(new[] { (5, 2) }, rig.Host.CursorMoves);
        Assert.Equal((4, 9), (rig.Host.CursorRow, rig.Host.CursorCol)); // row + (2-1), col + 5
    }

    [Fact]
    public void PutNeverMovesOrChangesTheImage()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=40,v=20,f=24", Rgb(40, 20));
        var image = rig.Store.FindByClientId(1)!;
        long bytes = rig.Store.UsedBytes;
        rig.Apc("a=p,i=1");
        rig.Apc("a=p,i=1,p=2,c=3");
        Assert.Same(image, rig.Store.FindByClientId(1));
        Assert.Equal(bytes, rig.Store.UsedBytes);
        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(2, image.PlacementCount);
    }

    [Fact]
    public void PutOfAnUnknownImageIsEnoent()
    {
        using var rig = new KittyRig();
        rig.Apc("a=p,i=9");
        rig.Apc("a=p,i=9,p=2");
        rig.Apc("a=p,I=4");
        rig.Apc("a=p,I=4,p=3");
        Assert.Equal(new[]
        {
            Err("i=9", "ENOENT:Image not found"),
            Err("i=9,p=2", "ENOENT:Image not found"),
            Err("I=4", "ENOENT:Image not found"),
            Err("I=4,p=3", "ENOENT:Image not found"),
        }, rig.Replies);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void PutByNumberResolvesTheNewestImageWithThatNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=3,s=1,v=1,f=24", "AAAA");   // allocated id 2147483648
        rig.Apc("I=3,s=1,v=1,f=24", "AAAA");   // allocated id 2147483649, the newer one
        rig.Replies.Clear();

        rig.Apc("a=p,I=3");
        Assert.Equal(new[] { Ok("i=2147483649,I=3") }, rig.Replies);
        var newest = rig.Store.FindByClientId(2147483649u)!;
        Assert.Equal(1, newest.PlacementCount);
        Assert.Equal(0, rig.Store.FindByClientId(2147483648u)!.PlacementCount);

        rig.Replies.Clear();
        rig.Apc("a=p,I=3,p=7");
        Assert.Equal(new[] { Ok("i=2147483649,I=3,p=7") }, rig.Replies);
        Assert.NotNull(rig.Store.FindPlacement(newest, 7));
    }

    [Fact]
    public void PutWithBothIdAndNumberIsInvalid()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,I=2");
        Assert.Equal(new[] { Err("i=1,I=2", "EINVAL:Cannot specify both image id and image number") }, rig.Replies);
        Assert.Equal(0, rig.Store.PlacementCount);
    }

    [Fact]
    public void PutWithoutAnIdOrNumberAddressesNothingAndAnswersNothing()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=p");
        rig.Apc("a=p,p=3,c=2");
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.PlacementCount);
    }

    [Fact]
    public void QuietModesApplyToPutReplies()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,q=1");        // OK suppressed
        rig.Apc("a=p,i=8,q=1");        // error shown
        rig.Apc("a=p,i=1,q=2");        // everything suppressed
        rig.Apc("a=p,i=8,q=2");
        Assert.Equal(new[] { Err("i=8", "ENOENT:Image not found") }, rig.Replies);
        Assert.Equal(2, rig.Store.PlacementCount); // the quiet puts still placed
    }

    [Fact]
    public void PutOutsideTheImageIsRectOutOfBounds()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,x=1");
        Assert.Equal(new[] { Err("i=1", "EINVAL:Rectangle out of bounds") }, rig.Replies);
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void HAndVAloneMeanNothingWithoutAParent()
    {
        // Relative keys with a parent are covered by KittyRelativePlacementTests.
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,H=1,V=1");
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(PlacementKind.Normal, Assert.Single(rig.Store.Placements).Kind);
    }

    [Fact]
    public void PutAtThePlacementCapIsEnospc()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPlacementsPerScreen = 1 });
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Apc("a=p,i=1,p=1");
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,p=2");
        Assert.Equal(new[] { Err("i=1,p=2", "ENOSPC:Too many placements") }, rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
    }

    [Fact]
    public void ThePlacementAnchorIncludesTheViewTop()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Store.OnRingRotatedUp(7, 100);
        rig.Host.CursorRow = 2;
        rig.Apc("a=p,i=1");
        Assert.Equal(9, rig.Store.Placements.Single().AnchorRow);
    }

    [Fact]
    public void PutTouchesTheImageForLru()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        var image = rig.Store.FindByClientId(1)!;
        long before = image.LastAccessTicks;
        rig.Apc("a=p,i=1");
        Assert.True(image.LastAccessTicks > before);
    }

    [Fact]
    public void CursorPolicyKeyAndVirtualPlacementsLeaveTheCursorAlone()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Apc("a=p,i=1,p=1,C=1,c=3,r=2");
        rig.Apc("a=p,i=1,p=2,U=1,c=3,r=2");
        Assert.Empty(rig.Host.CursorMoves);
        Assert.Equal((0, 0), (rig.Host.CursorRow, rig.Host.CursorCol));
        Assert.Equal(PlacementKind.Normal, rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 1)!.Kind);
        Assert.Equal(PlacementKind.Virtual, rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 2)!.Kind);

        rig.Apc("a=p,i=1,p=3,C=0,c=3,r=2");
        Assert.Equal(new[] { (3, 2) }, rig.Host.CursorMoves);
    }

    // ----------------------------------------------------------------------------- a=T through the controller

    [Fact]
    public void TransmitAndPutStoresPlacesAndMovesTheCursor()
    {
        using var rig = new KittyRig();
        rig.Host.CursorRow = 1;
        rig.Host.CursorCol = 2;
        rig.Apc("a=T,i=8,p=3,s=1,v=1,f=24,c=4,r=3", "AAAA");

        Assert.Equal(new[] { Ok("i=8,p=3") }, rig.Replies);
        var image = rig.Store.FindByClientId(8)!;
        var placement = rig.Store.FindPlacement(image, 3)!;
        Assert.Equal((1L, 2, 4, 3), (placement.AnchorRow, placement.AnchorCol, placement.EffCols, placement.EffRows));
        Assert.Equal(new[] { (4, 3) }, rig.Host.CursorMoves);
        Assert.Equal((3, 6), (rig.Host.CursorRow, rig.Host.CursorCol));
    }

    [Fact]
    public void TransmitAndPutUsesTheCursorOfTheFinalChunk()
    {
        using var rig = new KittyRig();
        rig.Host.CursorRow = 0;
        rig.Host.CursorCol = 0;
        rig.Apc("a=T,i=2,s=2,v=1,f=24,m=1,c=1,r=1", B64(new byte[] { 1, 2, 3 }));
        rig.Host.CursorRow = 4;
        rig.Host.CursorCol = 5;
        Assert.Equal(0, rig.Store.PlacementCount);
        rig.Apc("m=0", B64(new byte[] { 4, 5, 6 }));

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        var placement = rig.Store.Placements.Single();
        Assert.Equal((4L, 5), (placement.AnchorRow, placement.AnchorCol));
    }

    [Fact]
    public void TransmitAndPutReportsAPlacementErrorAfterKeepingTheImage()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=8,s=1,v=1,f=24,P=2", "AAAA");   // P names an unknown parent image (docs/KittyGraphics.md)
        Assert.Equal(new[] { Err("i=8", "ENOPARENT:Parent placement not found") }, rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(8));
        Assert.Equal(0, rig.Store.PlacementCount);

        rig.Replies.Clear();
        rig.Apc("a=T,i=9,s=1,v=1,f=24,x=3", "AAAA");
        Assert.Equal(new[] { Err("i=9", "EINVAL:Rectangle out of bounds") }, rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(9));
    }

    [Fact]
    public void TransmitAndPutOfAnAnonymousImageKeepsItOnlyWhilePlaced()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPlacementsPerScreen = 1 });
        rig.Apc("a=T,s=1,v=1,f=24", "AAAA");
        Assert.Empty(rig.Replies);
        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(1, rig.Store.PlacementCount);

        rig.Apc("a=T,s=1,v=1,f=24", "AAAA"); // over the cap, nothing in the history: ENOSPC, silent, image dropped
        Assert.Empty(rig.Replies);
        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(1, rig.Store.PlacementCount);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void TransmitAndPutByNumberAnswersWithTheAllocatedId()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,I=5,s=1,v=1,f=24,c=1,r=1", "AAAA");
        Assert.Equal(new[] { Ok("i=2147483648,I=5") }, rig.Replies);
        Assert.Equal(1, rig.Store.FindByNumber(5)!.PlacementCount);
    }

    [Fact]
    public void TransmitAndPutOfAnExistingIdReplacesTheImageAndItsPlacements()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=1,p=1,s=1,v=1,f=24", "AAAA");
        rig.Apc("a=p,i=1,p=2");
        Assert.Equal(2, rig.Store.PlacementCount);
        rig.Apc("a=T,i=1,p=1,s=1,v=1,f=24", "AAAA");
        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(1, rig.Store.PlacementCount);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void TransmitAndPutQuietRules()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=1,s=1,v=1,f=24,q=1", "AAAA");
        // An unknown parent (P=9) fails the placement: q=2 hides that error, q=1 only hides OK.
        rig.Apc("a=T,i=2,s=1,v=1,f=24,q=2,P=9", "AAAA");
        rig.Apc("a=T,i=3,s=1,v=1,f=24,q=1,P=9", "AAAA");
        Assert.Equal(new[] { Err("i=3", "ENOPARENT:Parent placement not found") }, rig.Replies);
    }

    [Fact]
    public void PlainTransmitDoesNotPlaceOrMoveTheCursor()
    {
        using var rig = new KittyRig();
        rig.Apc("a=t,i=1,s=1,v=1,f=24,c=4,r=3", "AAAA");
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void QueryNeverPlaces()
    {
        using var rig = new KittyRig();
        rig.Apc("a=q,i=1,s=1,v=1,f=24", "AAAA");
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void ControllerPlacementsAreReleasedWithTheController()
    {
        var baseline = new GraphicsBaseline();
        long before = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        using (var rig = new KittyRig())
        {
            rig.Apc("a=T,i=1,s=1,v=1,f=24", "AAAA");
            rig.Apc("a=p,i=1,p=2");
            Assert.Equal(2, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
        }
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
        baseline.AssertRestored();
    }

    [Fact]
    public void PlacementsAnnounceAGraphicsChange()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        int before = rig.Host.GraphicsChangedCalls;
        rig.Apc("a=p,i=1");
        Assert.True(rig.Host.GraphicsChangedCalls > before);
        before = rig.Host.GraphicsChangedCalls;
        rig.Apc("a=p,i=404"); // nothing changed
        Assert.Equal(before, rig.Host.GraphicsChangedCalls);
    }

    [Fact]
    public void QuotaEvictionOfADisplayedImageAnnouncesAGraphicsChange()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 1500 });
        rig.Apc("a=T,i=1,s=250,v=1,f=32,c=1,r=1", B64(new byte[1000]));
        Assert.Equal(1, rig.Store.PlacementCount);

        int before = rig.Host.GraphicsChangedCalls;
        rig.Apc("i=2,s=250,v=1,f=32", B64(new byte[1000]));        // evicts the displayed image 1
        Assert.Null(rig.Store.FindByClientId(1));
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.True(rig.Host.GraphicsChangedCalls > before);

        before = rig.Host.GraphicsChangedCalls;
        rig.Apc("i=3,s=250,v=1,f=32", B64(new byte[1000]));        // evicts image 2, which showed nothing
        Assert.Null(rig.Store.FindByClientId(2));
        Assert.Equal(before, rig.Host.GraphicsChangedCalls);
    }

    // ------------------------------------------------------------------------- deferred put / delete

    [Fact]
    public void APutIgnoresItsPayload()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,p=3", "AAAA");
        Assert.Equal(new[] { Ok("i=1,p=3") }, rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
    }

    [Fact]
    public void ACancelledPutOrDeleteDoesNothing()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Apc("a=p,i=1,c=2,r=2");
        rig.Replies.Clear();
        var cursor = (rig.Host.CursorRow, rig.Host.CursorCol);

        rig.CancelledApc("a=p,i=1,p=5", "AAAA");      // cut off after its ';'
        rig.CancelledApc("a=d,d=A", "AAAA");
        rig.CancelledApc("a=p,i=404");

        Assert.Empty(rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
        Assert.NotNull(rig.Store.FindByClientId(1));
        Assert.Single(rig.Host.CursorMoves);
        Assert.Equal(cursor, (rig.Host.CursorRow, rig.Host.CursorCol));
    }

    [Fact]
    public void ACancelledPutOrDeleteLeavesAnOpenUploadRunning()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24,m=1", B64(new byte[] { 1, 2, 3 }));
        rig.CancelledApc("a=d,d=A", "AAAA");
        rig.CancelledApc("a=p,i=1", "AAAA");
        Assert.True(rig.Controller.HasOpenTransfer);

        rig.Apc("m=0", B64(new byte[] { 4, 5, 6 }));
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(1, rig.Store.Count);
    }

    [Fact]
    public void ACompletedPutEndsAnOpenUpload()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Apc("i=2,s=2,v=1,f=24,m=1", B64(new byte[] { 1, 2, 3 }));
        Assert.True(rig.Controller.HasOpenTransfer);
        rig.Replies.Clear();

        rig.Apc("a=p,i=1");
        Assert.False(rig.Controller.HasOpenTransfer);
        rig.Apc("m=0", B64(new byte[] { 4, 5, 6 }));       // the rest of the abandoned upload is dropped
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Null(rig.Store.FindByClientId(2));
    }

    [Fact]
    public void ThePutReplyIsQueuedBeforeAFollowingDa1Reply()
    {
        var replies = new List<string>();
        using var adapter = KittyTransferTests.NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.ASCII.GetBytes(
            "\u001b_Gi=1,s=1,v=1,f=24;AAAA\u001b\\" + "\u001b_Ga=p,i=1,p=2\u001b\\" + "\u001b[c"));

        Assert.Equal(new[] { Ok("i=1"), Ok("i=1,p=2"), "\u001b[?1;0c" }, replies);
    }

    [Fact]
    public void PutWithAnExistingPlacementIdReplacesThePlacement()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Host.CursorRow = 2;
        rig.Host.CursorCol = 3;
        rig.Apc("a=p,i=1,p=4,c=2,r=1,C=1");
        rig.Host.CursorRow = 5;
        rig.Host.CursorCol = 0;
        rig.Apc("a=p,i=1,p=4,c=3,r=2,C=1");

        var placement = Assert.Single(rig.Store.Placements);
        Assert.Equal((5L, 0, 3, 2), (placement.AnchorRow, placement.AnchorCol, placement.EffCols, placement.EffRows));
        Assert.Equal(new[] { Ok("i=1"), Ok("i=1,p=4"), Ok("i=1,p=4") }, rig.Replies);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void ValidateInvariantsDetectsDanglingReflowAnchorsAndEmptyGeometry()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Img40x20(store), "p=1");
        store.ValidateInvariants();

        store.CaptureAnchors(0, 10, (row, col) => 0);
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
        store.RebaseAnchors(0, 10, (int slot, out long row, out int col) => { row = 0; col = 0; return true; });
        store.ValidateInvariants();

        p.Geometry = default;
        Assert.Throws<InvalidOperationException>(store.ValidateInvariants);
    }
}
