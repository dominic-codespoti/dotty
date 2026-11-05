using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// The terminal buffer's kitty-graphics hooks, end to end through a real adapter and parser: placements follow the
/// text through line feeds, scroll up/down with and without margins, history discards, resets, the alternate screen,
/// resize reflow and cell-size changes. Cells are 10x20 px; every image is 10 px wide and <c>rows * 20</c> px tall so
/// a placement of <c>r</c> rows maps 1:1 onto its pixels (a crop of one row removes exactly 20 source pixels).
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyBufferHooksTests
{
    private const string Esc = "\u001b";
    private const int CellW = 10;
    private const int CellH = 20;

    private sealed class Rig : IDisposable
    {
        private uint _nextId;

        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }
        public int Capacity { get; }

        public Rig(int rows = 6, int columns = 10, int capacity = 100, KittyGraphicsOptions? options = null)
        {
            Capacity = capacity;
            Adapter = new TerminalAdapter(rows, columns, capacity);
            Adapter.SetCellPixelSize(CellW, CellH);
            Adapter.ConfigureGraphics(options ?? new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public TerminalBuffer Buffer => Adapter.Buffer;
        public KittyImageStore Store => Buffer.ActiveImageStore;

        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));

        public void Apc(string control, string payload = "") => Parser.Feed(KittyTestData.ApcBytes(control, payload));

        public string Row(int row) => Buffer.GetRowText(row).TrimEnd();

        public RenderSnapshot Capture(int scrollOffset = 0) => Buffer.CaptureRenderSnapshotVisible(0, -1, scrollOffset);

        /// <summary>Writes "R0".."R{rows-1}" on the rows; the cursor ends on the last row.</summary>
        public void FillRows()
        {
            for (int r = 0; r < Buffer.Rows; r++)
                Feed((r == 0 ? "" : "\r\n") + "R" + r);
        }

        /// <summary>Moves the cursor to the 0-based cell and places a <paramref name="cols"/> x <paramref name="rows"/> image there.</summary>
        public uint Put(int row, int col, int cols = 1, int rows = 1, string extra = "C=1")
        {
            Feed(Esc + "[" + (row + 1) + ";" + (col + 1) + "H");
            return PutHere(cols, rows, extra);
        }

        /// <summary>Places a <paramref name="cols"/> x <paramref name="rows"/> image at the cursor. Without <c>C=1</c> the cursor moves past it.</summary>
        public uint PutHere(int cols = 1, int rows = 1, string extra = "C=1")
        {
            uint id = ++_nextId;
            int height = rows * CellH;
            string control = $"a=T,i={id},s={CellW},v={height},f=24,c={cols},r={rows}" + (extra.Length > 0 ? "," + extra : "");
            Apc(control, Convert.ToBase64String(new byte[CellW * height * 3]));
            return id;
        }

        /// <summary>Places an image at its native pixel size (no c/r) at the 0-based cell, cursor unmoved.</summary>
        public uint PutNative(int row, int col, int widthPx, int heightPx, string extra = "")
        {
            uint id = ++_nextId;
            Feed(Esc + "[" + (row + 1) + ";" + (col + 1) + "H");
            Apc($"a=T,i={id},s={widthPx},v={heightPx},f=24,C=1" + (extra.Length > 0 ? "," + extra : ""),
                Convert.ToBase64String(new byte[widthPx * heightPx * 3]));
            return id;
        }

        /// <summary>Line feeds issued with the cursor on the bottom row: each one scrolls the screen up by one row.</summary>
        public void Lf(int count = 1)
        {
            Feed(Esc + "[" + Buffer.Rows + ";1H" + new string('\n', count));
        }

        public KittyPlacement? Find(uint id, KittyImageStore? store = null) =>
            (store ?? Store).Placements.SingleOrDefault(p => p.Image.ClientId == id);

        /// <summary>Visible row (negative = history) of the placement of image <paramref name="id"/>, null when it is gone.</summary>
        public long? RowOf(uint id, KittyImageStore? store = null)
        {
            store ??= Store;
            return Find(id, store) is { } p ? p.AnchorRow - store.ViewTop : null;
        }

        public (long Row, int Col)? CellOf(uint id, KittyImageStore? store = null)
        {
            store ??= Store;
            return Find(id, store) is { } p ? (p.AnchorRow - store.ViewTop, p.AnchorCol) : null;
        }

        public void Dispose() => Adapter.Dispose();
    }

    // ------------------------------------------------------------------------------------- line feed / ring

    [Fact]
    public void ALineFeedAtTheBottomScrollsAPlacementUpWithItsText()
    {
        using var t = new Rig();
        t.FillRows();
        uint a = t.Put(3, 0);                           // on the line "R3"

        t.Lf();
        Assert.Equal(2L, t.RowOf(a));
        Assert.Equal("R3", t.Row(2));
        t.Lf(2);
        Assert.Equal(0L, t.RowOf(a));
        Assert.Equal("R3", t.Row(0));
        t.Lf();
        Assert.Equal(-1L, t.RowOf(a));                  // in the history now, still alive
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void ALineFeedAboveTheBottomMovesNothing()
    {
        using var t = new Rig();
        uint a = t.Put(3, 2);
        t.Feed(Esc + "[2;1H\n\n");                      // cursor row 1 -> 3: no scroll
        Assert.Equal(3L, t.RowOf(a));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void APlacementLeavesWithItsRowWhenItPassesTheHistoryCapacity()
    {
        using var t = new Rig(capacity: 5);
        uint a = t.Put(0, 0);
        t.Lf(5);
        Assert.Equal(-5L, t.RowOf(a));                  // the oldest history row the ring still holds
        t.Lf();
        Assert.Null(t.RowOf(a));                        // its row was overwritten
        Assert.Equal(0, t.Store.PlacementCount);
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void ATallPlacementStaysUntilItsLastRowLeavesTheHistory()
    {
        using var t = new Rig(capacity: 5);
        uint a = t.Put(0, 0, rows: 3);                  // rows 0..2
        t.Lf(7);                                        // its last row is at -5: still held
        Assert.Equal(-7L, t.RowOf(a));
        t.Lf();                                         // last row (index 2) now at -6: gone
        Assert.Null(t.RowOf(a));
    }

    [Fact]
    public void WithoutAnyScrollbackAPlacementIsGoneAsSoonAsItsRowScrollsOff()
    {
        using var t = new Rig(capacity: 0);
        uint a = t.Put(0, 0);
        uint b = t.Put(1, 0);
        t.Lf();
        Assert.Null(t.RowOf(a));
        Assert.Equal(0L, t.RowOf(b));
        t.Lf();
        Assert.Null(t.RowOf(b));
    }

    [Fact]
    public void ScrollingByMoreThanTheScreenHeightIsClampedToTheRingRotation()
    {
        using var t = new Rig(capacity: 100);
        uint a = t.Put(4, 0);
        t.Feed(Esc + "[100S");                          // the ring rotates by at most Rows (6)
        Assert.Equal(-2L, t.RowOf(a));
        Assert.Equal(6L, t.Store.ViewTop);
    }

    [Fact]
    public void RingWrapKeepsExactlyThePlacementsWhoseRowsTheRingStillHolds()
    {
        const int capacity = 5, rows = 6;
        using var t = new Rig(rows, 10, capacity);
        var lineOfId = new Dictionary<uint, int>();
        for (int i = 0; i < 100; i++)
        {
            t.Feed("M" + i);
            lineOfId[t.PutHere()] = i;
            t.Feed("\r\n");

            int scrolled = Math.Max(0, i + 1 - (rows - 1));      // line i+1 is the cursor line
            var expectedAlive = lineOfId.Where(kv => kv.Value - scrolled >= -capacity).Select(kv => kv.Key).OrderBy(x => x).ToArray();
            var actualAlive = t.Store.Placements.Select(p => p.Image.ClientId).OrderBy(x => x).ToArray();
            Assert.Equal(expectedAlive, actualAlive);

            foreach (var (id, line) in lineOfId)
            {
                long? row = t.RowOf(id);
                if (row is null)
                    continue;
                Assert.Equal(line - scrolled, row);
                if (row >= 0)
                    Assert.Equal("M" + line, t.Row((int)row));   // the placement is on its own text line
            }
        }
        Assert.True(t.Store.PlacementCount <= capacity + rows);
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void AHistoryLineTheRingKeptCarriesItsPlacementAndAnOlderOneDoesNot()
    {
        using var t = new Rig(3, 10, 1);
        t.Feed("L0");
        uint l0 = t.PutHere();
        t.Feed("\r\nL1");
        uint l1 = t.PutHere();
        t.Feed("\r\nL2\r\nL3\r\nL4");                  // two lines scrolled off; the 1-line history keeps L1 only
        Assert.Equal(1, t.Buffer.ScrollbackCount);
        Assert.Null(t.RowOf(l0));
        Assert.Equal(-1L, t.RowOf(l1));
    }

    [Fact]
    public void ThePlacementCursorMoveScrollMovesThePlacementItself()
    {
        using var t = new Rig();
        t.FillRows();
        t.Feed(Esc + "[5;1H");
        uint a = t.PutHere(rows: 3, extra: "");         // rows 4..6 overshoot the bottom: the screen scrolls by 1
        Assert.Equal(3L, t.RowOf(a));
        Assert.Equal("R4", t.Row(3));                   // the image sits on the line it was placed next to
        Assert.Equal((5, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    // -------------------------------------------------------------------------------------------- SU / SD

    [Fact]
    public void ScrollUpMovesPlacementsIntoTheHistory()
    {
        using var t = new Rig();
        uint a = t.Put(4, 0);
        uint b = t.Put(0, 0);
        t.Feed(Esc + "[2S");
        Assert.Equal(2L, t.RowOf(a));
        Assert.Equal(-2L, t.RowOf(b));
    }

    [Fact]
    public void ScrollDownPullsHistoryPlacementsBackAndPushesOthersOffTheBottom()
    {
        using var t = new Rig();
        uint a = t.Put(0, 0);
        uint c = t.Put(3, 0);
        t.Lf(3);
        Assert.Equal(-3L, t.RowOf(a));
        Assert.Equal(0L, t.RowOf(c));

        t.Feed(Esc + "[2T");
        Assert.Equal(-1L, t.RowOf(a));
        Assert.Equal(2L, t.RowOf(c));
        t.Feed(Esc + "[1T");
        Assert.Equal(0L, t.RowOf(a));
        t.Feed(Esc + "[3T");                            // c would land on row 6: below the 6-row screen
        Assert.Equal(3L, t.RowOf(a));
        Assert.Null(t.RowOf(c));
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void ReverseIndexAtTheTopRowMovesPlacementsDownAndRestoresHistory()
    {
        using var t = new Rig();
        uint a = t.Put(0, 0);
        t.Lf(2);
        Assert.Equal(-2L, t.RowOf(a));
        t.Feed(Esc + "[1;1H" + Esc + "M");
        Assert.Equal(-1L, t.RowOf(a));
        t.Feed(Esc + "M");
        Assert.Equal(0L, t.RowOf(a));
        t.Feed(Esc + "M");
        Assert.Equal(1L, t.RowOf(a));
    }

    [Fact]
    public void ReverseIndexBelowTheTopRowOnlyMovesTheCursor()
    {
        using var t = new Rig();
        uint a = t.Put(2, 0);
        t.Feed(Esc + "[3;1H" + Esc + "M");
        Assert.Equal(2L, t.RowOf(a));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void ScrollUpInsideMarginsMovesWholePlacementsAndCropsThemAtTheTopMargin()
    {
        using var t = new Rig();
        uint a = t.Put(2, 0, rows: 2);                  // rows 2..3, inside the margins (index 1..4)
        t.Feed(Esc + "[2;5r");
        t.Feed(Esc + "[1S");
        Assert.Equal(1L, t.RowOf(a));
        Assert.Equal(2, t.Find(a)!.EffRows);
        Assert.Equal(0L, t.Store.ViewTop);              // a partial region never rotates the ring

        t.Feed(Esc + "[1S");                            // would start at row 0, above the margin: one row cropped
        var p = t.Find(a)!;
        Assert.Equal(1L, t.RowOf(a));
        Assert.Equal(1, p.EffRows);
        Assert.Equal((20, 20), (p.SrcY, p.SrcH));       // the bottom half of the 40 px image is what stays

        t.Feed(Esc + "[1S");                            // nothing left inside the region
        Assert.Null(t.RowOf(a));
    }

    [Fact]
    public void ScrollDownInsideMarginsCropsAtTheBottomMarginAndDropsWhatLeavesIt()
    {
        using var t = new Rig();
        uint tall = t.Put(3, 0, rows: 2);               // rows 3..4: ends at the bottom margin
        uint one = t.Put(4, 3);                         // the last region row
        uint up = t.Put(1, 5);                          // the first region row
        t.Feed(Esc + "[2;5r");
        t.Feed(Esc + "[1T");

        var p = t.Find(tall)!;
        Assert.Equal(4L, t.RowOf(tall));
        Assert.Equal(1, p.EffRows);
        Assert.Equal((0, 20), (p.SrcY, p.SrcH));        // the top half stays
        Assert.Null(t.RowOf(one));                      // pushed off the bottom margin
        Assert.Equal(2L, t.RowOf(up));
    }

    [Fact]
    public void ScrollingAMarginRegionLeavesPlacementsOutsideAndStraddlingItAlone()
    {
        using var t = new Rig();
        uint above = t.Put(0, 0);
        uint below = t.Put(5, 0);
        uint straddlesTop = t.Put(0, 3, rows: 2);       // rows 0..1, the region starts at 1
        uint straddlesBottom = t.Put(4, 3, rows: 2);    // rows 4..5, the region ends at 4
        var before = t.Store.Placements.ToDictionary(p => p.Image.ClientId, p => (p.AnchorRow, p.Geometry));
        t.Feed(Esc + "[2;5r");

        t.Feed(Esc + "[2S");
        t.Feed(Esc + "[1T");
        foreach (uint id in new[] { above, below, straddlesTop, straddlesBottom })
        {
            Assert.Equal(before[id].AnchorRow, t.Find(id)!.AnchorRow);
            Assert.Equal(before[id].Geometry, t.Find(id)!.Geometry);
        }
    }

    [Fact]
    public void RectangleScrollMovesOnlyWhollyContainedPlacementsAndReturnsAffectedCount()
    {
        using var t = new Rig();
        uint inside = t.Put(2, 2, cols: 2);
        uint outside = t.Put(2, 0);
        uint straddlesLeft = t.Put(2, 1, cols: 2);
        uint straddlesRight = t.Put(2, 6, cols: 2);
        int changed = t.Store.OnRectangleScrolled(1, 4, 2, 6, 1, t.Buffer.Rows);

        Assert.Equal(1, changed);
        Assert.Equal(1L, t.RowOf(inside));
        Assert.Equal(2, t.Find(inside)!.AnchorCol);
        foreach (uint id in new[] { outside, straddlesLeft, straddlesRight })
            Assert.Equal(2L, t.RowOf(id));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void RectangleScrollCropsAndDeletesPlacementsAtVerticalEdges()
    {
        using var t = new Rig();
        uint cropped = t.Put(1, 2, rows: 2);
        uint deleted = t.Put(1, 4);

        Assert.Equal(2, t.Store.OnRectangleScrolled(1, 4, 2, 6, 1, t.Buffer.Rows));
        var p = t.Find(cropped)!;
        Assert.Equal(1L, t.RowOf(cropped));
        Assert.Equal(1, p.EffRows);
        Assert.Equal((20, 20), (p.SrcY, p.SrcH));
        Assert.Null(t.RowOf(deleted));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void FullWidthRegionScrollStillMovesHorizontallyOverhangingPlacements()
    {
        using var t = new Rig();
        uint overhanging = t.Put(2, 9, cols: 2);

        Assert.Equal(1, t.Store.OnRegionScrolled(1, 4, 1, t.Buffer.Rows));
        Assert.Equal(1L, t.RowOf(overhanging));
    }

    [Fact]
    public void HorizontalAndVerticalMarginScrollMovesOnlyContainedImagePlacements()
    {
        using var t = new Rig();
        uint inside = t.Put(2, 2);
        uint outside = t.Put(2, 0);

        t.Feed(Esc + "[?69h" + Esc + "[2;7s" + Esc + "[2;5r" + Esc + "[1S");

        Assert.Equal(1L, t.RowOf(inside));
        Assert.Equal(2L, t.RowOf(outside));
        Assert.Equal(0L, t.Store.ViewTop);
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void AScrollOfARegionTopAnchoredAtRowZeroDoesNotRotateTheRing()
    {
        using var t = new Rig();
        uint inside = t.Put(2, 0);
        uint outside = t.Put(5, 0);
        uint top = t.Put(0, 0);
        t.Feed(Esc + "[1;4r" + Esc + "[4;1H\n");        // margins rows 0..3, line feed at the bottom margin

        Assert.Equal(0L, t.Store.ViewTop);
        Assert.Equal(1L, t.RowOf(inside));
        Assert.Equal(5L, t.RowOf(outside));
        Assert.Null(t.RowOf(top));                      // moved above the region's top: deleted, not sent to history
    }

    [Fact]
    public void ReverseIndexAtTheTopMarginScrollsOnlyTheRegion()
    {
        using var t = new Rig();
        uint top = t.Put(1, 0);
        uint bottom = t.Put(4, 0);
        uint outside = t.Put(5, 0);
        t.Feed(Esc + "[2;5r" + Esc + "[2;1H" + Esc + "M");
        Assert.Equal(2L, t.RowOf(top));
        Assert.Null(t.RowOf(bottom));
        Assert.Equal(5L, t.RowOf(outside));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void ALargeRegionScrollThroughTheWholeRegionDeletesWhatWasInside()
    {
        using var t = new Rig();
        uint inside = t.Put(2, 0);
        uint outside = t.Put(0, 0);
        t.Feed(Esc + "[2;5r" + Esc + "[50S");
        Assert.Null(t.RowOf(inside));
        Assert.Equal(0L, t.RowOf(outside));
    }


    // --------------------------------------------------------------------------------------- erase / reset

    [Fact]
    public void EraseDisplayModesZeroAndOneLeavePlacementsAlone()
    {
        using var t = new Rig();
        uint a = t.Put(2, 0);
        t.Feed(Esc + "[2;1H" + Esc + "[0J");
        t.Feed(Esc + "[5;1H" + Esc + "[1J");
        t.Feed(Esc + "[3;1H" + Esc + "[2K");
        Assert.Equal(2L, t.RowOf(a));
    }

    [Fact]
    public void EraseDisplayTwoDeletesEveryPlacementIncludingTheHistoryOnes()
    {
        using var t = new Rig();
        uint inHistory = t.Put(0, 0);
        uint visible = t.Put(4, 0);
        t.Lf(2);
        Assert.Equal(-2L, t.RowOf(inHistory));

        t.Feed(Esc + "[2J");
        Assert.Equal(0, t.Store.PlacementCount);
        Assert.Null(t.RowOf(visible));
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void EraseDisplayTwoOnTheAlternateScreenClearsOnlyItsOwnPlacements()
    {
        using var t = new Rig();
        uint main = t.Put(1, 0);
        var mainStore = t.Buffer.MainImageStore!;
        t.Feed(Esc + "[?1049h");
        uint alt = t.Put(1, 0);
        t.Feed(Esc + "[2J");
        Assert.Equal(0, t.Store.PlacementCount);
        Assert.Equal(1L, t.RowOf(main, mainStore));
    }

    [Fact]
    public void ClearScrollbackDeletesHistoryPlacementsAndKeepsTheVisibleOnes()
    {
        using var t = new Rig();
        uint old = t.Put(0, 0);
        uint visible = t.Put(4, 0);
        t.Lf(2);
        uint added = t.Put(5, 0);
        Assert.Equal(-2L, t.RowOf(old));
        Assert.Equal(2L, t.RowOf(visible));

        t.Feed(Esc + "[3J");
        Assert.Null(t.RowOf(old));
        Assert.Equal(2L, t.RowOf(visible));
        Assert.Equal(5L, t.RowOf(added));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void ClearScrollbackKeepsAPlacementThatStillReachesIntoTheVisibleScreen()
    {
        using var t = new Rig();
        uint straddler = t.Put(4, 0, rows: 2);          // rows 4..5
        t.Lf(5);                                        // rows -1..0
        Assert.Equal(-1L, t.RowOf(straddler));
        t.Feed(Esc + "[3J");
        Assert.Equal(-1L, t.RowOf(straddler));          // its last row is visible: kept
        t.Lf();                                         // now wholly in the history
        Assert.Equal(-2L, t.RowOf(straddler));
    }

    [Fact]
    public void ClearScrollbackOnTheAlternateScreenLeavesTheMainStoreAlone()
    {
        using var t = new Rig();
        uint main = t.Put(0, 0);
        t.Lf(2);
        var mainStore = t.Buffer.MainImageStore!;
        t.Feed(Esc + "[?1049h");
        t.PutHere();
        t.Feed(Esc + "[3J");
        Assert.Equal(-2L, t.RowOf(main, mainStore));
    }

    [Fact]
    public void FullResetDeletesEveryImageOfBothScreensAndKeepsTheLimits()
    {
        using var t = new Rig(options: new KittyGraphicsOptions
        {
            Enabled = true,
            StorageLimitBytes = 123_456,
            MaxPlacementsPerScreen = 7,
        });
        uint main = t.Put(1, 0);
        t.Lf(2);
        t.Feed(Esc + "[?1049h");
        t.Put(2, 0);
        var mainStore = t.Buffer.MainImageStore!;
        var altStore = t.Buffer.AlternateImageStore!;

        t.Feed(Esc + "c");
        Assert.Equal((0, 0), (mainStore.Count, mainStore.PlacementCount));
        Assert.Equal((0, 0), (altStore.Count, altStore.PlacementCount));
        Assert.False(t.Buffer.IsAlternateScreenActive);
        Assert.Equal(123_456, t.Store.StorageLimitBytes);
        Assert.Equal(7, t.Store.MaxPlacements);

        // The screen works again afterwards.
        uint again = t.Put(0, 0);
        Assert.Equal(0L, t.RowOf(again));
        Assert.NotNull(t.Store.FindByClientId(again));
        mainStore.ValidateInvariants();
        altStore.ValidateInvariants();
    }

    [Fact]
    public void SoftResetLeavesImagesAndPlacementsUntouched()
    {
        using var t = new Rig();
        uint a = t.Put(2, 3, rows: 2);
        t.Lf(2);
        var before = t.Find(a)!;
        long row = before.AnchorRow;
        var geometry = before.Geometry;

        t.Feed(Esc + "[!p");
        Assert.Equal(1, t.Store.Count);
        Assert.Same(before, t.Find(a));
        Assert.Equal(row, t.Find(a)!.AnchorRow);
        Assert.Equal(geometry, t.Find(a)!.Geometry);
    }

    [Fact]
    public void TheBufferLevelClearScreenDeletesPlacementsOfBothScreens()
    {
        using var t = new Rig();
        uint main = t.Put(1, 0);
        t.Feed(Esc + "[?1049h");
        t.Put(1, 0);
        var mainStore = t.Buffer.MainImageStore!;
        var altStore = t.Buffer.AlternateImageStore!;
        t.Buffer.ClearScreen();
        Assert.Equal(0, mainStore.PlacementCount);
        Assert.Equal(0, altStore.PlacementCount);
    }

    // ---------------------------------------------------------------------------------------- alternate screen

    [Theory]
    [InlineData("?1049")]
    [InlineData("?47")]
    [InlineData("?1047")]
    public void TheAlternateScreenHidesMainPlacementsAndLeavingRestoresThemUntouched(string mode)
    {
        using var t = new Rig();
        uint main = t.Put(2, 0);
        var mainStore = t.Buffer.MainImageStore!;
        long mainViewTop = mainStore.ViewTop;

        t.Feed(Esc + "[" + mode + "h");
        Assert.True(t.Buffer.IsAlternateScreenActive);
        using (var s = t.Capture())
            Assert.Null(s.Graphics);                    // the main screen's placements are not drawn

        uint alt = t.Put(1, 0);
        using (var s = t.Capture())
        {
            var p = Assert.Single(s.Graphics!.Placements.ToArray());
            Assert.Equal((alt, 1), (p.ImageId, p.ViewRow));
        }

        t.Lf(10);                                       // alt scrolling never touches the main store
        Assert.Equal(2L, t.RowOf(main, mainStore));
        Assert.Equal(mainViewTop, mainStore.ViewTop);
        Assert.Equal(-9L, t.RowOf(alt));

        t.Feed(Esc + "[" + mode + "l");
        using (var s = t.Capture())
        {
            var p = Assert.Single(s.Graphics!.Placements.ToArray());
            Assert.Equal((main, 2), (p.ImageId, p.ViewRow));
        }

        mainStore.ValidateInvariants();
    }

    [Fact]
    public void Mode47RestoresAlternatePlacementsButMode1047ClearsThem()
    {
        using var t = new Rig();
        uint main = t.Put(2, 0);
        t.Feed(Esc + "[?47h");
        uint alternate = t.Put(1, 0);

        t.Feed(Esc + "[?47l");
        using (var snapshot = t.Capture())
        {
            var placement = Assert.Single(snapshot.Graphics!.Placements.ToArray());
            Assert.Equal((main, 2), (placement.ImageId, placement.ViewRow));
        }

        t.Feed(Esc + "[?47h");
        using (var snapshot = t.Capture())
        {
            var placement = Assert.Single(snapshot.Graphics!.Placements.ToArray());
            Assert.Equal((alternate, 1), (placement.ImageId, placement.ViewRow));
        }

        t.Feed(Esc + "[?1047l" + Esc + "[?47h");
        using var cleared = t.Capture();
        Assert.Null(cleared.Graphics);
    }

    [Fact]
    public void ScrollingMainTextBeforeAndAfterTheAlternateScreenKeepsTheMainPlacementAttached()
    {
        using var t = new Rig();
        t.FillRows();
        uint a = t.Put(4, 0);
        t.Lf(1);
        Assert.Equal(3L, t.RowOf(a));
        t.Feed(Esc + "[?1049h");
        t.Lf(4);
        t.Feed(Esc + "[?1049l");
        Assert.Equal(3L, t.RowOf(a));
        t.Lf(1);
        Assert.Equal(2L, t.RowOf(a));
        Assert.Equal("R4", t.Row(2));
    }

    [Theory]
    [InlineData("?1049")]
    [InlineData("?47")]
    [InlineData("?1047")]
    public void EnteringTheAlternateScreenAbortsAnOpenChunkedTransfer(string mode)
    {
        using var t = new Rig();
        t.Apc("a=T,i=77,s=1,v=2,f=24,m=1,c=1,r=1", "AAAA");   // first of two chunks
        t.Feed(Esc + "[" + mode + "h");
        t.Apc("m=0", "AAAA");                                   // the rest arrives on the other screen
        Assert.Equal(0, t.Buffer.MainImageStore?.Count ?? 0);
        Assert.Equal(0, t.Buffer.AlternateImageStore?.Count ?? 0);
        Assert.Equal(0, t.Store.PlacementCount);
    }

    [Theory]
    [InlineData("?1049")]
    [InlineData("?47")]
    [InlineData("?1047")]
    public void LeavingTheAlternateScreenAbortsAnOpenChunkedTransfer(string mode)
    {
        using var t = new Rig();
        t.Feed(Esc + "[" + mode + "h");
        t.Apc("a=T,i=77,s=1,v=2,f=24,m=1,c=1,r=1", "AAAA");
        t.Feed(Esc + "[" + mode + "l");
        t.Apc("m=0", "AAAA");
        Assert.Equal(0, t.Buffer.MainImageStore?.Count ?? 0);
        Assert.Equal(0, t.Buffer.AlternateImageStore?.Count ?? 0);
    }

    [Fact]
    public void ASwitchThatChangesNothingKeepsAnOpenChunkedTransferAlive()
    {
        using var t = new Rig();
        t.Apc("a=T,i=77,s=1,v=2,f=24,m=1,c=1,r=1", "AAAA");
        t.Feed(Esc + "[?1049l" + Esc + "[?47l");               // already on the main screen: no switch
        t.Apc("m=0", "AAAA");
        Assert.NotNull(t.Store.FindByClientId(77));
        Assert.Equal(1, t.Store.PlacementCount);
    }

    // ----------------------------------------------------------------------------------------------- reflow

    private static void WriteTwentyLetters(Rig t) => t.Feed("ABCDEFGHIJKLMNOPQRST");

    [Fact]
    public void WideningMergesASoftWrappedLineAndTheAnchorFollowsItsCharacter()
    {
        using var t = new Rig(4, 10);
        WriteTwentyLetters(t);                          // row 0 "ABCDEFGHIJ", row 1 "KLMNOPQRST" (soft wrap)
        uint a = t.Put(1, 3);                           // on "N"
        Assert.Equal((1L, 3), t.CellOf(a));

        t.Buffer.Resize(4, 20);
        Assert.Equal((0L, 13), t.CellOf(a));
        Assert.Equal("ABCDEFGHIJKLMNOPQRST", t.Row(0));
        Assert.Equal(0L, t.Store.ViewTop);
    }

    [Fact]
    public void NarrowingSplitsTheLineAndTheAnchorLandsOnTheRowHoldingItsCharacter()
    {
        using var t = new Rig(4, 10);
        WriteTwentyLetters(t);
        uint a = t.Put(1, 3);                           // "N" is offset 13 in the logical line

        t.Buffer.Resize(4, 5);                          // rows "ABCDE" "FGHIJ" "KLMNO" "PQRST"
        Assert.Equal((2L, 3), t.CellOf(a));
        Assert.Equal("KLMNO", t.Row(2));
    }

    [Fact]
    public void NarrowingBelowTheScreenHeightPushesTheTopOfTheLineIntoHistoryAndTheAnchorFollows()
    {
        using var t = new Rig(4, 10, 100);
        WriteTwentyLetters(t);
        uint a = t.Put(1, 3);
        uint first = t.Put(0, 2);                       // "C", offset 2

        t.Buffer.Resize(4, 4);                          // 5 rows: one goes to the history ("ABCD")
        Assert.Equal(1L, t.Store.ViewTop);
        Assert.Equal((2L, 1), t.CellOf(a));             // offset 13: row "MNOP" is output row 3, visible row 2
        Assert.Equal((-1L, 2), t.CellOf(first));        // offset 2 sits in "ABCD", now history
        Assert.Equal("MNOP", t.Row(2));
    }

    [Fact]
    public void ARowOnATrimmedLineIsDeletedWhenTheHistoryNoLongerFits()
    {
        using var t = new Rig(4, 10, 0);
        WriteTwentyLetters(t);
        uint kept = t.Put(1, 3);
        uint trimmed = t.Put(0, 2);                     // lands in "ABCD", the row that is dropped without history

        t.Buffer.Resize(4, 4);
        Assert.Equal((2L, 1), t.CellOf(kept));
        Assert.Null(t.RowOf(trimmed));
        Assert.Equal(0L, t.Store.ViewTop);
        t.Store.ValidateInvariants();
    }

    [Fact]
    public void HistoryPlacementsKeepTheirLineAcrossAResizeThatAddsRows()
    {
        using var t = new Rig(3, 10, 100);
        t.Feed("L0\r\nL1");
        uint a = t.PutHere();                           // on "L1"
        t.Feed("\r\nL2\r\nL3\r\nL4");
        Assert.Equal(-1L, t.RowOf(a));                  // history L0 L1, screen L2 L3 L4: "L1" is the row above the screen

        t.Buffer.Resize(3, 20);                         // same height, wider: nothing wraps, history unchanged
        Assert.Equal(-1L, t.RowOf(a));

        t.Buffer.Resize(5, 20);                         // two history lines come back into view
        Assert.Equal(1L, t.RowOf(a));
        Assert.Equal("L1", t.Row(1));
    }

    [Fact]
    public void ShrinkingTheHeightMovesTheTopOfTheScreenIntoHistoryAndKeepsPlacementsOnTheirLines()
    {
        using var t = new Rig(6, 10, 100);
        t.FillRows();
        uint r1 = t.Put(1, 0);
        uint r4 = t.Put(4, 0);

        t.Buffer.Resize(3, 10);                         // R0..R2 become history, R3..R5 stay visible
        Assert.Equal(-2L, t.RowOf(r1));
        Assert.Equal(1L, t.RowOf(r4));
        Assert.Equal("R4", t.Row(1));
        Assert.Equal(3L, t.Store.ViewTop);
    }

    [Fact]
    public void ShrinkingTheHeightWithoutHistoryDropsThePlacementsOnLinesThatFellOff()
    {
        using var t = new Rig(6, 10, 0);
        t.FillRows();
        uint r1 = t.Put(1, 0);
        uint r4 = t.Put(4, 0);

        t.Buffer.Resize(3, 10);
        Assert.Null(t.RowOf(r1));
        Assert.Equal(1L, t.RowOf(r4));
        Assert.Equal("R4", t.Row(1));
    }

    [Fact]
    public void ABlankRowPlacementSurvivesAResizeAtTheSameRow()
    {
        using var t = new Rig(6, 10, 100);
        t.Feed("text");
        uint a = t.Put(3, 7);                           // on an empty row below the text
        t.Buffer.Resize(6, 20);
        Assert.Equal((3L, 7), t.CellOf(a));
        t.Buffer.Resize(6, 5);
        Assert.Equal((3L, 4), t.CellOf(a));             // the column is clamped to the new width
    }

    [Fact]
    public void ResizeRebasesTheHiddenMainStoreAndTheAlternateStoreIndependently()
    {
        using var t = new Rig(4, 10);
        t.Feed("Hello");
        uint main = t.Put(0, 2);
        var mainStore = t.Buffer.MainImageStore!;
        t.Feed(Esc + "[?1049h" + Esc + "[H");
        WriteTwentyLetters(t);
        uint alt = t.Put(1, 3);

        t.Buffer.Resize(4, 20);
        Assert.Equal((0L, 13), t.CellOf(alt));
        Assert.Equal((0L, 2), t.CellOf(main, mainStore));

        t.Buffer.Resize(4, 5);
        Assert.Equal((2L, 3), t.CellOf(alt));
        Assert.Equal((0L, 2), t.CellOf(main, mainStore));
        t.Feed(Esc + "[?1049l");
        Assert.Equal((0L, 2), t.CellOf(main));
        mainStore.ValidateInvariants();
    }

    [Fact]
    public void AResizeWithoutPlacementsCreatesNoStore()
    {
        using var t = new Rig();
        t.Feed("a\r\nb");
        t.Buffer.Resize(8, 20);
        t.Buffer.Resize(3, 5);
        t.Feed(Esc + "[?1049h");
        t.Buffer.Resize(6, 10);
        Assert.Null(t.Buffer.MainImageStore);
        Assert.Null(t.Buffer.AlternateImageStore);
    }

    [Fact]
    public void AFailedReflowDeletesEveryPlacementInsteadOfLeavingStrandedAnchors()
    {
        using var t = new Rig(4, 10);
        WriteTwentyLetters(t);
        uint main = t.Put(1, 3);
        var mainStore = t.Buffer.MainImageStore!;
        t.Feed(Esc + "[?1049h" + Esc + "[H");
        t.Put(2, 0);
        var altStore = t.Buffer.AlternateImageStore!;
        Assert.Equal((1, 1), (mainStore.PlacementCount, altStore.PlacementCount));

        // The failure strikes after both stores captured their anchors and before the screens were reflowed.
        t.Buffer.ReflowFaultInjectorForTests = () => throw new InvalidOperationException("injected");
        ulong generation = t.Buffer.Generation;
        Assert.Throws<InvalidOperationException>(() => t.Buffer.Resize(4, 5));
        t.Buffer.ReflowFaultInjectorForTests = null;

        Assert.Equal((0, 0), (mainStore.PlacementCount, altStore.PlacementCount));
        Assert.NotEqual(generation, t.Buffer.Generation);   // the display is told the placements went
        Assert.Equal((4, 10), (t.Buffer.Rows, t.Buffer.Columns));
        mainStore.ValidateInvariants();
        altStore.ValidateInvariants();

        // Nothing is stranded: leaving the alternate screen and resizing for real re-bases a fresh placement.
        t.Feed(Esc + "[?1049l");
        uint again = t.Put(1, 3);
        t.Buffer.Resize(4, 20);
        Assert.Equal((0L, 13), t.CellOf(again));
        Assert.Null(t.Find(main));
        mainStore.ValidateInvariants();
    }

    [Fact]
    public void AFailedReflowThroughTheAdapterIsSwallowedAndAlsoClearsThePlacements()
    {
        using var t = new Rig(4, 10);
        uint a = t.Put(1, 3);
        t.Buffer.ReflowFaultInjectorForTests = () => throw new InvalidOperationException("injected");
        t.Adapter.ResizeBuffer(4, 5);                    // ResizeBuffer swallows the exception
        t.Buffer.ReflowFaultInjectorForTests = null;
        Assert.Null(t.Find(a));
        Assert.Equal(0, t.Store.PlacementCount);
    }

    // ------------------------------------------------------------------------------------------ cell size

    [Fact]
    public void ACellSizeChangeReResolvesNativeSizePlacementsOnBothScreens()
    {
        using var t = new Rig();
        uint main = t.PutNative(1, 0, 20, 40);          // 2 x 2 cells at 10x20
        var mainStore = t.Buffer.MainImageStore!;
        Assert.Equal((2, 2), (t.Find(main)!.EffCols, t.Find(main)!.EffRows));
        t.Feed(Esc + "[?1049h");
        uint alt = t.PutNative(0, 0, 40, 20);           // 4 x 1 cells
        Assert.Equal((4, 1), (t.Find(alt)!.EffCols, t.Find(alt)!.EffRows));

        t.Adapter.SetCellPixelSize(20, 20);
        Assert.Equal((2, 1), (t.Find(alt)!.EffCols, t.Find(alt)!.EffRows));
        Assert.Equal((1, 2), (t.Find(main, mainStore)!.EffCols, t.Find(main, mainStore)!.EffRows));

        t.Adapter.SetCellPixelSize(5, 10);
        Assert.Equal((8, 2), (t.Find(alt)!.EffCols, t.Find(alt)!.EffRows));
        Assert.Equal((4, 4), (t.Find(main, mainStore)!.EffCols, t.Find(main, mainStore)!.EffRows));
        Assert.Equal(1L, t.RowOf(main, mainStore));     // positions do not change
    }

    [Fact]
    public void ACellSizeChangeKeepsExplicitCellBoxesAndClampsInCellOffsets()
    {
        using var t = new Rig();
        uint boxed = t.Put(1, 0, cols: 3, rows: 2, extra: "C=1,X=9");
        Assert.Equal(9, t.Find(boxed)!.CellXOff);

        t.Adapter.SetCellPixelSize(5, 20);
        var p = t.Find(boxed)!;
        Assert.Equal((3, 2), (p.EffCols, p.EffRows));
        Assert.Equal(4, p.CellXOff);                    // below the new cell width
    }

    [Fact]
    public void ACellSizeChangeBumpsTheGenerationOnlyWhenSomethingChanged()
    {
        using var t = new Rig();
        t.Feed("x");
        ulong start = t.Buffer.Generation;
        t.Adapter.SetCellPixelSize(7, 9);               // no placements anywhere: nothing to redraw
        Assert.Equal(start, t.Buffer.Generation);
        Assert.Null(t.Buffer.MainImageStore);

        t.PutNative(1, 0, 20, 40);
        ulong placed = t.Buffer.Generation;
        t.Adapter.SetCellPixelSize(7, 9);               // same size again
        Assert.Equal(placed, t.Buffer.Generation);
        t.Adapter.SetCellPixelSize(8, 9);
        Assert.NotEqual(placed, t.Buffer.Generation);
    }

    // ----------------------------------------------------------------------------------- misc / lifecycle

    [Fact]
    public void WithoutGraphicsUseNoStoreIsCreatedByScrollingErasingResettingOrResizing()
    {
        using var t = new Rig();
        t.FillRows();
        t.Lf(3);
        t.Feed(Esc + "[2S" + Esc + "[3T" + Esc + "M" + Esc + "[2;5r" + Esc + "[2S" + Esc + "[2L" + Esc + "[3J" + Esc + "[2J" + Esc + "[!p");
        t.Buffer.Resize(5, 7);
        t.Adapter.SetCellPixelSize(12, 24);
        t.Feed(Esc + "c");
        Assert.Null(t.Buffer.MainImageStore);
        Assert.Null(t.Buffer.AlternateImageStore);
    }

    [Fact]
    public void DisposingTheAdapterReleasesEveryImageAndPlacementTheHooksTouched()
    {
        var baseline = new GraphicsBaseline();
        long placementsBefore = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        var t = new Rig();
        t.Put(0, 0);
        t.Put(3, 0, rows: 2);
        t.Lf(2);
        t.Feed(Esc + "[?1049h");
        t.Put(1, 0);
        t.Buffer.Resize(8, 4);
        Assert.True(GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore > 0);

        t.Dispose();
        Assert.Equal(0, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount - placementsBefore);
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------------------------------ fuzz

    private static void AssertConsistent(Rig t)
    {
        var buffer = t.Buffer;
        foreach (var store in new[] { buffer.MainImageStore, buffer.AlternateImageStore })
        {
            if (store is null)
                continue;
            store.ValidateInvariants();
            foreach (var p in store.Placements)
            {
                Assert.True(p.EffRows > 0 && p.EffCols > 0, "empty placement kept");
                Assert.True(p.EndRow > store.ViewTop - t.Capacity,
                    $"placement ending at {p.EndRow} is older than the history (view top {store.ViewTop}, capacity {t.Capacity})");
                Assert.True(p.AnchorRow < store.ViewTop + buffer.Rows,
                    $"placement at {p.AnchorRow} is below the screen (view top {store.ViewTop}, rows {buffer.Rows})");
                Assert.True(p.AnchorCol >= 0, "negative column");
            }
        }
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(3, 20)]
    [InlineData(4, 0)]
    [InlineData(5, 3)]
    [InlineData(6, 20)]
    [InlineData(7, 5)]
    [InlineData(8, 100)]
    public void RandomOperationSequencesKeepTheStoresAndTheBufferConsistent(int seed, int capacity)
    {
        var baseline = new GraphicsBaseline();
        var random = new Random(seed);
        var t = new Rig(6, 10, capacity);
        try
        {
            for (int step = 0; step < 600; step++)
            {
                int rows = t.Buffer.Rows, columns = t.Buffer.Columns;
                switch (random.Next(16))
                {
                    case 0:
                    case 1:
                    case 2:
                        t.Put(random.Next(rows), random.Next(columns), random.Next(1, 4), random.Next(1, 4),
                            random.Next(2) == 0 ? "C=1" : "");
                        break;
                    case 3:
                        t.Lf(random.Next(1, 4));
                        break;
                    case 4:
                        t.Feed("line" + step + "\r\n");
                        break;
                    case 5:
                        t.Feed(Esc + "[" + random.Next(1, 9) + "S");
                        break;
                    case 6:
                        t.Feed(Esc + "[" + random.Next(1, 9) + "T");
                        break;
                    case 7:
                        {
                            int top = random.Next(1, rows), bottom = random.Next(top + 1, rows + 1);
                            t.Feed(random.Next(3) == 0 ? Esc + "[1;" + rows + "r" : Esc + "[" + top + ";" + bottom + "r");
                            break;
                        }
                    case 8:
                        t.Feed(Esc + "[" + random.Next(1, rows + 1) + ";1H" + Esc + "[" + random.Next(1, 4) + (random.Next(2) == 0 ? "L" : "M"));
                        break;
                    case 9:
                        t.Feed(Esc + "[" + random.Next(1, rows + 1) + ";1H" + Esc + "M");
                        break;
                    case 10:
                        t.Feed(Esc + "[" + random.Next(4) + "J");
                        break;
                    case 11:
                        t.Buffer.Resize(random.Next(3, 9), random.Next(4, 13));
                        break;
                    case 12:
                        t.Feed(Esc + (t.Buffer.IsAlternateScreenActive ? "[?1049l" : (random.Next(2) == 0 ? "[?1049h" : "[?47h")));
                        break;
                    case 13:
                        t.Adapter.SetCellPixelSize(random.Next(5, 15), random.Next(10, 25));
                        break;
                    case 14:
                        t.Feed(random.Next(4) == 0 ? Esc + "c" : Esc + "[!p");
                        break;
                    default:
                        t.PutNative(random.Next(rows), random.Next(columns), random.Next(1, 21), random.Next(1, 41));
                        break;
                }
                AssertConsistent(t);
            }
        }
        finally
        {
            t.Dispose();
        }
        baseline.AssertRestored();
    }
}

/// <summary>The hooks must stay off the allocation-free hot paths while no placement exists.</summary>
[Collection("Allocation-sensitive tests")]
public sealed class KittyBufferHooksAllocationTests
{
    [Fact]
    public void OutputAllocatesNothingWithGraphicsEnabledAndNoImages()
    {
        var adapter = new TerminalAdapter(rows: 8, columns: 80, scrollbackCapacity: 32);
        adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, null);
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] line = Encoding.ASCII.GetBytes("\u001b[31mallocation-free terminal line\u001b[0m\r\n");

        for (int i = 0; i < 128; i++)
            parser.Feed(line);
        Assert.Equal(32, adapter.Buffer.ScrollbackCount);

        AllocationAssert.NoAllocations(() => parser.Feed(line), warmupIterations: 32, measuredIterationsPerWindow: 256, windows: 5);
        Assert.Null(adapter.Buffer.MainImageStore);
    }

    [Fact]
    public void ScrollPathsAllocateNothingWithAnEmptyImageStore()
    {
        var buffer = new TerminalBuffer(rows: 8, columns: 80, scrollbackCapacity: 32);
        _ = buffer.ActiveImageStore;                    // the store exists but holds no placement

        void Cycle()
        {
            buffer.SetCursor(7, 0);
            buffer.LineFeed();                          // full-screen ring rotation
            buffer.ScrollUpLines(2);
            buffer.ScrollDownLines(1);
            buffer.SetScrollRegion(2, 6);
            buffer.ScrollUpLines(1);                    // partial region
            buffer.ScrollDownLines(1);
            buffer.SetScrollRegion(1, 8);
            buffer.SetCursor(0, 0);
            buffer.ReverseIndex();
        }

        for (int i = 0; i < 64; i++)
            Cycle();

        AllocationAssert.NoAllocations(Cycle, warmupIterations: 32, measuredIterationsPerWindow: 128, windows: 5);
        Assert.Equal(0, buffer.ActiveImageStore.PlacementCount);
        buffer.Dispose();
    }

    [Fact]
    public void ResizeAllocatesNothingWithAnEmptyImageStoreOnBothScreens()
    {
        var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 256);
        string wrapped = new string('x', 120);
        for (int i = 0; i < 160; i++)
        {
            buffer.WriteText(wrapped.AsSpan(), CellAttributes.Default);
            buffer.CarriageReturn();
            buffer.LineFeed();
        }
        _ = buffer.ActiveImageStore;
        buffer.SetAlternateScreen(true);
        _ = buffer.ActiveImageStore;
        buffer.WriteText(wrapped.AsSpan(), CellAttributes.Default);

        for (int i = 0; i < 8; i++)
        {
            buffer.Resize(30, 100);
            buffer.Resize(24, 80);
        }

        AllocationAssert.NoAllocations(
            () =>
            {
                buffer.Resize(30, 100);
                buffer.Resize(24, 80);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 64,
            windows: 5);
        buffer.Dispose();
    }

    [Fact]
    public void ScrollPathsAllocateNothingWhilePlacementsExist()
    {
        var buffer = new TerminalBuffer(rows: 8, columns: 80, scrollbackCapacity: 5000);
        var store = buffer.ActiveImageStore;
        var image = store.AddImage(10, 20, clientId: 1);
        for (int n = 1; n <= 40; n++)
            store.Place(image, $"p={n},c=1,r=1", store.ViewTop + (n % 8), n, 10, 20);

        void Cycle()
        {
            buffer.SetCursor(7, 0);
            buffer.LineFeed();                          // ring rotation: ViewTop and the end-row bound move
            buffer.ScrollUpLines(2);
            buffer.ScrollDownLines(1);                  // ring rotated back: the below-the-screen scan
            buffer.SetScrollRegion(2, 6);
            buffer.ScrollUpLines(1);                    // margin scroll: move / crop / delete
            buffer.ScrollDownLines(1);
            buffer.SetScrollRegion(1, 8);
            buffer.SetCursor(0, 0);
            buffer.ReverseIndex();
        }

        for (int i = 0; i < 16; i++)
            Cycle();

        AllocationAssert.NoAllocations(Cycle, warmupIterations: 0, measuredIterationsPerWindow: 64, windows: 5);
        Assert.True(store.HasPlacements);               // the history placements are still held: the paths were exercised
        store.ValidateInvariants();
        buffer.Dispose();
    }

    [Fact]
    public void ResizeWithPlacementsOnBothScreensAllocatesNothingAfterWarmup()
    {
        var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 256);
        string wrapped = new string('x', 120);
        for (int i = 0; i < 160; i++)
        {
            buffer.WriteText(wrapped.AsSpan(), CellAttributes.Default);
            buffer.CarriageReturn();
            buffer.LineFeed();
        }
        var main = buffer.ActiveImageStore;
        var mainImage = main.AddImage(10, 20, clientId: 1);
        for (int n = 1; n <= 12; n++)
            main.Place(mainImage, $"p={n},c=1,r=1", main.ViewTop + n, n, 10, 20);

        buffer.SetAlternateScreen(true);
        buffer.WriteText(wrapped.AsSpan(), CellAttributes.Default);
        var alt = buffer.ActiveImageStore;
        var altImage = alt.AddImage(10, 20, clientId: 1);
        alt.Place(altImage, "p=1,c=1,r=1", alt.ViewTop, 5, 10, 20);
        alt.Place(altImage, "p=2,c=1,r=1", alt.ViewTop + 1, 3, 10, 20);

        for (int i = 0; i < 8; i++)
        {
            buffer.Resize(30, 100);
            buffer.Resize(24, 80);
        }
        Assert.True(main.HasPlacements && alt.HasPlacements);

        AllocationAssert.NoAllocations(
            () =>
            {
                buffer.Resize(30, 100);
                buffer.Resize(24, 80);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 64,
            windows: 5);
        Assert.True(main.HasPlacements && alt.HasPlacements);
        main.ValidateInvariants();
        alt.ValidateInvariants();
        buffer.Dispose();
    }
}
