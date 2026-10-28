using System;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class ImageQuadBuilderTests
{
    private static ImageSceneGeometry Scene(
        float padL = 0, float padT = 0, int colOff = 0, int rowOff = 0, float cw = 10f, float ch = 20f,
        float cl = 0, float ct = 0, float cr = 800, float cb = 600)
        => new(padL, padT, colOff, rowOff, cw, ch, cl, ct, cr, cb);

    private static KittyRenderPlacement P(ImagePixels px, int col = 0, int row = 0, int cols = 1, int rows = 1,
        float ox = 0, float oy = 0, int z = 0, uint img = 1, uint pl = 0,
        float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
        => new()
        {
            Pixels = px, ImageSerial = px.Serial, Col = col, ViewRow = row, Cols = cols, Rows = rows,
            OffXCells = ox, OffYCells = oy, U0 = u0, V0 = v0, U1 = u1, V1 = v1, Z = z, ImageId = img, PlacementId = pl,
        };

    private static ImagePixels Px() => ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);

    private static ImageDrawList Build(in ImageSceneGeometry g, params KittyRenderPlacement[] ps)
    {
        var list = new ImageDrawList();
        ImageQuadBuilder.Append(list, ps, g);
        return list;
    }

    [Fact]
    public void PaneOffsets_PaddingAndCells_PlaceQuad()
    {
        using var px = Px();
        var l = Build(Scene(padL: 5, padT: 3, colOff: 2, rowOff: 1), P(px, col: 3, row: 2, cols: 4, rows: 2));
        var q = Assert.Single(l.Quads.AsSpan(0, l.QuadCount).ToArray());
        Assert.Equal((55f, 63f, 40f, 40f), (q.X, q.Y, q.W, q.H));
        Assert.Equal((0f, 0f, 1f, 1f), (q.U0, q.V0, q.U1, q.V1));
        Assert.Equal(1f, q.Alpha);
        l.Clear();
    }

    [Fact]
    public void NegativeViewRow_ClipsTopWithExactUv()
    {
        using var px = Px();
        var l = Build(Scene(), P(px, row: -1, cols: 2, rows: 3, v0: 0f, v1: 1f));
        var q = l.Quads[0];
        Assert.Equal((0f, 0f, 20f, 40f), (q.X, q.Y, q.W, q.H));
        Assert.Equal(1f / 3f, q.V0, 6);
        Assert.Equal(1f, q.V1);
        l.Clear();
    }

    [Fact]
    public void SourceSubRect_IsInterpolatedNotReplaced()
    {
        using var px = Px();
        // placement shows u 0.25..0.75; clipping its top half => v only from the middle of its own range
        var l = Build(Scene(ct: 20), P(px, row: 0, cols: 2, rows: 2, u0: .25f, u1: .75f, v0: .5f, v1: 1f));
        var q = l.Quads[0];
        Assert.Equal((.25f, .75f), (q.U0, q.U1));
        Assert.Equal(.75f, q.V0, 6);
        Assert.Equal(1f, q.V1, 6);
        Assert.Equal((0f, 20f, 20f, 20f), (q.X, q.Y, q.W, q.H));
        l.Clear();
    }

    [Fact]
    public void WiderThanClip_ShrinksRightAndUv()
    {
        using var px = Px();
        var l = Build(Scene(cr: 100), P(px, col: 8, cols: 4, rows: 1));
        var q = l.Quads[0];
        Assert.Equal((80f, 0f, 20f, 20f), (q.X, q.Y, q.W, q.H));
        Assert.Equal((0f, 0.5f), (q.U0, q.U1));
        l.Clear();
    }

    [Fact]
    public void TallerThanClip_ShrinksBottom()
    {
        using var px = Px();
        var l = Build(Scene(cb: 30), P(px, row: 1, cols: 1, rows: 4));
        var q = l.Quads[0];
        Assert.Equal((0f, 20f, 10f, 10f), (q.X, q.Y, q.W, q.H));
        Assert.Equal(0.125f, q.V1);
        l.Clear();
    }

    [Fact]
    public void SplitPane_ClipsLeftOverhang()
    {
        using var px = Px();
        // right pane starts at col 40 (x=400); a placement at pane col -2 overhangs 20px to the left
        var l = Build(Scene(colOff: 40, cl: 400, cr: 800), P(px, col: -2, cols: 4, rows: 1));
        var q = l.Quads[0];
        Assert.Equal((400f, 20f, 0.5f), (q.X, q.W, q.U0));
        Assert.Equal(1f, q.U1);
        l.Clear();
    }

    [Fact]
    public void FractionalCellSize_SnapsAllFourEdgesLikeCellBackgrounds()
    {
        using var px = Px();
        var l = Build(Scene(cw: 7.5f, ch: 15.25f), P(px, col: 3, row: 2, cols: 2, rows: 2));
        var q = l.Quads[0];
        // left 22.5 -> 23, right 37.5 -> 38, top 30.5 -> 31, bottom 61.0 -> 61 (round half up on both edges)
        Assert.Equal((23f, 31f, 15f, 30f), (q.X, q.Y, q.W, q.H));
        l.Clear();
    }

    [Fact]
    public void NativeSize_ScalesByCellOverBasis()
    {
        using var px = Px();
        var native = new KittyRenderPlacement
        {
            Pixels = px, ImageSerial = px.Serial, Cols = 3, Rows = 4, U1 = 1, V1 = 1,
            NativeSize = true, NativeW = 30, NativeH = 61, BasisCellW = 10, BasisCellH = 20,
        };
        var l = Build(Scene(cw: 7.5f, ch: 15.25f), native);
        var q = l.Quads[0];
        Assert.Equal(23f, q.W);                          // 22.5 wide from x 0 -> right edge 23
        Assert.Equal(47f, q.H);                          // 61 * 15.25 / 20 = 46.5125 -> bottom edge 47
        l.Clear();
    }

    [Fact]
    public void NativeSize_WithZeroBasis_IsDropped()
    {
        using var px = Px();
        var bad = new KittyRenderPlacement { Pixels = px, ImageSerial = px.Serial, Cols = 1, Rows = 1, U1 = 1, V1 = 1, NativeSize = true, NativeW = 5, NativeH = 5 };
        Assert.Equal(0, Build(Scene(), bad).DrawCount);
    }

    [Fact]
    public void SubCellOffsets_AreFractionsOfCell_ThenRounded()
    {
        using var px = Px();
        var l = Build(Scene(), P(px, col: 1, row: 1, cols: 1, rows: 1, ox: .25f, oy: .5f));
        var q = l.Quads[0];
        Assert.Equal((13f, 30f), (q.X, q.Y));          // 10+2.5 -> 13 (half rounds up), 20+10
        l.Clear();
    }

    [Fact]
    public void ASubPixelQuadKeepsOnePixel()
    {
        using var px = Px();
        // 0.2 px wide at x 10.1 (both edges round to 10): kept as one pixel so a thin image does not vanish
        var thin = new KittyRenderPlacement
        {
            Pixels = px, ImageSerial = px.Serial, Cols = 1, Rows = 1, U1 = 1, V1 = 1, OffXCells = 0.01f,
            NativeSize = true, NativeW = 2, NativeH = 4096, BasisCellW = 100, BasisCellH = 4096,
        };
        var l = Build(Scene(), thin);
        var q = Assert.Single(l.Quads.AsSpan(0, l.QuadCount).ToArray());
        Assert.Equal((0f, 1f), (q.X, q.W));
        l.Clear();
    }

    // Adjacent quads share an edge computed from the same absolute coordinates, so after snapping they must touch
    // exactly: no background row between them (gap) and no doubly covered row (overlap) at any cell size.
    [Theory]
    [InlineData(18.4f, 0f, false)]
    [InlineData(18.4f, 3.3f, false)]
    [InlineData(18.75f, 0f, false)]
    [InlineData(18.75f, 4.5f, false)]
    [InlineData(22.5f, 0f, false)]
    [InlineData(22.5f, 7f, false)]
    [InlineData(18.4f, 3.3f, true)]
    [InlineData(18.75f, 0f, true)]
    [InlineData(22.5f, 7f, true)]
    public void VerticallyAdjacentQuadsTouchAtFractionalCellHeights(float cellHeight, float padTop, bool native)
    {
        using var px = Px();
        const int rows = 60;
        var ps = new KittyRenderPlacement[rows];
        for (int r = 0; r < rows; r++)
            ps[r] = native ? Native(px, 0, r, pl: (uint)r) : P(px, row: r, pl: (uint)r);
        var l = Build(Scene(padT: padTop, ch: cellHeight, cr: 10_000, cb: 100_000), ps);

        Assert.Equal(rows, l.QuadCount);
        Assert.Equal((float)Math.Floor(padTop + 0.5), l.Quads[0].Y);
        for (int r = 1; r < rows; r++)
            Assert.Equal(l.Quads[r - 1].Y + l.Quads[r - 1].H, l.Quads[r].Y);
        // total height = the snapped bottom of the last row
        var last = l.Quads[rows - 1];
        Assert.Equal((float)Math.Floor(padTop + (double)rows * cellHeight + 0.5), last.Y + last.H);
        l.Clear();
    }

    [Theory]
    [InlineData(11.25f, 0f, false)]
    [InlineData(11.25f, 6.6f, false)]
    [InlineData(9.2f, 0f, false)]
    [InlineData(9.2f, 2.5f, true)]
    [InlineData(12.5f, 8f, true)]
    public void HorizontallyAdjacentQuadsTouchAtFractionalCellWidths(float cellWidth, float padLeft, bool native)
    {
        using var px = Px();
        const int cols = 80;
        var ps = new KittyRenderPlacement[cols];
        for (int c = 0; c < cols; c++)
            ps[c] = native ? Native(px, c, 0, pl: (uint)c) : P(px, col: c, pl: (uint)c);
        var l = Build(Scene(padL: padLeft, cw: cellWidth, cr: 100_000, cb: 10_000), ps);

        Assert.Equal(cols, l.QuadCount);
        for (int c = 1; c < cols; c++)
            Assert.Equal(l.Quads[c - 1].X + l.Quads[c - 1].W, l.Quads[c].X);
        l.Clear();
    }

    [Fact]
    public void SnappedQuadStretchesItsTextureOverTheSnappedRectangle()
    {
        using var px = Px();
        // top edge 15.25*2 = 30.5 -> 31, so a clip at y 40 keeps 9 of the snapped 30 rows: v1 = 9/30
        var l = Build(Scene(cw: 7.5f, ch: 15.25f, cb: 40), P(px, row: 2, rows: 2));
        var q = l.Quads[0];
        Assert.Equal((31f, 9f), (q.Y, q.H));
        Assert.Equal(9f / 30f, q.V1, 6);
        l.Clear();
    }

    private static KittyRenderPlacement Native(ImagePixels px, int col, int row, uint pl)
        => new()
        {
            Pixels = px, ImageSerial = px.Serial, Col = col, ViewRow = row, Cols = 1, Rows = 1, U1 = 1, V1 = 1,
            NativeSize = true, NativeW = 4096, NativeH = 4096, BasisCellW = 4096, BasisCellH = 4096,
            ImageId = 1, PlacementId = pl,
        };

    [Fact]
    public void LayerBoundaries()
    {
        var pxs = new[] { Px(), Px(), Px(), Px() };
        try
        {
            var l = Build(Scene(),
                P(pxs[0], col: 0, z: -1_073_741_825),
                P(pxs[1], col: 1, z: -1_073_741_824),
                P(pxs[2], col: 2, z: -1),
                P(pxs[3], col: 3, z: 0));
            Assert.Equal(new[] { 0, 1, 3, 4 }, l.LayerDrawStart);
            Assert.Equal(ImageLayer.BelowBackground, l.Draws[0].Layer);
            Assert.Equal(ImageLayer.BelowText, l.Draws[1].Layer);
            Assert.Equal(ImageLayer.BelowText, l.Draws[2].Layer);
            Assert.Equal(ImageLayer.AboveText, l.Draws[3].Layer);
            // z=-2^30 < z=-1 within BelowText
            Assert.Equal(10f, l.Quads[l.Draws[1].FirstQuad].X);
            Assert.Equal(20f, l.Quads[l.Draws[2].FirstQuad].X);
            l.Clear();
        }
        finally { foreach (var p in pxs) p.Dispose(); }
    }

    [Fact]
    public void EqualZ_OrdersByImageIdThenPlacementId_StableOtherwise()
    {
        using var a = Px(); using var b = Px(); using var c = Px(); using var d = Px();
        var l = Build(Scene(),
            P(a, col: 0, img: 5, pl: 2),
            P(b, col: 1, img: 3, pl: 9),
            P(c, col: 2, img: 5, pl: 1),
            P(d, col: 3, img: 5, pl: 1));      // identical key as c: input order kept
        Assert.Equal(new[] { 10f, 20f, 30f, 0f }, new[] { l.Quads[0].X, l.Quads[1].X, l.Quads[2].X, l.Quads[3].X });
        l.Clear();
    }

    [Fact]
    public void LowerZ_DrawsFirst_EvenWithHigherImageId()
    {
        using var a = Px(); using var b = Px();
        var l = Build(Scene(), P(a, col: 0, z: 2, img: 1), P(b, col: 1, z: 1, img: 9));
        Assert.Equal(10f, l.Quads[0].X);
        Assert.Equal(0f, l.Quads[1].X);
        l.Clear();
    }

    [Fact]
    public void SameImage_Merges_WhenAdjacentAfterSort_NotWhenInterleaved()
    {
        using var s1 = Px(); using var s2 = Px();
        var l = Build(Scene(),
            P(s1, col: 0, img: 1, pl: 1),
            P(s2, col: 1, img: 2, pl: 1),
            P(s1, col: 2, img: 1, pl: 2));
        Assert.Equal(2, l.DrawCount);
        Assert.Equal((s1.Serial, 0, 2), (l.Draws[0].Pixels.Serial, l.Draws[0].FirstQuad, l.Draws[0].QuadCount));
        Assert.Equal((s2.Serial, 2, 1), (l.Draws[1].Pixels.Serial, l.Draws[1].FirstQuad, l.Draws[1].QuadCount));
        l.Clear();

        var m = Build(Scene(),
            P(s1, col: 0, z: 0, img: 1),
            P(s2, col: 1, z: 1, img: 2),
            P(s1, col: 2, z: 2, img: 1));
        Assert.Equal(3, m.DrawCount);
        m.Clear();
    }

    [Fact]
    public void SameImage_DoesNotMergeAcrossLayers()
    {
        using var s = Px();
        var l = Build(Scene(), P(s, col: 0, z: -1), P(s, col: 1, z: 1));
        Assert.Equal(2, l.DrawCount);
        Assert.Equal(new[] { 0, 0, 1, 2 }, l.LayerDrawStart);
        l.Clear();
    }

    [Fact]
    public void MultipleAppends_KeepDrawsGroupedByLayer()
    {
        using var above = Px(); using var below = Px(); using var bg = Px();
        var l = new ImageDrawList();
        ImageQuadBuilder.Append(l, new[] { P(above, col: 0, z: 5) }, Scene());
        ImageQuadBuilder.Append(l, new[] { P(below, col: 1, z: -5), P(bg, col: 2, z: int.MinValue) }, Scene());
        Assert.Equal(new[] { 0, 1, 2, 3 }, l.LayerDrawStart);
        Assert.Equal(bg.Serial, l.Draws[0].Pixels.Serial);
        Assert.Equal(below.Serial, l.Draws[1].Pixels.Serial);
        Assert.Equal(above.Serial, l.Draws[2].Pixels.Serial);
        Assert.Equal(0f, l.Quads[l.Draws[2].FirstQuad].X);
        Assert.Equal(20f, l.Quads[l.Draws[0].FirstQuad].X);
        l.Clear();
    }

    [Fact]
    public void Invalid_Quads_AreDropped()
    {
        using var px = Px();
        var ps = new[]
        {
            P(px, cols: 0, rows: 1),
            P(px, cols: 1, rows: 0),
            P(px, ox: float.NaN),
            P(px, oy: float.PositiveInfinity),
            P(px, u0: float.NaN),
            P(px, col: 100, cols: 2),                 // fully right of clip
            P(px, row: -5, rows: 2),                  // fully above clip
            P(px, col: int.MaxValue, cols: 1),
        };
        var l = new ImageDrawList();
        ImageQuadBuilder.Append(l, ps, Scene());
        Assert.Equal((0, 0), (l.DrawCount, l.QuadCount));
    }

    [Fact]
    public void BadScene_AppendsNothing()
    {
        using var px = Px();
        var ps = new[] { P(px) };
        foreach (var g in new[] { Scene(cw: 0), Scene(ch: float.NaN), Scene(cw: -1), Scene(padL: float.NaN) })
        {
            var l = new ImageDrawList();
            ImageQuadBuilder.Append(l, ps, g);
            Assert.Equal(0, l.DrawCount);
        }
        var empty = new ImageDrawList();
        ImageQuadBuilder.Append(empty, ReadOnlySpan<KittyRenderPlacement>.Empty, Scene());
        Assert.Equal(0, empty.DrawCount);
    }

    [Fact]
    public void ZeroAreaClipRect_DropsEverything()
    {
        using var px = Px();
        Assert.Equal(0, Build(Scene(cl: 50, cr: 50), P(px, col: 4, cols: 3)).DrawCount);
    }

    [Fact]
    public void Leases_ReturnToBaseline()
    {
        long baseCount = GraphicsMemoryMetrics.GetSnapshot().LiveImageCount;
        var px = Px();
        var l = Build(Scene(), P(px, col: 0), P(px, col: 1, pl: 1));
        Assert.Equal(1, l.DrawCount);
        px.Dispose();                                   // owner gone, list keeps it alive
        Assert.Equal(baseCount + 1, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        l.ReleaseLeases();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        l.ReleaseLeases();                               // idempotent
        l.Clear();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        Assert.Equal(new[] { 0, 0, 0, 0 }, l.LayerDrawStart);
        Assert.Equal((0, 0), (l.DrawCount, l.QuadCount));
    }

    [Fact]
    public void Leases_OneRetainPerRun_NotPerQuad()
    {
        long baseCount = GraphicsMemoryMetrics.GetSnapshot().LiveImageCount;
        var a = Px(); var b = Px();
        var l = Build(Scene(), P(a, col: 0, img: 1, pl: 1), P(a, col: 1, img: 1, pl: 2), P(b, col: 2, img: 2));
        a.Dispose(); b.Dispose();
        Assert.Equal(baseCount + 2, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        l.Clear();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
    }

    [Fact]
    public void Dropped_Placements_DoNotLeakLeases()
    {
        long baseCount = GraphicsMemoryMetrics.GetSnapshot().LiveImageCount;
        var px = Px();
        var l = Build(Scene(), P(px, col: 500));        // clipped away
        px.Dispose();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        Assert.Equal(0, l.DrawCount);
    }

    [Fact]
    public void Append_AllocatesNothing_AfterWarmup()
    {
        using var a = Px(); using var b = Px();
        var ps = new[]
        {
            P(a, col: 0, img: 2, z: 1), P(b, col: 1, img: 1, z: -3), P(a, col: 2, img: 3, z: 1), P(b, col: 3, row: -1, rows: 3, z: -2_000_000_000),
        };
        var l = new ImageDrawList();
        var g = Scene();
        AllocationAssert.NoAllocations(() =>
        {
            ImageQuadBuilder.Append(l, ps, g);
            l.Clear();
        });
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class KittyRenderSnapshotTests
{
    [Fact]
    public void Add_Retains_Dispose_Releases_Idempotently()
    {
        long baseCount = GraphicsMemoryMetrics.GetSnapshot().LiveImageCount;
        var px = ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgb8);
        var snap = new KittyRenderSnapshot();
        snap.Add(new KittyRenderPlacement { Pixels = px, ImageSerial = px.Serial, ImageId = 7 });
        snap.Add(new KittyRenderPlacement { Pixels = px, ImageSerial = px.Serial, ImageId = 8 });
        px.Dispose();
        Assert.Equal(2, snap.Placements.Length);
        Assert.Equal(8u, snap.Placements[1].ImageId);
        Assert.Equal(baseCount + 1, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        snap.Dispose();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        snap.Dispose();
        Assert.Equal(baseCount, GraphicsMemoryMetrics.GetSnapshot().LiveImageCount);
        Assert.True(snap.Placements.IsEmpty);
    }

    [Fact]
    public void Add_GrowsBeyondInitialCapacity_PreservingOrder()
    {
        var px = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);
        var snap = new KittyRenderSnapshot();
        for (uint i = 0; i < 100; i++)
            snap.Add(new KittyRenderPlacement { Pixels = px, ImageId = i });
        px.Dispose();
        Assert.Equal(100, snap.Placements.Length);
        for (int i = 0; i < 100; i++) Assert.Equal((uint)i, snap.Placements[i].ImageId);
        snap.Dispose();
    }

    [Fact]
    public void Reset_AllowsReuse_AndClearsDeadline()
    {
        var px = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);
        var snap = new KittyRenderSnapshot { NextAnimationDeadlineTicks = 99 };
        snap.Add(new KittyRenderPlacement { Pixels = px });
        snap.Reset();
        Assert.True(snap.Placements.IsEmpty);
        Assert.Equal(0, snap.NextAnimationDeadlineTicks);
        snap.Add(new KittyRenderPlacement { Pixels = px });
        Assert.Single(snap.Placements.ToArray());
        snap.Dispose();
        px.Dispose();
    }

    [Fact]
    public void Add_WithoutPixels_Throws()
    {
        var snap = new KittyRenderSnapshot();
        Assert.Throws<ArgumentException>(() => snap.Add(new KittyRenderPlacement()));
    }
}
