using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Relative placements (<c>P/Q/H/V</c>): validation errors with their exact reply bytes, the chain-depth boundary,
/// cycles, position resolution at capture time (the parent link is stored, never an absolute anchor), lifetime
/// (cascade on every way a parent can disappear), replacement of a parent, and the cursor staying put.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyRelativePlacementTests
{
    private const string NoParent = "ENOPARENT:Parent placement not found";
    private const string TooDeep = "ETOODEEP:Relative placement chain too deep";
    private const string Cycle = "ECYCLE:Relative placement cycle";
    private const string SelfParent = "EINVAL:Placement cannot be its own parent";
    private const string VirtualRelative = "EINVAL:Virtual placement cannot be relative";

    // ---------------------------------------------------------------------------------------------- helpers

    private static KittyRenderPlacement[] Capture(KittyImageStore store, int rows = 24, int scrollOffset = 0)
    {
        using var snapshot = new KittyRenderSnapshot();
        store.CaptureVisible(snapshot, rows, scrollOffset);
        return snapshot.Placements.ToArray();
    }

    private static KittyRenderPlacement CaptureOf(KittyImageStore store, uint imageId, int rows = 24, int scrollOffset = 0) =>
        Assert.Single(Capture(store, rows, scrollOffset), p => p.ImageId == imageId);

    private static KittyStatus Rel(KittyImageStore store, KittyImage image, string keys, out KittyPlacement? placement) =>
        store.TryPlaceRelative(image, Parse(keys), 8, 16, out placement);

    private static KittyPlacement RelOk(KittyImageStore store, KittyImage image, string keys)
    {
        var status = Rel(store, image, keys, out var placement);
        Assert.Equal(KittyStatus.Ok, status);
        Assert.NotNull(placement);
        return placement!;
    }

    /// <summary>A store with images 1 and 2 (40x20 px: 5x2 cells at the 8x16 cell size).</summary>
    private static KittyImageStore TwoImageStore(out KittyImage a, out KittyImage b)
    {
        var store = new KittyImageStore(1_000_000);
        a = store.AddImage(40, 20, 1);
        b = store.AddImage(40, 20, 2);
        return store;
    }

    private static void Image(KittyRig rig, uint id) => rig.Apc($"i={id},s=1,v=1,f=24,q=2", "AAAA");

    private static KittyRig RigWithImages(int count)
    {
        var rig = new KittyRig();
        for (uint id = 1; id <= count; id++)
            Image(rig, id);
        rig.Replies.Clear();
        return rig;
    }

    // ------------------------------------------------------------------------------------- positioning

    [Theory]
    [InlineData(10, 4, 3, 2, 12, 7)]
    [InlineData(10, 4, -2, -3, 7, 2)]
    [InlineData(1, 1, -4, 0, 1, -3)]
    [InlineData(5, 0, 0, 0, 5, 0)]
    public void TheChildSitsAtTheParentsCellPlusHAndV(int parentRow, int parentCol, int h, int v, int row, int col)
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: parentRow, anchorCol: parentCol);
        RelOk(store, b, $"p=1,P=1,Q=1,H={h},V={v}");

        var child = CaptureOf(store, 2);
        Assert.Equal((row, col), (child.ViewRow, child.Col));
        store.ValidateInvariants();
    }

    [Fact]
    public void OffsetsAccumulateAlongTheChain()
    {
        using var store = new KittyImageStore(1_000_000);
        var images = Enumerable.Range(1, 4).Select(i => store.AddImage(40, 20, (uint)i)).ToArray();
        store.Place(images[0], "p=1", anchorRow: 2, anchorCol: 1);
        RelOk(store, images[1], "p=1,P=1,Q=1,H=1,V=1");
        RelOk(store, images[2], "p=1,P=2,Q=1,H=2,V=-1");
        RelOk(store, images[3], "p=1,P=3,Q=1,H=-1,V=3");

        Assert.Equal((2 + 1 - 1 + 3, 1 + 1 + 2 - 1), (CaptureOf(store, 4).ViewRow, CaptureOf(store, 4).Col));
        Assert.Equal((2 + 1 - 1, 1 + 1 + 2), (CaptureOf(store, 3).ViewRow, CaptureOf(store, 3).Col));
    }

    [Fact]
    public void ARelativePlacementStoresNoAnchorAndKeepsItsOwnGeometry()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 10, anchorCol: 4);
        var child = RelOk(store, b, "p=1,P=1,Q=1,H=3,V=2,c=2,r=1,z=-3");

        Assert.Equal(PlacementKind.Relative, child.Kind);
        Assert.Equal((0L, 0), (child.AnchorRow, child.AnchorCol));
        var drawn = CaptureOf(store, 2);
        Assert.Equal((2, 1, -3), (drawn.Cols, drawn.Rows, drawn.Z));
    }

    [Theory]
    [InlineData(-7, false)]   // rows -2..-1: wholly above
    [InlineData(-6, true)]    // rows -1..0: row 0 shows
    [InlineData(4, true)]     // rows 9..10: row 9 shows
    [InlineData(5, false)]    // rows 10..11: wholly below
    public void VisibilityOfAChildFollowsTheViewportRules(int v, bool visible)
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 5);
        RelOk(store, b, $"p=1,P=1,Q=1,V={v}");

        Assert.Equal(visible, Capture(store, rows: 10).Any(p => p.ImageId == 2));
    }

    [Fact]
    public void AChildIsCapturedEvenWhenItsParentIsOffScreen()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.OnRingRotatedUp(12, 1000);
        store.Place(a, "p=1", anchorRow: 10);          // visible row -2: rows -2..-1, wholly in the history
        RelOk(store, b, "p=1,P=1,Q=1,V=3");

        var captured = Capture(store, rows: 10);
        Assert.Equal(1, Assert.Single(captured).ViewRow);
        Assert.Equal(2u, captured[0].ImageId);
    }

    [Fact]
    public void ScrollbackViewingShiftsParentAndChildTogether()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 4, anchorCol: 2);
        RelOk(store, b, "p=1,P=1,Q=1,H=1,V=2");

        var shifted = Capture(store, 24, scrollOffset: 3);
        Assert.Equal(7, shifted.Single(p => p.ImageId == 1).ViewRow);
        Assert.Equal(9, shifted.Single(p => p.ImageId == 2).ViewRow);
    }

    // ------------------------------------------------------------------------------ following the parent

    [Fact]
    public void AFullScreenScrollMovesTheChildOnlyThroughItsParent()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 10, anchorCol: 4);
        var child = RelOk(store, b, "p=1,P=1,Q=1,H=3,V=2");

        store.OnRingRotatedUp(3, 1000);

        Assert.Equal(7, CaptureOf(store, 1).ViewRow);
        Assert.Equal(9, CaptureOf(store, 2).ViewRow);
        Assert.Equal(7, CaptureOf(store, 2).Col);
        Assert.Equal(0, child.AnchorRow);   // never touched: no double move
        store.ValidateInvariants();
    }

    [Fact]
    public void AScrollRotatingBackMovesThemDownTogether()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 4);
        RelOk(store, b, "p=1,P=1,Q=1,V=2");

        store.OnRingRotatedDown(2, 24);

        Assert.Equal(6, CaptureOf(store, 1).ViewRow);             // ViewTop is -2 now: 4 - (-2)
        Assert.Equal(8, CaptureOf(store, 2).ViewRow);
    }

    [Fact]
    public void ARegionScrollMovesTheParentOnceAndTheChildFollows()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 5);                      // rows 5..6, wholly inside the region 2..20
        var child = RelOk(store, b, "p=1,P=1,Q=1,V=1");

        store.OnRegionScrolled(2, 20, 1, 24);

        Assert.Equal(4, CaptureOf(store, 1).ViewRow);
        Assert.Equal(5, CaptureOf(store, 2).ViewRow);             // 4 + V; a second move would give 4 or 3
        Assert.Equal(0, child.AnchorRow);
    }

    [Fact]
    public void AColumnEditCropsTheParentAndRelativeChildFollowsItsNewGeometry()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1,c=2,r=2", anchorRow: 5, anchorCol: 2);
        var child = RelOk(store, b, "p=1,P=1,Q=1,H=1,V=0");
        int childColumnBefore = CaptureOf(store, 2).Col;

        Assert.Equal(1, store.OnColumnsScrolled(2, 20, 2, 20, 1, 24));

        var parent = CaptureOf(store, 1);
        var drawnChild = CaptureOf(store, 2);
        Assert.Equal((2, 1), (parent.Col, parent.Cols));
        Assert.Equal(childColumnBefore, drawnChild.Col);
        Assert.Equal(1, store.OnColumnsScrolled(2, 20, 2, 20, -1, 24));
        Assert.Equal(3, CaptureOf(store, 1).Col);
        Assert.Equal(childColumnBefore + 1, CaptureOf(store, 2).Col);
        Assert.Equal(1, CaptureOf(store, 1).Cols);
        Assert.Equal(PlacementKind.Relative, child.Kind);
        Assert.Equal(0, child.AnchorCol);
    }

    [Fact]
    public void ACroppedParentLeavesTheChildGeometryAlone()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 2);                      // rows 2..3 at the region's top margin
        var child = RelOk(store, b, "p=1,P=1,Q=1,V=1");

        store.OnRegionScrolled(2, 20, 1, 24);                     // the parent loses its top row

        Assert.Equal(1, CaptureOf(store, 1).Rows);
        Assert.Equal(2, CaptureOf(store, 1).ViewRow);
        Assert.Equal(3, CaptureOf(store, 2).ViewRow);
        Assert.Equal(2, child.EffRows);
    }

    [Fact]
    public void AParentScrolledOutOfTheRegionTakesItsChildrenWithIt()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 2);
        RelOk(store, b, "p=1,P=1,Q=1,V=1");

        store.OnRegionScrolled(2, 20, 2, 24);                     // both of its rows scroll off the margin

        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void ARegionScrollNeverDeletesAChildBecauseItsOwnCellsLeftTheRegion()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 10);
        RelOk(store, b, "p=1,P=1,Q=1,V=-9");                      // the child's cells are above the region, outside it

        store.OnRegionScrolled(5, 20, 3, 24);

        Assert.Equal(2, store.PlacementCount);
    }

    // --------------------------------------------------------------------------- lifetime and cascades

    [Fact]
    public void PruningAParentByHistoryCapacityPrunesItsChildren()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 0);
        RelOk(store, b, "p=1,P=1,Q=1,V=30");                      // far below: still "visible" through its own cells

        store.OnRingRotatedUp(1000, 10);                          // the parent's last row falls out of the ring

        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void PushingAParentOffTheBottomDeletesItsChildren()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 22);
        RelOk(store, b, "p=1,P=1,Q=1,V=-2");

        store.OnRingRotatedDown(5, 24);                           // anchor 22 >= -5 + 24

        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void HistoryInvalidationAndResetCascade()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 0);
        RelOk(store, b, "p=1,P=1,Q=1");
        store.OnRingRotatedUp(10, 1000);                          // parent now wholly in the history

        store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, store.ViewTop);

        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();

        store.Place(a, "p=1");
        RelOk(store, b, "p=1,P=1,Q=1");
        Assert.Equal(2, store.OnHistoryInvalidated(HistoryDiscardReason.Reset, 0));
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void DeletingTheParentImageDeletesTheChildrenButNotTheirImages()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1");
        RelOk(store, b, "p=1,P=1,Q=1");

        Assert.True(store.Delete(a));

        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(0, b.PlacementCount);
        Assert.False(b.IsDeleted);
        store.ValidateInvariants();
    }

    [Fact]
    public void ReflowRebasingMovesTheParentAndTheChildFollows()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 4, anchorCol: 2);
        RelOk(store, b, "p=1,P=1,Q=1,H=1,V=1");

        var anchors = new List<(int Row, int Col)>();
        Assert.Equal(1, store.CaptureAnchors(0, 24, (row, col) =>
        {
            anchors.Add((row, col));
            return anchors.Count - 1;
        }));
        Assert.Equal(0, store.RebaseAnchors(5, 24, (int slot, out long row, out int column) =>
        {
            row = 5 + anchors[slot].Row + 3;
            column = anchors[slot].Col + 1;
            return true;
        }));

        Assert.Equal(7, CaptureOf(store, 1).ViewRow);
        Assert.Equal(3, CaptureOf(store, 1).Col);
        Assert.Equal(8, CaptureOf(store, 2).ViewRow);
        Assert.Equal(4, CaptureOf(store, 2).Col);
        store.ValidateInvariants();
    }

    [Fact]
    public void AParentTrimmedByReflowTakesItsChildren()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 4);
        RelOk(store, b, "p=1,P=1,Q=1");

        store.CaptureAnchors(0, 24, (row, col) => 0);
        store.RebaseAnchors(0, 24, (int slot, out long row, out int column) =>
        {
            row = 0;
            column = 0;
            return false;                                         // its line was trimmed away
        });

        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void ACellSizeChangeReResolvesChildrenWithoutMovingThem()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1", anchorRow: 6);
        var child = RelOk(store, b, "p=1,P=1,Q=1,V=2");

        store.OnCellPixelSizeChanged(10, 20);

        Assert.Equal(4, child.EffCols);                           // 40 px / 10
        Assert.Equal(1, child.EffRows);                           // 20 px / 20
        Assert.Equal(8, CaptureOf(store, 2).ViewRow);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------ delete commands

    private static KittyRig ParentAndChild()
    {
        var rig = RigWithImages(2);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Apc("a=p,i=2,p=1,P=1,Q=1,H=1,V=1");
        rig.Replies.Clear();
        Assert.Equal(2, rig.Store.PlacementCount);
        return rig;
    }

    [Fact]
    public void LowerCaseDeleteOfTheParentRemovesTheChildPlacementOnly()
    {
        using var rig = ParentAndChild();

        rig.Apc("a=d,d=i,i=1");

        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.NotNull(rig.Store.FindByClientId(1));
        Assert.NotNull(rig.Store.FindByClientId(2));
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void UpperCaseDeleteOfTheParentAlsoFreesTheChildImageLeftWithoutPlacements()
    {
        using var rig = ParentAndChild();

        rig.Apc("a=d,d=I,i=1");

        Assert.Equal(0, rig.Store.Count);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void UpperCaseDeleteOfTheParentKeepsAChildImageThatStillHasAnotherPlacement()
    {
        using var rig = ParentAndChild();
        rig.Apc("a=p,i=2,p=7,C=1");

        rig.Apc("a=d,d=I,i=1");

        Assert.Null(rig.Store.FindByClientId(1));
        var survivor = rig.Store.FindByClientId(2);
        Assert.NotNull(survivor);
        Assert.Equal(7u, Assert.Single(rig.Store.Placements).ClientId);
    }

    [Fact]
    public void DeleteAllVisibleCascadesFromTheNormalParent()
    {
        using var rig = ParentAndChild();

        rig.Apc("a=d,d=a");
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.Equal(2, rig.Store.Count);

        using var upper = ParentAndChild();
        upper.Apc("a=d,d=A");
        Assert.Equal(0, upper.Store.Count);
    }

    [Fact]
    public void DeletingJustTheChildLeavesTheParentWithoutChildren()
    {
        using var rig = ParentAndChild();

        rig.Apc("a=d,d=i,i=2");

        var parent = Assert.Single(rig.Store.Placements);
        Assert.Equal(1u, parent.Image.ClientId);
        Assert.Null(parent.Children);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void ADeleteByCellFindsOnlyNormalParentsAndCascades()
    {
        using var rig = ParentAndChild();

        rig.Apc("a=d,d=c");                                       // cursor cell (0,0): the parent

        Assert.Equal(0, rig.Store.PlacementCount);
    }

    [Fact]
    public void AnAnonymousChildImageIsFreedWithItsParent()
    {
        using var rig = RigWithImages(1);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();                                     // the a=p with an id answers OK
        rig.Apc("a=T,f=24,s=1,v=1,P=1,Q=1,H=1", "AAAA");        // no id: nothing to answer, nothing can address it
        Assert.Empty(rig.Replies);
        Assert.Equal(2, rig.Store.Count);
        Assert.Equal(2, rig.Store.PlacementCount);

        rig.Apc("a=d,d=i,i=1");

        Assert.Equal(1, rig.Store.Count);
        Assert.Equal(0, rig.Store.PlacementCount);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void RetransmittingTheParentImageDropsItsChildren()
    {
        using var rig = ParentAndChild();

        rig.Apc("i=1,s=1,v=1,f=24,q=2", "AAAA");

        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.NotNull(rig.Store.FindByClientId(2));
        rig.Store.ValidateInvariants();
    }

    // -------------------------------------------------------------------------------- replacing a parent

    [Fact]
    public void ReplacingAParentPlacementKeepsItsChildrenAndMovesThem()
    {
        using var rig = ParentAndChild();
        rig.Host.CursorRow = 5;
        rig.Host.CursorCol = 3;

        rig.Apc("a=p,i=1,p=1,C=1");

        Assert.Equal(new[] { Ok("i=1,p=1") }, rig.Replies);
        var image1 = rig.Store.FindByClientId(1)!;
        var parent = rig.Store.FindPlacement(image1, 1)!;
        var child = Assert.Single(parent.Children!);
        Assert.Same(parent, child.Parent);
        Assert.Equal(2, rig.Store.PlacementCount);
        var drawn = CaptureOf(rig.Store, 2);
        Assert.Equal((6, 4), (drawn.ViewRow, drawn.Col));         // 5 + V=1, 3 + H=1
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void ReplacingAParentWithADifferentSizeKeepsTheOffsetsToo()
    {
        using var store = TwoImageStore(out var a, out var b);
        store.Place(a, "p=1,c=3,r=1", anchorRow: 2);
        RelOk(store, b, "p=1,P=1,Q=1,V=4");

        store.Place(a, "p=1,c=1,r=1", anchorRow: 9, anchorCol: 1);

        var drawn = CaptureOf(store, 2);
        Assert.Equal(13, drawn.ViewRow);
        Assert.Equal(1, drawn.Col);
    }

    [Fact]
    public void AParentCanBeReplacedByARelativePlacementAndStillKeepItsChildren()
    {
        using var store = new KittyImageStore(1_000_000);
        var images = Enumerable.Range(1, 3).Select(i => store.AddImage(40, 20, (uint)i)).ToArray();
        store.Place(images[0], "p=1", anchorRow: 7, anchorCol: 1);
        RelOk(store, images[1], "p=1,P=1,Q=1,H=2,V=2");           // child of image 1
        store.Place(images[2], "p=1", anchorRow: 1, anchorCol: 0);

        RelOk(store, images[0], "p=1,P=3,Q=1,H=1,V=1");           // image 1 now hangs off image 3

        Assert.Equal((2, 1), (CaptureOf(store, 1).ViewRow, CaptureOf(store, 1).Col));
        Assert.Equal((4, 3), (CaptureOf(store, 2).ViewRow, CaptureOf(store, 2).Col));
        store.ValidateInvariants();
    }

    [Fact]
    public void ReplacingAChildReplacesItAndKeepsItsOwnChildren()
    {
        using var store = new KittyImageStore(1_000_000);
        var images = Enumerable.Range(1, 3).Select(i => store.AddImage(40, 20, (uint)i)).ToArray();
        store.Place(images[0], "p=1", anchorRow: 2, anchorCol: 1);
        RelOk(store, images[1], "p=1,P=1,Q=1,H=1,V=1");
        RelOk(store, images[2], "p=1,P=2,Q=1,H=1,V=1");

        RelOk(store, images[1], "p=1,P=1,Q=1,H=5,V=0");

        Assert.Equal(3, store.PlacementCount);
        var parent = store.FindPlacement(images[0], 1)!;
        Assert.Single(parent.Children!);
        Assert.Equal((2, 6), (CaptureOf(store, 2).ViewRow, CaptureOf(store, 2).Col));
        Assert.Equal((3, 7), (CaptureOf(store, 3).ViewRow, CaptureOf(store, 3).Col));
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------- errors, exact bytes

    [Fact]
    public void ASuccessfulRelativePlacementAnswersOkAndNeverMovesTheCursor()
    {
        using var rig = RigWithImages(2);
        rig.Host.CursorRow = 4;
        rig.Host.CursorCol = 2;
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=2,p=1,P=1,Q=1,H=2,V=-1");                 // no C=1: a normal placement would move the cursor

        Assert.Equal(new[] { Ok("i=2,p=1") }, rig.Replies);
        Assert.Empty(rig.Host.CursorMoves);
        Assert.Equal((4, 2), (rig.Host.CursorRow, rig.Host.CursorCol));
        var drawn = CaptureOf(rig.Store, 2);
        Assert.Equal((3, 4), (drawn.ViewRow, drawn.Col));
    }

    [Fact]
    public void TransmitAndPutCanBeRelativeToo()
    {
        using var rig = RigWithImages(1);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=T,i=3,p=1,f=24,s=1,v=1,P=1,Q=1,H=2", "AAAA");

        Assert.Equal(new[] { Ok("i=3,p=1") }, rig.Replies);
        Assert.Empty(rig.Host.CursorMoves);
        Assert.Equal(PlacementKind.Relative, rig.Store.FindPlacement(rig.Store.FindByClientId(3)!, 1)!.Kind);
    }

    [Fact]
    public void ATransmitAndPutWithAMissingParentKeepsTheImageAndReportsTheError()
    {
        using var rig = RigWithImages(0);

        rig.Apc("a=T,i=3,p=1,f=24,s=1,v=1,P=9,Q=1", "AAAA");

        Assert.Equal(new[] { Err("i=3,p=1", NoParent) }, rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(3));
        Assert.Equal(0, rig.Store.PlacementCount);
    }

    [Theory]
    [InlineData("a=p,i=2,p=1,P=9,Q=1")]     // unknown parent image
    [InlineData("a=p,i=2,p=1,Q=1")]         // Q without P: image 0 does not exist
    [InlineData("a=p,i=2,p=1,P=3,Q=1")]     // image 3 exists but has no placement at all
    [InlineData("a=p,i=2,p=1,P=1,Q=9")]     // image 1 has no placement 9
    public void AMissingParentIsEnoparentAndCreatesNothing(string command)
    {
        using var rig = RigWithImages(3);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc(command);

        Assert.Equal(new[] { Err("i=2,p=1", NoParent) }, rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
        Assert.Empty(rig.Host.CursorMoves);
    }

    [Fact]
    public void APlacementCannotBeItsOwnParent()
    {
        using var rig = RigWithImages(1);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,p=1,P=1,Q=1");

        Assert.Equal(new[] { Err("i=1,p=1", SelfParent) }, rig.Replies);
        Assert.Equal(PlacementKind.Normal, Assert.Single(rig.Store.Placements).Kind);   // untouched
    }

    [Fact]
    public void NamingTheOwnImageWithoutAnyPlacementIdIsAlsoSelfParenting()
    {
        // No p and no Q: both ids are 0, so the placement would be its own parent (the same rule as p == Q).
        using var rig = RigWithImages(1);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,P=1");

        Assert.Equal(new[] { Err("i=1", SelfParent) }, rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
    }

    [Fact]
    public void ADifferentPlacementOfTheSameImageIsAValidParent()
    {
        using var rig = RigWithImages(1);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,p=2,P=1,Q=1,H=3");

        Assert.Equal(new[] { Ok("i=1,p=2") }, rig.Replies);
        Assert.Equal(PlacementKind.Relative, rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 2)!.Kind);
    }

    [Fact]
    public void AVirtualPlacementCannotBeRelative()
    {
        using var rig = RigWithImages(2);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=2,p=1,U=1,c=1,r=1,P=1,Q=1");

        Assert.Equal(new[] { Err("i=2,p=1", VirtualRelative) }, rig.Replies);
        Assert.Equal(1, rig.Store.PlacementCount);
    }

    [Fact]
    public void ACycleIsRejectedWhateverTheDistance()
    {
        using var rig = RigWithImages(3);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Apc("a=p,i=2,p=1,P=1,Q=1");
        rig.Apc("a=p,i=3,p=1,P=2,Q=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,p=1,P=3,Q=1");      // A under its grandchild
        rig.Apc("a=p,i=1,p=1,P=2,Q=1");      // A under its child
        rig.Apc("a=p,i=2,p=1,P=3,Q=1");      // B under its own child

        Assert.Equal(new[]
        {
            Err("i=1,p=1", Cycle), Err("i=1,p=1", Cycle), Err("i=2,p=1", Cycle),
        }, rig.Replies);
        Assert.Equal(PlacementKind.Normal, rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 1)!.Kind);
        rig.Store.ValidateInvariants();

        rig.Replies.Clear();
        rig.Apc("a=p,i=3,p=1,P=1,Q=1");      // moving a descendant up is fine
        Assert.Equal(new[] { Ok("i=3,p=1") }, rig.Replies);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void TheChainDepthLimitIsEightLinks()
    {
        using var rig = RigWithImages(10);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Replies.Clear();

        for (uint k = 2; k <= 9; k++)
            rig.Apc($"a=p,i={k},p=1,P={k - 1},Q=1,H=1,V=1");
        rig.Apc("a=p,i=10,p=1,P=9,Q=1,H=1,V=1");

        Assert.Equal(
            Enumerable.Range(2, 8).Select(k => Ok($"i={k},p=1")).Append(Err("i=10,p=1", TooDeep)).ToArray(),
            rig.Replies.ToArray());
        Assert.Equal(9, rig.Store.PlacementCount);
        var deepest = CaptureOf(rig.Store, 9);
        Assert.Equal((8, 8), (deepest.ViewRow, deepest.Col));     // eight links of (+1, +1)
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void MovingASubtreeUnderAnotherParentCountsItsHeightAgainstTheLimit()
    {
        using var rig = RigWithImages(11);
        rig.Apc("a=p,i=1,p=1,C=1");
        for (uint k = 2; k <= 9; k++)
            rig.Apc($"a=p,i={k},p=1,P={k - 1},Q=1,V=1");           // 8 links below image 1
        rig.Apc("a=p,i=11,p=1,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=1,p=1,P=11,Q=1");                          // 1 + 8 > 8
        Assert.Equal(new[] { Err("i=1,p=1", TooDeep) }, rig.Replies);
        Assert.Equal(PlacementKind.Normal, rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 1)!.Kind);

        rig.Apc("a=d,d=i,i=9");                                    // the subtree is one link shorter now
        rig.Replies.Clear();
        rig.Apc("a=p,i=1,p=1,P=11,Q=1");                          // 1 + 7 = 8: allowed
        Assert.Equal(new[] { Ok("i=1,p=1") }, rig.Replies);
        Assert.Equal(7, CaptureOf(rig.Store, 8).ViewRow);          // image 11 at row 0, then 7 links of V=1
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void QWithoutAnIdPicksTheOldestPlacementOfTheParentImage()
    {
        using var rig = RigWithImages(2);
        rig.Host.CursorRow = 1;
        rig.Apc("a=p,i=1,p=5,C=1");
        rig.Host.CursorRow = 9;
        rig.Apc("a=p,i=1,p=2,C=1");
        rig.Replies.Clear();

        rig.Apc("a=p,i=2,p=1,P=1,V=1");

        Assert.Equal(new[] { Ok("i=2,p=1") }, rig.Replies);
        var child = rig.Store.FindPlacement(rig.Store.FindByClientId(2)!, 1)!;
        Assert.Equal(5u, child.Parent!.ClientId);
        Assert.Equal(2, CaptureOf(rig.Store, 2).ViewRow);
    }

    [Fact]
    public void ARelativePlacementWithoutAPlacementIdIsAnonymousButStillCascades()
    {
        using var rig = RigWithImages(2);
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Apc("a=p,i=2,P=1,Q=1");
        Assert.Equal(2, rig.Store.PlacementCount);
        Assert.Equal(0u, rig.Store.FindByClientId(2)!.PlacementList![0].ClientId);

        rig.Apc("a=d,d=i,i=1,p=1");

        Assert.Equal(0, rig.Store.PlacementCount);
    }

    // ----------------------------------------------------------------------------------------- the cap

    [Fact]
    public void ThePlacementCapNeverEvictsTheParentOfThePlacementBeingCreated()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 2 };
        var a = store.AddImage(40, 20, 1);
        var b = store.AddImage(40, 20, 2);
        store.OnRingRotatedUp(10, 1000);
        store.Place(a, "p=1", anchorRow: 0);                      // history
        var keep = store.Place(a, "p=2", anchorRow: 3);           // history

        var child = RelOk(store, b, "p=1,P=1,Q=2,V=1");

        Assert.Null(store.FindPlacement(a, 1));                   // the older history placement went
        Assert.Same(keep, store.FindPlacement(a, 2));
        Assert.Same(keep, child.Parent);
        store.ValidateInvariants();
    }

    [Fact]
    public void WhenOnlyTheParentCouldBeEvictedTheCapRefusesTheChild()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 1 };
        var a = store.AddImage(40, 20, 1);
        var b = store.AddImage(40, 20, 2);
        store.OnRingRotatedUp(10, 1000);
        store.Place(a, "p=1", anchorRow: 3);

        Assert.Equal(KittyStatus.TooManyPlacements, Rel(store, b, "p=1,P=1,Q=1", out var child));

        Assert.Null(child);
        Assert.NotNull(store.FindPlacement(a, 1));
        Assert.Equal(1, store.PlacementCount);
    }

    [Fact]
    public void EvictingAHistoryParentForTheCapTakesItsChildrenToo()
    {
        using var store = new KittyImageStore(1_000_000) { MaxPlacements = 2 };
        var a = store.AddImage(40, 20, 1);
        var b = store.AddImage(40, 20, 2);
        var c = store.AddImage(40, 20, 3);
        store.OnRingRotatedUp(10, 1000);
        store.Place(a, "p=1", anchorRow: 3);
        RelOk(store, b, "p=1,P=1,Q=1,V=20");

        store.Place(c, "p=1", anchorRow: 12);

        Assert.Equal(1, store.PlacementCount);
        Assert.Equal(3u, Assert.Single(store.Placements).Image.ClientId);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------- virtual parents

    [Fact]
    public void ARelativePlacementMayHangOffAVirtualPlacementAndIsLeftForTheResolver()
    {
        using var store = TwoImageStore(out var a, out var b);
        var virtualParent = store.Place(a, "U=1,c=2,r=2,p=1");
        var child = RelOk(store, b, "p=1,P=1,Q=1,H=1,V=2");
        Assert.Same(virtualParent, child.Parent);

        using var snapshot = new KittyRenderSnapshot();
        store.CaptureVisible(snapshot, 24, 0);

        Assert.Empty(snapshot.Placements.ToArray());
        var virtualChild = Assert.Single(snapshot.VirtualChildren.ToArray());
        Assert.Equal(virtualParent.InternalId, virtualChild.VirtualInternalId);
        Assert.Equal((2, 1), (virtualChild.Placement.ViewRow, virtualChild.Placement.Col));
        Assert.Equal(virtualParent.InternalId, Assert.Single(snapshot.Virtuals.ToArray()).InternalId);
    }

    [Fact]
    public void DeletingAVirtualParentRemovesItsChildren()
    {
        using var rig = RigWithImages(2);
        rig.Apc("a=p,i=1,p=1,U=1,c=2,r=2");
        rig.Apc("a=p,i=2,p=1,P=1,Q=1");
        Assert.Equal(2, rig.Store.PlacementCount);

        rig.Apc("a=d,d=i,i=1,p=1");

        Assert.Equal(0, rig.Store.PlacementCount);
    }
}
