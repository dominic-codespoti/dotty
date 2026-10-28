using System;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>Capture of the visible placements into <see cref="KittyRenderSnapshot"/>: viewport rules, UV/offset math, leases.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittySnapshotTests
{
    private const string Esc = "\u001b";

    private static void Close(double expected, double actual) => Assert.Equal(expected, actual, 5);

    private static KittyRenderPlacement[] Capture(KittyImageStore store, int rows, int scrollOffset = 0)
    {
        using var snapshot = new KittyRenderSnapshot();
        store.CaptureVisible(snapshot, rows, scrollOffset);
        return snapshot.Placements.ToArray();
    }

    // ------------------------------------------------------------------------------------------- viewport

    [Fact]
    public void MultiRowPlacementsAreIncludedWhileAnyRowIsOnScreen()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "c=1,r=3,p=1", anchorRow: -3);   // rows -3..-1: wholly above
        store.Place(image, "c=1,r=3,p=2", anchorRow: -2);   // rows -2..0: row 0 shows
        store.Place(image, "c=1,r=3,p=3", anchorRow: 9);    // rows 9..11: row 9 shows
        store.Place(image, "c=1,r=3,p=4", anchorRow: 10);   // rows 10..: wholly below

        var visible = Capture(store, 10);
        Assert.Equal(new[] { -2, 9 }, visible.Select(p => p.ViewRow).OrderBy(r => r));
        Assert.Equal(new uint[] { 2, 3 }, visible.Select(p => p.PlacementId).OrderBy(i => i));
    }

    [Fact]
    public void SingleRowBoundaries()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        foreach (int row in new[] { -1, 0, 9, 10 })
            store.Place(image, "c=1,r=1", anchorRow: row);
        Assert.Equal(new[] { 0, 9 }, Capture(store, 10).Select(p => p.ViewRow).OrderBy(r => r));
    }

    [Fact]
    public void ScrollOffsetShiftsTheViewportIntoTheHistory()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "c=1,r=3,p=1", anchorRow: -3);
        store.Place(image, "c=1,r=3,p=2", anchorRow: -2);
        store.Place(image, "c=1,r=3,p=3", anchorRow: 9);
        store.Place(image, "c=1,r=3,p=4", anchorRow: 10);

        // Scrolled back by 3: absolute row -3 is on screen row 0; rows 9 and 10 are pushed off the bottom.
        var scrolled = Capture(store, 10, 3);
        Assert.Equal(new[] { 0, 1 }, scrolled.Select(p => p.ViewRow).OrderBy(r => r));
        Assert.Equal(new uint[] { 1, 2 }, scrolled.Select(p => p.PlacementId).OrderBy(i => i));

        // Offset 1: row 9 -> 10 is just off the bottom; row -3 -> -2 is still partly visible.
        var one = Capture(store, 10, 1);
        Assert.Equal(new uint[] { 1, 2 }, one.Select(p => p.PlacementId).OrderBy(i => i));
        Assert.Equal(-2, one.Single(p => p.PlacementId == 1).ViewRow);
        Assert.Equal(-1, one.Single(p => p.PlacementId == 2).ViewRow);
    }

    [Fact]
    public void TheViewRowIsRelativeToTheViewTop()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.OnRingRotatedUp(1_000, 5_000);
        store.Place(image, "c=1,r=1", anchorRow: 1_003, anchorCol: 6);
        var p = Assert.Single(Capture(store, 10));
        Assert.Equal((3, 6), (p.ViewRow, p.Col));
    }

    [Fact]
    public void AScrollOffsetFarBeyondTheHistoryNeverOverflowsAndShowsNothing()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "c=1,r=1", anchorRow: -3);
        store.Place(image, "c=1,r=1", anchorRow: 4);
        Assert.Empty(Capture(store, 10, int.MaxValue));
        Assert.Equal(new[] { 6 }, Capture(store, 10, 2).Select(p => p.ViewRow));
    }

    [Fact]
    public void EmptyStoresAndDegenerateViewportsAppendNothing()
    {
        using var store = new KittyImageStore(1_000_000);
        using var snapshot = new KittyRenderSnapshot();
        Assert.Equal(0, store.CaptureVisible(snapshot, 10, 0));
        var image = store.AddImage(40, 20, 1);
        store.Place(image);
        Assert.Equal(0, store.CaptureVisible(snapshot, 0, 0));
        Assert.Equal(0, store.CaptureVisible(snapshot, -3, 0));
        Assert.Equal(0, snapshot.Placements.Length);
        Assert.Throws<ArgumentNullException>(() => store.CaptureVisible(null!, 10, 0));
    }

    [Fact]
    public void OnlyNormalPlacementsAreCaptured()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "U=1,c=2,r=2,p=1");
        store.Place(image, "c=2,r=2,p=2");
        using var snapshot = new KittyRenderSnapshot();
        Assert.Equal(1, store.CaptureVisible(snapshot, 10, 0));
        Assert.Equal(2u, snapshot.Placements[0].PlacementId);
    }

    [Fact]
    public void CaptureAppendsToWhatIsAlreadyThere()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image);
        using var snapshot = new KittyRenderSnapshot();
        store.CaptureVisible(snapshot, 10, 0);
        store.CaptureVisible(snapshot, 10, 0);
        Assert.Equal(2, snapshot.Placements.Length);
    }

    // ------------------------------------------------------------------------------------------ fields

    [Fact]
    public void WholeImagePlacementCoversTheFullTextureRange()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "p=7,z=-3,c=2,r=2", anchorRow: 1, anchorCol: 4);
        var p = Assert.Single(Capture(store, 10));
        Assert.Equal((1, 4, 2, 2), (p.ViewRow, p.Col, p.Cols, p.Rows));
        Close(0, p.U0); Close(0, p.V0); Close(1, p.U1); Close(1, p.V1);
        Assert.Equal((-3, 1u, 7u), (p.Z, p.ImageId, p.PlacementId));
        Assert.False(p.NativeSize);
        Assert.Equal(0, p.NativeW);
        Assert.Equal(0, p.NativeH);
        Assert.Equal((8, 16), (p.BasisCellW, p.BasisCellH));
        Assert.Same(image.Root, p.Pixels);
        Assert.Equal(image.Root.Serial, p.ImageSerial);
    }

    [Fact]
    public void CroppedSourceBecomesNormalisedUvAndOffsetsBecomeCellFractions()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "x=10,y=5,w=20,h=10,X=4,Y=8");
        var p = Assert.Single(Capture(store, 10));
        Close(0.25, p.U0); Close(0.25, p.V0); Close(0.75, p.U1); Close(0.75, p.V1);
        Close(0.5, p.OffXCells);  // 4 px of an 8 px cell
        Close(0.5, p.OffYCells);  // 8 px of a 16 px cell
        Assert.Equal((3, 2), (p.Cols, p.Rows));            // ceil(24/8), ceil(18/16)
        Assert.True(p.NativeSize);
        Assert.Equal((20, 10), (p.NativeW, p.NativeH));    // the cropped source at 1:1
    }

    [Fact]
    public void OffsetsAreFractionsOfTheCellSizeTheyWereResolvedAt()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        store.Place(image, "X=3,Y=4");
        store.OnCellPixelSizeChanged(16, 32);
        var p = Assert.Single(Capture(store, 10));
        Close(3.0 / 16, p.OffXCells);
        Close(4.0 / 32, p.OffYCells);
        Assert.Equal((16, 32), (p.BasisCellW, p.BasisCellH));
    }

    [Fact]
    public void ACroppedPlacementCapturesTheCroppedSourceRectangle()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(16, 48, 1);
        store.Place(image, "c=2,r=3", anchorRow: 2);
        store.OnRegionScrolled(2, 7, 1, 10);              // loses the top row: source y 16..48
        var p = Assert.Single(Capture(store, 10));
        Assert.Equal((2, 2), (p.ViewRow, p.Rows));
        Close(0, p.U0); Close(1, p.U1);
        Close(16.0 / 48, p.V0); Close(1, p.V1);
    }

    [Fact]
    public void ACroppedNativePlacementCapturesItsCroppedNativeSizeAndConsumedOffset()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(16, 48, 1);
        store.Place(image, "Y=8", anchorRow: 2);          // native 16x48 px with an 8 px offset: 2 x 4 cells
        store.OnRegionScrolled(2, 7, 1, 10);              // the top row goes: 16 - 8 = 8 px of the image with it
        var p = Assert.Single(Capture(store, 10));
        Assert.Equal((2, 2, 3), (p.ViewRow, p.Cols, p.Rows));
        Assert.True(p.NativeSize);
        Assert.Equal((16, 40), (p.NativeW, p.NativeH));
        Close(0, p.U0); Close(1, p.U1);
        Close(8.0 / 48, p.V0); Close(1, p.V1);
        Close(0, p.OffYCells);
    }

    [Fact]
    public void CaptureDoesNotTouchTheImageAccessTime()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1, now: 5);
        store.Place(image);
        Capture(store, 10);
        Assert.Equal(5, image.LastAccessTicks);
        Assert.Equal(1, store.PlacementCount);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------------ leases

    [Fact]
    public void ASnapshotKeepsDeletedPixelsAliveUntilItIsDisposed()
    {
        var baseline = new GraphicsBaseline();
        using (var store = new KittyImageStore(1_000_000))
        {
            var image = store.AddImage(40, 20, 1);
            store.Place(image);
            store.Place(image, "p=2");
            var snapshot = new KittyRenderSnapshot();
            Assert.Equal(2, store.CaptureVisible(snapshot, 10, 0));

            store.Delete(image);                       // the store lets go ...
            Assert.Equal(40 * 20 * 4, baseline.LiveBytesDelta); // ... the snapshot's two leases keep the buffer
            Assert.Equal(1, baseline.LiveCountDelta);

            snapshot.Dispose();
            Assert.Equal(0, baseline.LiveBytesDelta);
            snapshot.Dispose();                        // idempotent
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void DisposingTheSnapshotLeavesLivePixelsAlone()
    {
        var baseline = new GraphicsBaseline();
        using (var store = new KittyImageStore(1_000_000))
        {
            var image = store.AddImage(40, 20, 1);
            store.Place(image);
            Capture(store, 10);
            Assert.Equal(40 * 20 * 4, baseline.LiveBytesDelta);
            Assert.False(image.IsDeleted);
        }
        baseline.AssertRestored();
    }

    // ---------------------------------------------------------------------- through the terminal buffer

    private sealed class Term : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(6, 10);
        public BasicAnsiParser Parser { get; }

        public Term()
        {
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));
        public void Apc(string control, string payload = "") => Parser.Feed(ApcBytes(control, payload));
        public RenderSnapshot Capture(int scrollOffset = 0) => Adapter.Buffer.CaptureRenderSnapshotVisible(0, -1, scrollOffset);
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void WithoutPlacementsTheSnapshotCarriesNoGraphics()
    {
        using var t = new Term();
        using (var s = t.Capture())
            Assert.Null(s.Graphics);

        t.Apc("i=1,s=1,v=1,f=24", "AAAA");            // an image without a placement is not visible either
        using (var s = t.Capture())
            Assert.Null(s.Graphics);
    }

    [Fact]
    public void PlacedImagesAppearInTheSnapshotAtTheirCursorCell()
    {
        using var t = new Term();
        t.Feed(Esc + "[2;3H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=3,r=2", "AAAA");
        using var s = t.Capture();
        Assert.NotNull(s.Graphics);
        var p = Assert.Single(s.Graphics!.Placements.ToArray());
        Assert.Equal((1, 2, 3, 2), (p.ViewRow, p.Col, p.Cols, p.Rows));
        Assert.Equal(1u, p.ImageId);
        Assert.False(p.NativeSize);
    }

    [Fact]
    public void TheSnapshotHonoursTheScrollOffset()
    {
        using var t = new Term();
        t.Feed(Esc + "[2;3H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=3,r=2,C=1", "AAAA");
        using (var s = t.Capture(2))
            Assert.Equal(3, Assert.Single(s.Graphics!.Placements.ToArray()).ViewRow);
        using (var s = t.Capture(5))                  // rows 1-2 -> 6-7 on a 6-row screen: off the bottom
            Assert.Empty(s.Graphics!.Placements.ToArray());
    }

    [Fact]
    public void TheAlternateScreenCapturesItsOwnStore()
    {
        using var t = new Term();
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1,C=1", "AAAA");
        using (var s = t.Capture())
            Assert.Single(s.Graphics!.Placements.ToArray());

        t.Feed(Esc + "[?1049h");
        using (var s = t.Capture())
            Assert.Null(s.Graphics);                  // nothing on the alternate screen (and no graphics object)

        t.Feed(Esc + "[5;1H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=2,r=1,C=1", "AAAA");
        using (var s = t.Capture())
        {
            var p = Assert.Single(s.Graphics!.Placements.ToArray());
            Assert.Equal((4, 0, 2), (p.ViewRow, p.Col, p.Cols));
        }

        t.Feed(Esc + "[?1049l");
        using (var s = t.Capture())
        {
            var p = Assert.Single(s.Graphics!.Placements.ToArray());
            Assert.Equal((0, 0, 1), (p.ViewRow, p.Col, p.Cols));
        }
    }

    [Fact]
    public void SnapshotLeasesOutliveADeleteUntilTheSnapshotIsDisposed()
    {
        var baseline = new GraphicsBaseline();
        var t = new Term();
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", "AAAA");
        var s = t.Capture();
        Assert.Single(s.Graphics!.Placements.ToArray());

        t.Apc("a=d,d=I,i=1");                         // the image is gone from the store ...
        Assert.True(baseline.LiveBytesDelta > 0);     // ... but the capture still holds its pixels
        Assert.Equal(1, s.Graphics!.Placements.Length);

        s.Dispose();
        Assert.Equal(0, baseline.LiveBytesDelta);
        Assert.Null(s.Graphics);
        t.Dispose();
        baseline.AssertRestored();
    }

    [Fact]
    public void AReusedPooledSnapshotStartsFromAnEmptyGraphicsList()
    {
        using var t = new Term();
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1,C=1", "AAAA");
        t.Capture().Dispose();                        // returns the pooled snapshot with one placement captured
        t.Apc("a=d,d=A");
        using var s = t.Capture();
        Assert.Null(s.Graphics);
    }

    // ------------------------------------------------------------------ virtual placements and their children

    [Fact]
    public void VirtualPlacementsAreCapturedSortedByImageThenPlacementWhateverTheCreationOrder()
    {
        using var store = new KittyImageStore(10_000_000);
        for (uint id = 20; id >= 1; id--)
        {
            var image = store.AddImage(16, 32, id);
            foreach (uint p in new uint[] { 3, 1, 2 })
                store.Place(image, $"U=1,c=1,r=1,p={p}");
        }

        using var snapshot = new KittyRenderSnapshot();
        Assert.Equal(0, store.CaptureVisible(snapshot, 10, 0));

        var keys = snapshot.Virtuals.ToArray().Select(v => (v.ImageId, v.PlacementId)).ToArray();
        Assert.Equal(60, keys.Length);
        Assert.Equal(keys.OrderBy(k => k.ImageId).ThenBy(k => k.PlacementId).ToArray(), keys);
        Assert.Equal(((1u, 1u), (20u, 3u)), (keys[0], keys[^1]));
        Assert.Equal(60, keys.Distinct().Count());
    }

    [Fact]
    public void AVirtualPlacementOfAnImageWithoutAClientIdIsNotCaptured()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 0, 5);
        store.Place(image, "U=1,c=1,r=1");
        Assert.Equal(1, store.PlacementCount);

        using var snapshot = new KittyRenderSnapshot();
        store.CaptureVisible(snapshot, 10, 0);

        Assert.True(snapshot.Virtuals.IsEmpty);
    }

    [Fact]
    public void VirtualPlacementsAndTheirChildrenHoldLeasesAndResetReleasesThem()
    {
        var baseline = new GraphicsBaseline();
        using (var store = new KittyImageStore(1_000_000))
        {
            var parent = store.AddImage(40, 20, 1);
            store.Place(parent, "U=1,c=2,r=2,p=1");
            var child = store.AddImage(16, 16, 2);
            Assert.Equal(KittyStatus.Ok, store.TryPlaceRelative(child, Parse("P=1,Q=1"), 8, 16, out _));
            var snapshot = new KittyRenderSnapshot();
            store.CaptureVisible(snapshot, 10, 0);
            Assert.Single(snapshot.Virtuals.ToArray());
            Assert.Single(snapshot.VirtualChildren.ToArray());

            store.Clear();                                           // the store lets go of both images ...
            Assert.Equal(40 * 20 * 4 + 16 * 16 * 4, baseline.LiveBytesDelta);   // ... the snapshot's leases keep them

            snapshot.Reset();
            Assert.Equal(0, baseline.LiveBytesDelta);
            Assert.True(snapshot.Virtuals.IsEmpty);
            Assert.True(snapshot.VirtualChildren.IsEmpty);
            snapshot.Dispose();                                      // idempotent
        }
        baseline.AssertRestored();
    }
}
