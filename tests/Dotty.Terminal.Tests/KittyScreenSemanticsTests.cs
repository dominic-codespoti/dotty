using System;
using System.Collections.Generic;
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

/// <summary>
/// Placements and screens: the cursor move after a placement through a real adapter + parser, per-screen stores,
/// and the store's notification API (ring rotation, region scroll, history invalidation, cell size, reflow anchors).
/// Cells are 8x16 px.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyScreenSemanticsTests
{
    private const string Esc = "\u001b";
    private const string OnePixel = "AAAA";

    private sealed class Term : IDisposable
    {
        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }
        public List<string> Replies { get; } = new();

        public Term(int rows, int columns, KittyGraphicsOptions? options = null)
        {
            Adapter = new TerminalAdapter(rows, columns);
            Adapter.ConfigureGraphics(options ?? new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Adapter.ReplyRequested += reply => Replies.Add(reply.ToString());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public TerminalBuffer Buffer => Adapter.Buffer;
        public (int Row, int Col) Cursor => (Buffer.CursorRow, Buffer.CursorCol);
        public KittyImageStore Store => Buffer.ActiveImageStore;

        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));
        public void Apc(string control, string payload = "") => Parser.Feed(ApcBytes(control, payload));
        public string Row(int row) => Buffer.GetRowText(row).TrimEnd();

        /// <summary>Fills each row r with the text "R&lt;r&gt;" (the cursor ends on the last row).</summary>
        public void FillRows()
        {
            for (int r = 0; r < Buffer.Rows; r++)
                Feed((r == 0 ? "" : "\r\n") + "R" + r);
        }

        public void Dispose() => Adapter.Dispose();
    }

    // ------------------------------------------------------------------------------- cursor after a placement

    // 6 rows x 10 columns. Setup is VT text that positions the cursor / sets margins; the placement is a=T of a 1x1
    // image with the given keys (so c and r give the cell box).
    [Theory]
    [InlineData("", "c=3,r=2", 1, 3)]
    [InlineData(Esc + "[3;5H", "c=2,r=1", 2, 6)]
    [InlineData(Esc + "[1;9H", "c=2,r=1", 1, 0)]                  // 8+2 = 10: wraps
    [InlineData(Esc + "[1;9H", "c=1,r=1", 0, 9)]                  // 8+1 = 9: stays on the row
    [InlineData(Esc + "[3;10H", "c=1,r=1", 3, 0)]                 // last column wraps
    [InlineData(Esc + "[5;1H", "c=1,r=3", 5, 1)]                  // overshoots the bottom by one row: scrolls once
    [InlineData(Esc + "[6;1H", "c=1,r=1", 5, 1)]                  // last row, fits
    [InlineData(Esc + "[6;10H", "c=1,r=1", 5, 0)]                 // last row, wrap: scrolls once
    [InlineData("", "c=1,r=100", 5, 1)]                           // taller than the screen
    [InlineData(Esc + "[2;4r" + Esc + "[3;1H", "c=1,r=3", 3, 1)]  // margins rows 2-4: scrolls the region
    [InlineData(Esc + "[2;4r", "c=1,r=2", 1, 1)]                  // cursor homed above the region: no scroll
    [InlineData(Esc + "[2;4r" + Esc + "[5;1H", "c=1,r=3", 5, 1)]  // below the region: clamps to the screen
    [InlineData(Esc + "[2;4r" + Esc + "[?6h", "c=1,r=2", 2, 1)]   // DECOM: starts on the top margin row
    [InlineData(Esc + "[2;4r" + Esc + "[?6h", "c=1,r=4", 3, 1)]   // DECOM, overshoots the bottom margin
    [InlineData("", "c=3,r=2,C=1", 0, 0)]
    [InlineData("", "c=3,r=2,U=1", 0, 0)]
    [InlineData(Esc + "[4;5H", "c=3,r=2,C=1", 3, 4)]
    public void CursorMovesPastTheImage(string setup, string keys, int expectedRow, int expectedCol)
    {
        using var t = new Term(6, 10);
        t.Feed(setup);
        t.Apc("a=T,i=1,s=1,v=1,f=24," + keys, OnePixel);
        Assert.Equal((expectedRow, expectedCol), t.Cursor);
    }

    [Fact]
    public void PlacementAtTheBottomScrollsTheTextAndKeepsItsAnchorRow()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed(Esc + "[5;1H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=3", OnePixel);

        Assert.Equal((5, 1), t.Cursor);
        Assert.Equal(new[] { "R1", "R2", "R3", "R4", "R5", "" }, Enumerable.Range(0, 6).Select(r => t.Row(r)));
        var placement = t.Store.Placements.Single();
        // Registered at the cursor row (4) before the scroll, which then moves it up with the text (row 3).
        Assert.Equal(3, placement.AnchorRow - t.Store.ViewTop);
        Assert.Equal(0, placement.AnchorCol);
    }

    [Fact]
    public void PlacementOverTheBottomMarginScrollsOnlyTheRegion()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed(Esc + "[2;4r" + Esc + "[3;1H");   // margins rows 2-4 (index 1..3); cursor at index (2, 0)
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=3", OnePixel);

        Assert.Equal((3, 1), t.Cursor);
        Assert.Equal(new[] { "R0", "R2", "R3", "", "R4", "R5" }, Enumerable.Range(0, 6).Select(r => t.Row(r)));
    }

    [Fact]
    public void AnImageOverhangingTheBottomMarginIsNotScrolledWithTheRegion()
    {
        using var t = new Term(6, 10);
        t.Feed(Esc + "[2;4r" + Esc + "[3;1H");   // margins rows index 1..3, cursor at (2, 0)
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=3", OnePixel);

        // Rows 2..4 reach past the bottom margin: like kitty, only placements wholly inside the margins scroll
        // with the text, so this one stays where it was put although the region scrolled.
        Assert.Equal((3, 1), t.Cursor);
        var placement = t.Store.Placements.Single();
        Assert.Equal((2L, 3), (placement.AnchorRow - t.Store.ViewTop, placement.EffRows));
    }

    [Fact]
    public void AnImageWhollyInsideTheMarginsScrollsWithTheRegionWhenTheCursorMoveScrollsIt()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed(Esc + "[2;4r" + Esc + "[4;1H");   // margins rows index 1..3, cursor on the bottom margin row
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=10,r=1", OnePixel);   // full width: the cursor wraps below the margin

        Assert.Equal((3, 0), t.Cursor);
        Assert.Equal(new[] { "R0", "R2", "R3", "", "R4", "R5" }, Enumerable.Range(0, 6).Select(r => t.Row(r)));
        var placement = t.Store.Placements.Single();
        Assert.Equal(2, placement.AnchorRow - t.Store.ViewTop);   // moved up with its text line "R3"
        Assert.Equal(1, placement.EffRows);
    }

    [Fact]
    public void ATallerThanScreenImageScrollsEverythingAwayAndLeavesTheCursorOnTheLastRow()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed(Esc + "[1;1H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=100", OnePixel);

        Assert.Equal((5, 1), t.Cursor);
        Assert.All(Enumerable.Range(0, 6), r => Assert.Equal("", t.Row(r)));
        var placement = t.Store.Placements.Single();
        Assert.Equal(100, placement.EffRows);
        // The whole overshoot (99 - 5 = 94 rows) scrolled, so the image's last row is the bottom screen row.
        Assert.Equal(-94, placement.AnchorRow - t.Store.ViewTop);
        using var snapshot = t.Buffer.CaptureRenderSnapshotVisible();
        var captured = Assert.Single(snapshot.Graphics!.Placements.ToArray());
        Assert.Equal((-94, 100), (captured.ViewRow, captured.Rows));
    }

    [Fact]
    public void ATallerThanRegionImageClearsTheRegion()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed(Esc + "[2;4r" + Esc + "[2;1H");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=50", OnePixel);
        Assert.Equal((3, 1), t.Cursor);
        Assert.Equal(new[] { "R0", "", "", "", "R4", "R5" }, Enumerable.Range(0, 6).Select(r => t.Row(r)));
    }

    [Fact]
    public void ThePlacementCursorMoveClearsWrapPending()
    {
        using var t = new Term(6, 10);
        t.Feed("ABCDEFGHIJ");                    // fills row 0: the cursor waits at column 9 to wrap
        Assert.Equal((0, 9), t.Cursor);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        Assert.Equal((1, 0), t.Cursor);          // 9+1 >= 10: next row, column 0
        t.Feed("Z");
        Assert.Equal("ABCDEFGHIJ", t.Row(0));
        Assert.Equal("Z", t.Row(1));
        Assert.Equal(0, t.Store.Placements.Single().AnchorRow);
        Assert.Equal(9, t.Store.Placements.Single().AnchorCol); // wrap-pending keeps its column as the anchor
    }

    [Fact]
    public void ThePlacementAnchorIsTheCursorCellAtTheFinalChunk()
    {
        using var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=2,v=1,f=24,m=1,c=1,r=1", "AQID");
        t.Feed(Esc + "[4;7H");
        t.Apc("m=0", "BAUG");
        var placement = t.Store.Placements.Single();
        Assert.Equal((3L, 6), (placement.AnchorRow, placement.AnchorCol));
        Assert.Equal((3, 7), t.Cursor);
    }

    [Fact]
    public void PutAfterTransmitUsesTheCursorAtPutTime()
    {
        using var t = new Term(6, 10);
        t.Apc("i=1,s=1,v=1,f=24", OnePixel);
        t.Feed(Esc + "[2;3H");
        t.Apc("a=p,i=1,c=2,r=2");
        Assert.Equal((2, 4), t.Cursor);           // (1,2) + 2 cols, + 1 row
        Assert.Equal(new[] { Ok("i=1") }, t.Replies.Skip(1));
        Assert.Equal((1L, 2), (t.Store.Placements.Single().AnchorRow, t.Store.Placements.Single().AnchorCol));
    }

    [Fact]
    public void ParsedColumnEditsStartAtCursorAndLeaveImagesLeftOfItAnchored()
    {
        using var t = new Term(6, 10);
        t.Feed(Esc + "[?69h" + Esc + "[2;10s");
        t.Feed(Esc + "[4;2H");
        t.Apc("a=T,i=1,p=1,s=1,v=1,f=24,c=1,r=1,C=1", OnePixel);
        t.Feed(Esc + "[4;6H");
        t.Apc("a=T,i=2,p=1,s=1,v=1,f=24,c=1,r=1,C=1", OnePixel);
        t.Feed(Esc + "[4;3H\u001b[1'}");

        var left = t.Store.FindPlacement(t.Store.FindByClientId(1)!, 1)!;
        var right = t.Store.FindPlacement(t.Store.FindByClientId(2)!, 1)!;
        Assert.Equal(1, left.AnchorCol);
        Assert.Equal(6, right.AnchorCol);

        t.Feed(Esc + "[1'~");
        Assert.Equal(1, left.AnchorCol);
        Assert.Equal(5, right.AnchorCol);
    }

    [Fact]
    public void PlacementsBumpTheBufferGenerationSoTheRendererRedraws()
    {
        using var t = new Term(6, 10);
        ulong before = t.Buffer.Generation;
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        Assert.NotEqual(before, t.Buffer.Generation);
    }

    // ---------------------------------------------------------------------------------- per-screen stores

    [Fact]
    public void MainAndAlternateScreensHaveSeparateStores()
    {
        using var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        var main = t.Store;

        t.Feed(Esc + "[?1049h");
        var alt = t.Store;
        Assert.NotSame(main, alt);
        Assert.Equal(0, alt.Count);
        Assert.Null(t.Adapter.KittyGraphics!.Store.FindByClientId(1)); // main's image is invisible here

        t.Apc("a=T,i=1,s=1,v=1,f=24,c=2,r=1", OnePixel); // the same id on the other screen is another image
        Assert.Equal(1, alt.Count);
        Assert.Equal(1, main.Count);
        Assert.NotSame(main.FindByClientId(1), alt.FindByClientId(1));
        Assert.Equal(2, alt.Placements.Single().EffCols);
        Assert.Equal(1, main.Placements.Single().EffCols);

        t.Feed(Esc + "[?1049l");
        Assert.Same(main, t.Store);
        Assert.Equal(1, main.Count);
        main.ValidateInvariants();
        alt.ValidateInvariants();
    }

    [Fact]
    public void EnteringAndLeavingTheAlternateScreenClearsItsStore()
    {
        using var t = new Term(6, 10);
        t.Feed(Esc + "[?1049h");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        var alt = t.Store;
        Assert.Equal(1, alt.Count);

        t.Feed(Esc + "[?1049l");                  // leaving frees the pixels at once
        Assert.Equal(0, alt.Count);
        Assert.Equal(0, alt.PlacementCount);

        t.Feed(Esc + "[?1049h");                  // entering starts empty (same store object, reused)
        Assert.Same(alt, t.Store);
        Assert.Equal(0, t.Store.Count);

        t.Apc("a=T,i=2,s=1,v=1,f=24,c=1,r=1", OnePixel);
        t.Feed(Esc + "[?1049h"); // already alternate: nothing changes
        Assert.Equal(1, t.Store.Count);
    }

    [Fact]
    public void ModeFortySevenSwitchesStoresToo()
    {
        using var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        t.Feed(Esc + "[?47h");
        Assert.Equal(0, t.Store.Count);
        t.Feed(Esc + "[?47l");
        Assert.Equal(1, t.Store.Count);
    }

    [Fact]
    public void TheQuotaIsPerScreen()
    {
        using var t = new Term(6, 10, new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 5000 });
        string big = B64(new byte[4000]);
        t.Apc("i=1,s=1000,v=1,f=32", big);       // 4000 bytes on the main screen
        t.Feed(Esc + "[?1049h");
        t.Apc("i=2,s=1000,v=1,f=32", big);       // another 4000 bytes on the alternate screen: no eviction
        Assert.Equal(1, t.Store.Count);
        Assert.Equal(new[] { Ok("i=1"), Ok("i=2") }, t.Replies);
        t.Feed(Esc + "[?1049l");
        Assert.Equal(1, t.Store.Count);
        Assert.NotNull(t.Store.FindByClientId(1));
    }

    [Fact]
    public void ALoweredLimitAppliesToBothScreens()
    {
        using var t = new Term(6, 10, new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 5000 });
        string big = B64(new byte[4000]);
        t.Apc("i=1,s=1000,v=1,f=32", big);
        t.Feed(Esc + "[?1049h");
        t.Apc("i=2,s=1000,v=1,f=32", big);
        var alt = t.Store;
        var main = t.Buffer.MainImageStore!;

        t.Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 3000 }, null);
        Assert.Equal(0, alt.Count);
        Assert.Equal(0, main.Count);
        Assert.Equal(3000, alt.StorageLimitBytes);
        Assert.Equal(3000, main.StorageLimitBytes);
    }

    [Fact]
    public void ThePlacementCapAppliesToBothScreens()
    {
        using var t = new Term(6, 10, new KittyGraphicsOptions { Enabled = true, MaxPlacementsPerScreen = 1 });
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1,C=1", OnePixel);
        t.Apc("a=p,i=1,p=2,C=1");
        Assert.Equal(1, t.Store.PlacementCount);
        t.Feed(Esc + "[?1049h");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1,C=1", OnePixel);
        t.Apc("a=p,i=1,p=2,C=1");
        Assert.Equal(1, t.Store.PlacementCount);
        Assert.Equal(1, t.Buffer.MainImageStore!.PlacementCount);
        Assert.Equal(new[] { Ok("i=1"), Err("i=1,p=2", "ENOSPC:Too many placements"), Ok("i=1"), Err("i=1,p=2", "ENOSPC:Too many placements") }, t.Replies);

        t.Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, MaxPlacementsPerScreen = 3 }, null);
        t.Apc("a=p,i=1,p=2,C=1");
        Assert.Equal(2, t.Store.PlacementCount);
    }

    [Fact]
    public void AMultiChunkTransferCommitsIntoTheScreenThatIsActiveAtTheEnd()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24,m=1", "AQID");
        rig.Host.AlternateActive = true;        // switched without the adapter's abort: the commit is what counts
        rig.Apc("m=0", "BAUG");
        Assert.Equal(1, rig.Host.AltStore.Count);
        Assert.Equal(0, rig.Host.MainStore.Count);
    }

    [Fact]
    public void FullResetDeletesTheImagesOfBothScreens()
    {
        using var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        t.Feed(Esc + "[?1049h");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        var main = t.Buffer.MainImageStore!;
        var alt = t.Buffer.AlternateImageStore!;

        t.Feed(Esc + "c");
        Assert.Equal(0, main.Count);
        Assert.Equal(0, alt.Count);
        Assert.Equal(0, main.PlacementCount);
        Assert.False(t.Buffer.IsAlternateScreenActive);
    }

    [Fact]
    public void APutOnTheAlternateScreenCannotSeeTheMainScreensImages()
    {
        using var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1,C=1", OnePixel);
        t.Feed(Esc + "[?1049h");
        t.Apc("a=p,i=1");
        Assert.Equal(0, t.Store.PlacementCount);

        t.Feed(Esc + "[?1049l");
        t.Apc("a=p,i=1,p=2");
        Assert.Equal(2, t.Store.PlacementCount);
        Assert.Equal(new[] { Ok("i=1"), Err("i=1", "ENOENT:Image not found"), Ok("i=1,p=2") }, t.Replies);
    }

    [Fact]
    public void ScrollingErasingResizingAndResettingNeverCreateStores()
    {
        using var t = new Term(6, 10);
        t.FillRows();
        t.Feed("\r\n\r\n" + Esc + "[2S" + Esc + "[T" + Esc + "[2;4r" + Esc + "[S" + Esc + "[T" + Esc + "[r");
        t.Feed(Esc + "[2J" + Esc + "[3J" + Esc + "[?1049h" + "\r\n\r\n\r\n\r\n\r\n\r\n\r\n" + Esc + "[?1049l");
        t.Buffer.ClearScrollback();
        t.Buffer.Resize(8, 12);
        t.Adapter.SetCellPixelSize(9, 18);
        t.Feed(Esc + "c");
        Assert.Null(t.Buffer.MainImageStore);
        Assert.Null(t.Buffer.AlternateImageStore);
    }

    [Fact]
    public void DisposingTheAdapterReleasesPixelsAndPlacementsOfBothScreens()
    {
        var baseline = new GraphicsBaseline();
        long before = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        var t = new Term(6, 10);
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        t.Feed(Esc + "[?1049h");
        t.Apc("a=T,i=1,s=1,v=1,f=24,c=1,r=1", OnePixel);
        t.Apc("a=p,i=1,p=2");
        Assert.Equal(3, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);

        t.Dispose();
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - before);
        baseline.AssertRestored();
    }

    [Fact]
    public void TheStoresAreOnlyCreatedWhenGraphicsAreUsed()
    {
        using var t = new Term(6, 10);
        Assert.Null(t.Buffer.MainImageStore);
        Assert.Null(t.Buffer.AlternateImageStore);
        t.Feed(Esc + "[?1049h" + Esc + "[?1049l" + "hello");
        Assert.Null(t.Buffer.MainImageStore);
        Assert.Null(t.Buffer.AlternateImageStore);
    }

    // ----------------------------------------------------------------------------------- ring rotation

    private static KittyImage Named(KittyImageStore store) => store.AddImage(16, 48, 1);

    [Fact]
    public void RotatingUpPrunesOnlyPlacementsThatLeftTheHistory()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=2,p=1", anchorRow: 0);       // rows 0-1, end 2

        Assert.Equal(0, store.OnRingRotatedUp(6, 5));          // ViewTop 6, floor 1: end 2 > 1, stays
        Assert.Equal(6, store.ViewTop);
        Assert.Equal(1, store.PlacementCount);
        Assert.Equal(1, store.OnRingRotatedUp(1, 5));          // ViewTop 7, floor 2: end 2 <= 2, gone
        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void RotatingUpKeepsTheAnchorRowAndMovesTheViewTop()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Named(store), "c=1,r=1", anchorRow: 4);
        store.OnRingRotatedUp(3, 100);
        Assert.Equal(4, p.AnchorRow);
        Assert.Equal(3, store.ViewTop);
        Assert.Equal(1, p.AnchorRow - store.ViewTop); // on screen row 1 now
    }

    [Fact]
    public void RotatingByZeroOrLessChangesNothing()
    {
        using var store = new KittyImageStore(1_000_000);
        store.Place(Named(store), "c=1,r=1", anchorRow: 0);
        Assert.Equal(0, store.OnRingRotatedUp(0, 0));
        Assert.Equal(0, store.OnRingRotatedUp(-5, 0));
        Assert.Equal(0, store.ViewTop);
        Assert.Equal(0, store.OnRingRotatedDown(0, 10));
        Assert.Equal(0, store.OnRingRotatedDown(-1, 10));
        Assert.Equal(0, store.ViewTop);
        Assert.Equal(1, store.PlacementCount);
    }

    [Fact]
    public void RotatingUpWithoutHistoryCapacityPrunesEverythingAboveTheScreen()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=2,p=1", anchorRow: 0); // rows 0-1
        store.Place(image, "c=1,r=2,p=2", anchorRow: 1); // rows 1-2
        Assert.Equal(1, store.OnRingRotatedUp(2, 0));    // ViewTop 2: placement 1 ended at 2, placement 2 shows row 2
        Assert.Null(store.FindPlacement(image, 1));
        Assert.NotNull(store.FindPlacement(image, 2));
        Assert.Equal(1, store.OnRingRotatedUp(1, 0));
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void TheEndRowBoundAdaptsToDeletionsAndMovesSoLaterPrunesStayCorrect()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=1,p=1", anchorRow: 2);   // end 3
        store.Place(image, "c=1,r=1,p=2", anchorRow: 20);  // end 21
        store.OnRingRotatedUp(4, 0);                       // floor 4: placement 1 goes
        Assert.Equal(1, store.PlacementCount);
        Assert.Equal(0, store.OnRingRotatedUp(10, 0));     // floor 14 < 21
        Assert.Equal(1, store.OnRingRotatedUp(7, 0));      // floor 21: gone
        Assert.Equal(0, store.PlacementCount);

        // A region scroll that moves a placement up lowers its end row: the prune must notice.
        var image2 = store.AddImage(16, 48, 2);
        long top = store.ViewTop;
        store.Place(image2, "c=1,r=1", anchorRow: top + 3);   // screen row 3
        store.OnRegionScrolled(2, 7, 1, 10);                  // now row 2, end = top + 3
        Assert.Equal(1, store.OnRingRotatedUp(3, 0));         // floor = top + 3
        Assert.Equal(0, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void ARotationLargerThanTheCapacityPrunesInOnePass()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        for (uint i = 1; i <= 5; i++)
            store.Place(image, $"c=1,r=1,p={i}", anchorRow: i * 2);
        Assert.Equal(5, store.OnRingRotatedUp(1_000_000_000_000L, 100));
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(1_000_000_000_000L, store.ViewTop);
        store.ValidateInvariants();
    }

    [Fact]
    public void RotatingDownDeletesPlacementsThatStartBelowTheScreen()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(10, 100);                       // ViewTop 10
        store.Place(image, "c=1,r=1,p=1", anchorRow: 12);
        store.Place(image, "c=1,r=1,p=2", anchorRow: 13);
        store.Place(image, "c=1,r=1,p=3", anchorRow: 14);
        store.Place(image, "c=1,r=3,p=4", anchorRow: 12);     // rows 12-14: starts above the limit, keeps a tail

        // Rotate down by 2 with a 5-row screen: ViewTop 8, rows 8..12 stay; a placement starting at 13 is below.
        Assert.Equal(2, store.OnRingRotatedDown(2, 5));
        Assert.Equal(8, store.ViewTop);
        Assert.NotNull(store.FindPlacement(image, 1));
        Assert.Null(store.FindPlacement(image, 2));
        Assert.Null(store.FindPlacement(image, 3));
        Assert.NotNull(store.FindPlacement(image, 4));
        store.ValidateInvariants();
    }

    [Fact]
    public void RotatingDownThenUpRestoresTheViewTop()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(Named(store), "c=1,r=1", anchorRow: 5);
        store.OnRingRotatedUp(4, 100);
        store.OnRingRotatedDown(3, 10);
        Assert.Equal(1, store.ViewTop);
        Assert.Equal(4, p.AnchorRow - store.ViewTop);
    }

    // ----------------------------------------------------------------------------------- region scroll

    // Region rows 2..7 (inclusive) of a 10-row screen; image 16x48 px, cells 8x16, c=2,r=3 stretches 1:1 (48 px tall).
    private static KittyPlacement InRegion(KittyImageStore store, KittyImage image, string keys, long row) =>
        store.Place(image, keys, anchorRow: row);

    [Fact]
    public void PlacementsOutsideTheRegionAreUntouched()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var above = InRegion(store, image, "c=2,r=2,p=1", 0);
        var below = InRegion(store, image, "c=2,r=2,p=2", 8);
        var geoAbove = above.Geometry;

        Assert.Equal(0, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal(0, store.OnRegionScrolled(2, 7, -1, 10));
        Assert.Equal((0L, 8L), (above.AnchorRow, below.AnchorRow));
        Assert.Equal(geoAbove.EffRows, above.EffRows);
        Assert.Equal(2, below.EffRows);
    }

    [Fact]
    public void PlacementsWhollyInsideMoveWithTheScroll()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=2,r=2", 4);
        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal(3, p.AnchorRow);
        Assert.Equal((0, 48, 2), (p.SrcY, p.SrcH, p.EffRows)); // not cropped
        Assert.Equal(1, store.OnRegionScrolled(2, 7, -2, 10));
        Assert.Equal(5, p.AnchorRow);
        store.ValidateInvariants();
    }

    [Fact]
    public void ScrollingUpPastTheTopMarginCropsTheTopProportionally()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=2,r=3", 2);       // rows 2-4: touches the top margin
        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        // One row (16 of 48 px) is lost: the source loses its top third.
        Assert.Equal((2L, 2, 16, 32), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));

        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal((2L, 1, 32, 16), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));

        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));  // the last row scrolls out: deleted
        Assert.Equal(0, store.PlacementCount);
        Assert.False(image.IsDeleted);
        store.ValidateInvariants();
    }

    [Fact]
    public void ScrollingUpByMoreThanThePlacementHeightDeletesItWithoutCropping()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        InRegion(store, image, "c=2,r=3", 2);
        store.OnRegionScrolled(2, 7, 3, 10); // lost = 3 >= EffRows 3
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void ScrollingDownPastTheBottomMarginCropsTheBottom()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=2,r=3", 5);       // rows 5-7: touches the bottom margin
        Assert.Equal(1, store.OnRegionScrolled(2, 7, -1, 10));
        Assert.Equal((6L, 2, 0, 32), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));
        store.ValidateInvariants();

        Assert.Equal(1, store.OnRegionScrolled(2, 7, -1, 10));
        Assert.Equal((7L, 1, 0, 16), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));
    }

    [Fact]
    public void ScrollingDownCompletelyOutDeletesThePlacement()
    {
        using var store = new KittyImageStore(1_000_000);
        InRegion(store, Named(store), "c=2,r=3", 5);
        store.OnRegionScrolled(2, 7, -3, 10);
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void PlacementsStraddlingAMarginAreLeftAlone()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var topStraddler = InRegion(store, image, "c=2,r=3,p=1", 1);    // rows 1-3, margin at 2
        var bottomStraddler = InRegion(store, image, "c=2,r=3,p=2", 6); // rows 6-8, margin ends at 7
        Assert.Equal(0, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal(0, store.OnRegionScrolled(2, 7, -1, 10));
        Assert.Equal((1L, 3, 6L, 3), (topStraddler.AnchorRow, topStraddler.EffRows, bottomStraddler.AnchorRow, bottomStraddler.EffRows));
    }

    [Fact]
    public void ARegionOfOneRowDeletesWhatScrollsOutOfIt()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var inside = InRegion(store, image, "c=1,r=1,p=1", 4);
        var straddles = InRegion(store, image, "c=1,r=2,p=2", 3);   // rows 3-4
        var outside = InRegion(store, image, "c=1,r=1,p=3", 3);

        Assert.Equal(1, store.OnRegionScrolled(4, 4, 1, 10));
        Assert.Null(store.FindPlacement(image, 1));
        Assert.Same(straddles, store.FindPlacement(image, 2));
        Assert.Same(outside, store.FindPlacement(image, 3));

        InRegion(store, image, "c=1,r=1,p=4", 4);
        Assert.Equal(1, store.OnRegionScrolled(4, 4, -1, 10));
        Assert.Null(store.FindPlacement(image, 4));
        store.ValidateInvariants();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void AScrollLargerThanTheRegionDeletesEveryPlacementInsideIt(int delta)
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        InRegion(store, image, "c=1,r=1,p=1", 3);
        InRegion(store, image, "c=1,r=2,p=2", 5);
        var outside = InRegion(store, image, "c=1,r=1,p=3", 9);
        Assert.Equal(2, store.OnRegionScrolled(2, 7, delta, 10));
        Assert.Equal(1, store.PlacementCount);
        Assert.Same(outside, store.FindPlacement(image, 3));

        InRegion(store, image, "c=1,r=1,p=1", 3);
        Assert.Equal(1, store.OnRegionScrolled(2, 7, -delta, 10));
        Assert.Equal(1, store.PlacementCount);
    }

    [Fact]
    public void RegionScrollsLeaveHistoryAndVirtualPlacementsAlone()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(5, 100);                      // ViewTop 5
        var history = InRegion(store, image, "c=1,r=2,p=1", 1);
        var virtualP = InRegion(store, image, "U=1,c=2,r=2,p=2", 0);
        Assert.Equal(0, store.OnRegionScrolled(0, 3, 1, 10));
        Assert.Equal(1, history.AnchorRow);
        Assert.Equal(0, virtualP.AnchorRow);
        Assert.Equal(PlacementKind.Virtual, virtualP.Kind);
    }

    [Fact]
    public void RegionScrollsAreRelativeToTheViewTop()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(100, 1000);
        var p = InRegion(store, image, "c=1,r=1", 104); // screen row 4
        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal(103, p.AnchorRow);
    }

    [Fact]
    public void RegionBoundsAreClampedToTheScreenAndEmptyRegionsDoNothing()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=1,r=1", 8);
        Assert.Equal(0, store.OnRegionScrolled(12, 99, 1, 10));  // top beyond the clamped bottom
        Assert.Equal(0, store.OnRegionScrolled(5, 3, 1, 10));
        Assert.Equal(0, store.OnRegionScrolled(0, 9, 0, 10));
        Assert.Equal(8, p.AnchorRow);
        Assert.Equal(1, store.OnRegionScrolled(-5, 99, 1, 10));  // clamps to 0..9
        Assert.Equal(7, p.AnchorRow);
    }

    [Fact]
    public void CroppingConsumesTheTopOffsetAndKeepsTheRestOfTheSource()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        // Native size, Y offset 8 px: 56 px tall = 4 cells. Losing one row removes 16-8 = 8 px.
        var p = InRegion(store, image, "Y=8", 2);
        Assert.Equal(4, p.EffRows);
        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal((2L, 3, 0, 8, 40), (p.AnchorRow, p.EffRows, p.CellYOff, p.SrcY, p.SrcH));
    }

    [Fact]
    public void CropsKeepTheSourceColumns()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "x=4,w=8,c=1,r=3", 2);
        Assert.Equal(1, store.OnRegionScrolled(2, 7, 1, 10));
        Assert.Equal((4, 8), (p.SrcX, p.SrcW));
        Assert.Equal(2, p.EffRows);
    }

    [Fact]
    public void APlacementFillingTheWholeRegionIsCroppedByEachScrollAndKeepsItsSourceWindow()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=2,r=3", 2);       // rows 2..4 = exactly the region
        Assert.Equal(1, store.OnRegionScrolled(2, 4, 1, 10));
        Assert.Equal((2L, 2, 16, 32), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));

        // The lost row does not grow back; the remaining two rows move down and still fit.
        Assert.Equal(1, store.OnRegionScrolled(2, 4, -1, 10));
        Assert.Equal((3L, 2, 16, 32), (p.AnchorRow, p.EffRows, p.SrcY, p.SrcH));
        store.ValidateInvariants();
    }

    [Fact]
    public void ColumnEditsMoveAndCropOnlyContainedPlacementsWithoutRegrowth()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var left = store.Place(image, "c=2,r=2,p=1", anchorRow: 3, anchorCol: 2);


        Assert.Equal(1, store.OnColumnsScrolled(2, 7, 2, 7, 1, 10));
        Assert.Equal((2, 1, 8, 8), (left.AnchorCol, left.EffCols, left.SrcX, left.SrcW));
        Assert.Equal(1, store.OnColumnsScrolled(2, 7, 2, 7, -1, 10));
        Assert.Equal((3, 1, 8, 8), (left.AnchorCol, left.EffCols, left.SrcX, left.SrcW));

        store.ValidateInvariants();
    }

    [Fact]
    public void ColumnInsertionCropsRightAndReversingDoesNotRestoreSource()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = store.Place(image, "c=2,r=2,p=1", anchorRow: 3, anchorCol: 6);
        Assert.Equal(1, store.OnColumnsScrolled(2, 7, 2, 7, -1, 10));
        Assert.Equal((7, 1, 0, 8), (p.AnchorCol, p.EffCols, p.SrcX, p.SrcW));
        Assert.Equal(1, store.OnColumnsScrolled(2, 7, 2, 7, 1, 10));
        Assert.Equal((6, 1, 0, 8), (p.AnchorCol, p.EffCols, p.SrcX, p.SrcW));
        store.ValidateInvariants();
    }

    [Fact]
    public void ColumnEditsLeaveOutsideStraddlingAndHistoryPlacementsUntouched()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(10, 100);
        var history = InRegion(store, image, "c=1,r=1,p=1", 5);
        var outside = InRegion(store, image, "c=1,r=1,p=2", 20);
        var straddler = store.Place(image, "c=2,r=1,p=3", anchorRow: 12, anchorCol: 7);
        var historyGeometry = history.Geometry;
        var outsideGeometry = outside.Geometry;
        var straddlerGeometry = straddler.Geometry;
        Assert.Equal(0, store.OnColumnsScrolled(0, 5, 2, 7, 1, 10));
        Assert.Equal((5L, 20L, 7), (history.AnchorRow, outside.AnchorRow, straddler.AnchorCol));
        Assert.Equal(historyGeometry, history.Geometry);
        Assert.Equal(outsideGeometry, outside.Geometry);
        Assert.Equal(straddlerGeometry, straddler.Geometry);
    }

    [Fact]
    public void ColumnDeletionRemovesFullyShiftedPlacementsButRetainsTheImage()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=2,r=1,p=1", anchorRow: 3, anchorCol: 2);
        Assert.Equal(1, store.OnColumnsScrolled(2, 7, 2, 7, 2, 10));
        Assert.Equal(0, store.PlacementCount);
        Assert.False(image.IsDeleted);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------ history

    private static KittyImageStore HistoryStore(out KittyPlacement above, out KittyPlacement touching, out KittyPlacement crossing,
        out KittyPlacement visible, out KittyPlacement virtualP)
    {
        var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(10, 1000); // ViewTop 10
        above = store.Place(image, "c=1,r=2,p=1", anchorRow: 5);      // end 7
        touching = store.Place(image, "c=1,r=2,p=2", anchorRow: 8);   // end 10 == ViewTop
        crossing = store.Place(image, "c=1,r=2,p=3", anchorRow: 9);   // end 11
        visible = store.Place(image, "c=1,r=2,p=4", anchorRow: 12);
        virtualP = store.Place(image, "U=1,c=1,r=1,p=5");
        return store;
    }

    [Fact]
    public void ClearedWithTheViewTopAsFloorDeletesOnlyHistorySidePlacements()
    {
        using var store = HistoryStore(out _, out _, out var crossing, out var visible, out var virtualP);
        Assert.Equal(2, store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, store.ViewTop));
        Assert.Equal(new[] { crossing, visible, virtualP }.Select(p => p.ClientId).OrderBy(i => i),
            store.Placements.Select(p => p.ClientId).OrderBy(i => i));
        store.ValidateInvariants();
    }

    [Fact]
    public void ClearedWithAFloorBeyondEverythingDeletesAllAnchoredPlacements()
    {
        using var store = HistoryStore(out _, out _, out _, out _, out var virtualP);
        Assert.Equal(4, store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, long.MaxValue));
        Assert.Same(virtualP, Assert.Single(store.Placements));
    }

    [Fact]
    public void ClearedBoundaryIsInclusiveOfTheEndRow()
    {
        using var store = HistoryStore(out var above, out var touching, out var crossing, out _, out _);
        Assert.Equal(0, store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, above.EndRow - 1));
        Assert.Equal(1, store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, above.EndRow));
        Assert.Equal(1, store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, touching.EndRow));
        Assert.NotNull(store.FindPlacement(crossing.Image, 3));
    }

    [Fact]
    public void ResetDeletesEveryPlacementIncludingVirtualOnes()
    {
        using var store = HistoryStore(out _, out _, out _, out _, out _);
        Assert.Equal(5, store.OnHistoryInvalidated(HistoryDiscardReason.Reset, 0));
        Assert.Equal(0, store.PlacementCount);
        Assert.Equal(1, store.Count); // the image itself stays (the controller clears images separately)
    }

    [Fact]
    public void CapacityShrunkDeletesWhatLiesWhollyAboveTheNewFloor()
    {
        using var store = HistoryStore(out _, out _, out var crossing, out _, out _);
        Assert.Equal(2, store.OnHistoryInvalidated(HistoryDiscardReason.CapacityShrunk, 10));
        Assert.NotNull(store.FindPlacement(crossing.Image, 3));
        Assert.Equal(0, store.OnHistoryInvalidated(HistoryDiscardReason.CapacityShrunk, 10));
    }

    [Fact]
    public void RebasedDeletesNothing()
    {
        using var store = HistoryStore(out _, out _, out _, out _, out _);
        Assert.Equal(0, store.OnHistoryInvalidated(HistoryDiscardReason.Rebased, long.MaxValue));
        Assert.Equal(5, store.PlacementCount);
    }

    [Fact]
    public void HistoryInvalidationDropsAnonymousImagesLeftWithoutPlacements()
    {
        using var store = new KittyImageStore(1_000_000);
        var anonymous = store.AddImage(16, 48);
        store.Place(anonymous, "c=1,r=1", anchorRow: 0);
        store.OnRingRotatedUp(5, 100);
        store.OnHistoryInvalidated(HistoryDiscardReason.Cleared, store.ViewTop);
        Assert.True(anonymous.IsDeleted);
    }

    // ----------------------------------------------------------------------------------- cell size

    [Fact]
    public void ACellSizeChangeReResolvesNativeAndOneDimensionPlacements()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        var native = store.Place(image, "p=1");              // 5 x 2 at 8x16
        var colsOnly = store.Place(image, "p=2,c=10");       // 10 x 3 at 8x16
        var box = store.Place(image, "p=3,c=3,r=3");
        Assert.Equal(3, store.OnCellPixelSizeChanged(10, 20));

        Assert.Equal((4, 1), (native.EffCols, native.EffRows));      // ceil(40/10), ceil(20/20)
        Assert.Equal((10, 3), (colsOnly.EffCols, colsOnly.EffRows)); // ceil(10*10*20 / (40*20)) = ceil(2.5)
        Assert.Equal((3, 3), (box.EffCols, box.EffRows));
        Assert.All(new[] { native, colsOnly, box }, p => Assert.Equal((10, 20), (p.BasisCellW, p.BasisCellH)));
        store.ValidateInvariants();
    }

    [Fact]
    public void ReResolvingKeepsTheSourceRectangleAndReClampsOffsets()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(40, 20, 1);
        var p = store.Place(image, "x=4,y=2,w=20,h=10,X=7");
        Assert.Equal(7, p.CellXOff);
        store.OnCellPixelSizeChanged(4, 16);
        Assert.Equal((4, 2, 20, 10), (p.SrcX, p.SrcY, p.SrcW, p.SrcH));
        Assert.Equal(3, p.CellXOff);                    // clamped to the new cell width - 1
        Assert.Equal(6, p.EffCols);                     // ceil((20 + 3) / 4)
        Assert.Equal(1, p.EffRows);                     // ceil(10 / 16)
    }

    [Fact]
    public void AnUnchangedOrNonPositiveCellSizeChangesNothing()
    {
        using var store = new KittyImageStore(1_000_000);
        var p = store.Place(store.AddImage(40, 20, 1));
        Assert.Equal(0, store.OnCellPixelSizeChanged(8, 16));
        Assert.Equal(1, store.OnCellPixelSizeChanged(0, -3)); // treated as 1x1
        Assert.Equal((1, 1), (p.BasisCellW, p.BasisCellH));
        Assert.Equal((40, 20), (p.EffCols, p.EffRows));
    }

    [Fact]
    public void ACroppedPlacementStaysCroppedAcrossACellSizeChange()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        var p = InRegion(store, image, "c=2,r=3", 2);
        store.OnRegionScrolled(2, 7, 1, 10);                  // cropped: rows 2, source y 16..48
        store.OnCellPixelSizeChanged(8, 32);
        Assert.Equal((2, 2, 16, 32), (p.EffCols, p.EffRows, p.SrcY, p.SrcH));
        Assert.Equal(32, p.BasisCellH);
    }

    // ------------------------------------------------------------------------------- reflow anchors

    [Fact]
    public void CapturingRegistersOnlyPlacementsInTheRetainedRows()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(10, 1000);                        // ViewTop 10
        store.Place(image, "c=1,r=1,p=1", anchorRow: 7, anchorCol: 2);    // logical -3: in the 3 retained history rows
        store.Place(image, "c=1,r=1,p=2", anchorRow: 6);                  // logical -4: already beyond
        store.Place(image, "c=1,r=1,p=3", anchorRow: 10, anchorCol: 5);   // logical 0
        store.Place(image, "c=1,r=1,p=4", anchorRow: 13, anchorCol: 1);   // logical 3: last of 4 rows
        store.Place(image, "c=1,r=1,p=5", anchorRow: 14);                 // logical 4: below the screen
        store.Place(image, "U=1,c=1,r=1,p=6");

        var registered = new List<(int Row, int Col)>();
        int count = store.CaptureAnchors(3, 4, (row, col) =>
        {
            registered.Add((row, col));
            return registered.Count - 1;
        });

        Assert.Equal(3, count);
        Assert.Equal(new[] { (-3, 2), (0, 5), (3, 1) }.OrderBy(x => x), registered.OrderBy(x => x));
    }

    [Fact]
    public void RebasingMovesPlacementsToTheMappedPositionAndDeletesTheTrimmedOnes()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.OnRingRotatedUp(10, 1000);
        store.Place(image, "c=1,r=1,p=1", anchorRow: 7, anchorCol: 2);    // history
        store.Place(image, "c=1,r=1,p=2", anchorRow: 10, anchorCol: 5);   // trimmed by the reflow
        store.Place(image, "c=1,r=1,p=3", anchorRow: 13, anchorCol: 1);   // lands on the last output row
        store.Place(image, "c=1,r=1,p=4", anchorRow: 14);                 // never captured
        store.Place(image, "c=1,r=1,p=5", anchorRow: 11, anchorCol: 0);   // maps beyond the retained rows
        var virtualP = store.Place(image, "U=1,c=1,r=1,p=6");

        var slotOf = new Dictionary<int, uint>();
        int next = 0;
        store.CaptureAnchors(3, 4, (row, col) =>
        {
            // identify by logical position
            uint id = row switch { -3 => 1u, 0 => 2u, 3 => 3u, 1 => 5u, _ => 99u };
            slotOf[next] = id;
            return next++;
        });
        Assert.Equal(4, next);

        // New layout: 8 history rows + 5 visible rows = 13 output rows (indices 0..12).
        int deleted = store.RebaseAnchors(8, 5, (int slot, out long row, out int col) =>
        {
            switch (slotOf[slot])
            {
                case 1: row = 6; col = 3; return true;
                case 2: row = 0; col = 0; return false;     // line trimmed
                case 3: row = 12; col = 0; return true;
                default: row = 13; col = 0; return true;    // outside [0, 13)
            }
        });

        Assert.Equal(3, deleted);                 // trimmed, never captured, and mapped outside the output
        Assert.Equal(8, store.ViewTop);
        var p1 = store.FindPlacement(image, 1)!;
        var p3 = store.FindPlacement(image, 3)!;
        Assert.Equal((6L, 3), (p1.AnchorRow, p1.AnchorCol));
        Assert.Equal((12L, 0), (p3.AnchorRow, p3.AnchorCol));
        Assert.Equal(4, p3.AnchorRow - store.ViewTop);
        Assert.Same(virtualP, store.FindPlacement(image, 6));
        Assert.Equal(3, store.PlacementCount);
        store.ValidateInvariants();
    }

    [Fact]
    public void RebasingWithoutCapturingDeletesEveryAnchoredPlacement()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=1", anchorRow: 0);
        Assert.Equal(1, store.RebaseAnchors(0, 3, (int slot, out long row, out int col) => { row = 0; col = 0; return true; }));
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void RebasingKeepsTheEndRowBoundCorrect()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=1", anchorRow: 8);                  // end 9: the cached bound is 9
        Assert.Equal(1, store.CaptureAnchors(0, 10, (row, col) => 0));
        Assert.Equal(0, store.RebaseAnchors(10, 5, (int slot, out long row, out int col) => { row = 2; col = 1; return true; }));
        // The placement ends at row 3 now. With ViewTop 11 and capacity 7 the floor is 4: it must be pruned even
        // though the old bound (9) was higher.
        Assert.Equal(1, store.OnRingRotatedUp(1, 7));
        Assert.Equal(0, store.PlacementCount);
    }

    [Fact]
    public void ClearAllDeletesEveryPlacementOfEveryKindAndKeepsNamedImages()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = Named(store);
        store.Place(image, "c=1,r=1,p=1");
        store.Place(image, "U=1,c=1,r=1,p=2");
        Assert.Equal(2, store.ClearAll());
        Assert.Equal(0, store.ClearAll());
        Assert.Equal(1, store.Count);
        store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------- randomised

    private static readonly string[] PlaceKeys =
    {
        "", "c=2,r=2", "c=3", "r=2", "p=1", "p=2,c=1,r=3", "p=3,z=2", "U=1,p=4,c=2,r=2",
        "x=2,y=2,w=5,h=5,X=3,Y=3", "x=100", "p=1,c=4,r=4,z=-3", "c=1,r=1",
    };

    private static readonly string[] DeleteKeys =
    {
        "d=a", "d=A", "d=i,i=1", "d=I,i=2", "d=i,i=3,p=1", "d=n,I=1", "d=N,I=2,p=2", "d=c", "d=C", "d=p,x=3,y=3",
        "d=q,x=2,y=2,z=2", "d=x,x=2", "d=X,x=1", "d=y,y=3", "d=Y,y=1", "d=z,z=2", "d=Z,z=-3", "d=r,x=1,y=3", "d=R,x=2,y=5",
        "d=f", "d=k",
    };

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
    public void SeededRandomOperationsIncludingNotificationsKeepTheInvariants(int seed)
    {
        var baseline = new GraphicsBaseline();
        long placementsBefore = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        var rng = new Random(seed);
        var store = new KittyImageStore(40_000) { MaxPlacements = 12 };
        long now = 0;
        for (int step = 0; step < 600; step++)
        {
            switch (rng.Next(16))
            {
                case 0:
                    {
                        var pixels = ImagePixels.Allocate(rng.Next(1, 30), rng.Next(1, 30), ImagePixelFormat.Rgba8Premultiplied);
                        if (!store.TryAdd(pixels, (uint)rng.Next(0, 6), (uint)rng.Next(0, 3), rng.Next(5) == 0, ++now, out _))
                            pixels.Dispose(); // larger than the (lowered) quota: the store did not take it
                        break;
                    }
                case 1:
                case 2:
                case 3:
                case 4:
                    {
                        var images = store.Images.ToList();
                        if (images.Count == 0) break;
                        var image = images[rng.Next(images.Count)];
                        long row = store.ViewTop + rng.Next(-4, 14);
                        store.TryPlace(image, Parse(PlaceKeys[rng.Next(PlaceKeys.Length)]), row, rng.Next(0, 12),
                            rng.Next(0, 3) == 0 ? 8 : 6, 16, out _);
                        break;
                    }
                case 5:
                    store.ApplyDelete(Parse("a=d," + DeleteKeys[rng.Next(DeleteKeys.Length)]), rng.Next(0, 10), rng.Next(0, 12), 10);
                    break;
                case 6:
                    store.OnRingRotatedUp(rng.Next(1, 5), rng.Next(0, 10));
                    break;
                case 7:
                    store.OnRingRotatedDown(rng.Next(1, 4), 10);
                    break;
                case 8:
                    {
                        int top = rng.Next(0, 8);
                        store.OnRegionScrolled(top, rng.Next(top, 10), rng.Next(-4, 5), 10);
                        break;
                    }
                case 9:
                    store.OnHistoryInvalidated((HistoryDiscardReason)rng.Next(0, 4), store.ViewTop + rng.Next(-3, 4));
                    break;
                case 10:
                    store.OnCellPixelSizeChanged(rng.Next(4, 17), rng.Next(8, 33));
                    break;
                case 11:
                    {
                        int sb = rng.Next(0, 6);
                        int newSb = rng.Next(0, 6);
                        int slots = 0;
                        store.CaptureAnchors(sb, 10, (r, c) => slots++);
                        store.RebaseAnchors(newSb, 10, (int slot, out long row, out int col) =>
                        {
                            row = newSb + slot % 10;
                            col = slot % 7;
                            return slot % 5 != 0;
                        });
                        break;
                    }
                case 12:
                    store.SetStorageLimit(rng.Next(2_000, 60_000));
                    break;
                case 13:
                    if (rng.Next(6) == 0) store.ClearAll();
                    break;
                case 14:
                    store.MaxPlacements = rng.Next(1, 16);
                    break;
                default:
                    if (rng.Next(20) == 0) store.Clear();
                    break;
            }
            store.ValidateInvariants();
            Assert.Equal(store.PlacementCount, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore);
        }
        store.Dispose();
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore);
        baseline.AssertRestored();
    }
}
