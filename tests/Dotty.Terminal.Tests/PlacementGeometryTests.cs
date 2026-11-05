using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PlacementGeometryTests
{
    // Image 100x50, cell 10x20 unless stated.
    private static ResolvedGeometry R(
        uint iw = 100, uint ih = 50, uint sx = 0, uint sy = 0, uint sw = 0, uint sh = 0,
        uint xo = 0, uint yo = 0, uint c = 0, uint r = 0, int cw = 10, int ch = 20)
        => PlacementGeometry.Resolve(iw, ih, sx, sy, sw, sh, xo, yo, c, r, cw, ch);

    [Fact]
    public void Neither_IsNativeSize_CellsRoundUp()
    {
        var g = R();
        Assert.True(g.NativeSize);
        Assert.Equal((10, 3), (g.EffCols, g.EffRows));
        Assert.Equal((100f, 50f), (g.DestW, g.DestH));
        Assert.Equal((0, 0, 100, 50), (g.SrcX, g.SrcY, g.SrcW, g.SrcH));
    }

    [Fact]
    public void Neither_OffsetsPushCellCountUp()
    {
        var g = R(xo: 3, yo: 15);
        Assert.Equal((3, 15), (g.XOff, g.YOff));
        Assert.Equal((11, 4), (g.EffCols, g.EffRows)); // ceil(103/10), ceil(65/20)
    }

    [Fact]
    public void Offsets_AreClampedBelowCellSize()
    {
        var g = R(xo: 999, yo: 999);
        Assert.Equal((9, 19), (g.XOff, g.YOff));
        Assert.Equal(9, R(xo: 10).XOff);
        Assert.Equal(8, R(xo: 8).XOff);
    }

    [Fact]
    public void Both_StretchesToBox()
    {
        var g = R(c: 4, r: 2);
        Assert.False(g.NativeSize);
        Assert.Equal((4, 2), (g.EffCols, g.EffRows));
        Assert.Equal((40f, 40f), (g.DestW, g.DestH));
    }

    [Fact]
    public void ColsOnly_DerivesRowsFromAspect()
    {
        var g = R(c: 5);
        Assert.Equal(5, g.EffCols);
        Assert.Equal(2, g.EffRows);           // 50px wide -> 25px tall -> ceil(25/20)
        Assert.Equal((50f, 25f), (g.DestW, g.DestH));
    }

    [Fact]
    public void RowsOnly_DerivesColsFromAspect()
    {
        var g = R(r: 3);
        Assert.Equal(12, g.EffCols);          // 60px tall -> 120px wide
        Assert.Equal(3, g.EffRows);
        Assert.Equal((120f, 60f), (g.DestW, g.DestH));
    }

    [Fact]
    public void ColsOnly_UsesCroppedSourceAspect()
    {
        var g = R(sx: 50, sw: 50, c: 5);      // 50x50 source => square
        Assert.Equal((50, 0, 50, 50), (g.SrcX, g.SrcY, g.SrcW, g.SrcH));
        Assert.Equal(3, g.EffRows);           // 50px -> ceil(50/20)
        Assert.Equal((50f, 50f), (g.DestW, g.DestH));
    }

    [Fact]
    public void SourceRect_IsClampedToImage()
    {
        var g = R(sx: 90, sw: 50);
        Assert.Equal((90, 10), (g.SrcX, g.SrcW));
        g = R(sx: 40, sy: 10);                // zero width/height = remainder
        Assert.Equal((40, 10, 60, 40), (g.SrcX, g.SrcY, g.SrcW, g.SrcH));
        g = R(sw: 1000, sh: 1000);
        Assert.Equal((100, 50), (g.SrcW, g.SrcH));
    }

    [Fact]
    public void SourceStartingBeyondImage_IsEmpty()
    {
        Assert.True(R(sx: 100).IsEmpty);
        Assert.True(R(sx: 5000).IsEmpty);
        Assert.True(R(sy: 50, c: 3, r: 3).IsEmpty);
    }

    [Fact]
    public void EmptyImage_IsEmpty()
    {
        Assert.True(R(iw: 0).IsEmpty);
        Assert.True(R(ih: 0, c: 2, r: 2).IsEmpty);
    }

    [Fact]
    public void ZeroCellSize_IsTreatedAsOne_AndDoesNotThrow()
    {
        var g = R(cw: 0, ch: 0, xo: 5, yo: 5);
        Assert.Equal((0, 0), (g.XOff, g.YOff));
        Assert.Equal((100, 50), (g.EffCols, g.EffRows));
        g = R(cw: -3, ch: 0, c: 2, r: 2);
        Assert.Equal((2, 2), (g.EffCols, g.EffRows));
        Assert.Equal((1, 1), (g.BasisCellW, g.BasisCellH));
    }

    [Fact]
    public void HugeValues_AreClamped()
    {
        var g = R(c: uint.MaxValue, r: uint.MaxValue);
        Assert.Equal((PlacementGeometry.MaxCells, PlacementGeometry.MaxCells), (g.EffCols, g.EffRows));
        g = R(iw: 1, ih: 1, c: uint.MaxValue, cw: 1000, ch: 1000);
        Assert.False(g.IsEmpty);
        Assert.Equal((PlacementGeometry.MaxCells, PlacementGeometry.MaxCells), (g.EffCols, g.EffRows));
        g = R(iw: uint.MaxValue, ih: uint.MaxValue);   // does not fit an int source rect
        Assert.True(g.IsEmpty);
        g = R(iw: 1, ih: int.MaxValue, c: 1, cw: int.MaxValue, ch: 1);
        Assert.InRange(g.EffRows, 1, PlacementGeometry.MaxCells);
    }

    [Fact]
    public void OneByOneImage_Native()
    {
        var g = R(iw: 1, ih: 1);
        Assert.Equal((1, 1), (g.EffCols, g.EffRows));
        Assert.Equal((1f, 1f), (g.DestW, g.DestH));
    }

    [Fact]
    public void DestRect_NativeScalesWithCellRatio_BoxFollowsCells()
    {
        var native = R();
        var d = PlacementGeometry.DestRect(native, 15f, 30f);
        Assert.Equal((150f, 75f), (d.W, d.H));

        var box = R(c: 4, r: 2);
        d = PlacementGeometry.DestRect(box, 7.5f, 15.25f);
        Assert.Equal(30f, d.W);
        Assert.Equal(30.5f, d.H);

        var aspect = R(c: 5);                 // 50 x 25 at basis
        d = PlacementGeometry.DestRect(aspect, 20f, 20f);
        Assert.Equal((100f, 25f * 20f / 20f), (d.W, d.H));
    }

    [Fact]
    public void DestRect_RejectsBadCellSizes()
    {
        var g = R();
        Assert.Equal(default, PlacementGeometry.DestRect(g, 0f, 20f));
        Assert.Equal(default, PlacementGeometry.DestRect(g, float.NaN, 20f));
        Assert.Equal(default, PlacementGeometry.DestRect(g, 10f, float.PositiveInfinity));
        Assert.Equal(default, PlacementGeometry.DestRect(R(sx: 100), 10f, 20f));
    }

    [Fact]
    public void CropTop_RemovesProportionalSource_AndKeepsNativeNative()
    {
        var g = PlacementGeometry.CropTop(R(), 1);      // 3 rows, 50px tall
        Assert.Equal((0, 20, 100, 30), (g.SrcX, g.SrcY, g.SrcW, g.SrcH));
        Assert.Equal(2, g.EffRows);
        Assert.Equal(30f, g.DestH);
        Assert.True(g.NativeSize);
        Assert.Equal(0, g.YOff);
    }

    [Fact]
    public void CropBottom_TrimsOnlyBelowNewCellEdge()
    {
        var g = PlacementGeometry.CropBottom(R(), 1);   // box bottom at 40px, image 50px => 10px removed
        Assert.Equal((0, 0, 100, 40), (g.SrcX, g.SrcY, g.SrcW, g.SrcH));
        Assert.Equal(2, g.EffRows);
        Assert.Equal(40f, g.DestH);
        g = PlacementGeometry.CropBottom(R(), 2);
        Assert.Equal(20, g.SrcH);
        Assert.Equal(1, g.EffRows);
    }

    [Fact]
    public void CropLeftAndRight_TrimSourceAndRespectFractionalPixels()
    {
        var g = R(iw: 100, ih: 20, sx: 10, sw: 80, xo: 5, c: 4, r: 1);
        var left = PlacementGeometry.CropLeft(g, 1);
        Assert.Equal((20, 70), (left.SrcX, left.SrcW));
        Assert.Equal((0, 3), (left.XOff, left.EffCols));
        Assert.Equal(35f, left.DestW);

        var right = PlacementGeometry.CropRight(g, 1);
        Assert.Equal((10, 50), (right.SrcX, right.SrcW));
        Assert.Equal((5, 3), (right.XOff, right.EffCols));
        Assert.Equal(25f, right.DestW);
    }

    [Fact]
    public void CropHorizontal_IsIrreversibleAndSaturatesSafely()
    {
        var g = R(iw: 100, ih: 20, c: 4, r: 1);
        var twice = PlacementGeometry.CropLeft(PlacementGeometry.CropLeft(g, 1), 1);
        var once = PlacementGeometry.CropLeft(g, 2);
        Assert.Equal((once.SrcX, once.SrcW, once.EffCols, once.DestW),
            (twice.SrcX, twice.SrcW, twice.EffCols, twice.DestW));
        Assert.True(PlacementGeometry.CropRight(g, int.MaxValue).IsEmpty);
        Assert.Equal(g, PlacementGeometry.CropLeft(g, 0));
    }

    [Fact]
    public void CropBottom_OnStretchedBox_IsProportional()
    {
        var g = PlacementGeometry.CropBottom(R(c: 4, r: 2), 1);   // 40px box showing half of 50px source
        Assert.Equal(25, g.SrcH);
        Assert.Equal(20f, g.DestH);
        Assert.Equal(1, g.EffRows);
    }

    [Fact]
    public void Crop_ZeroOrNegative_IsIdentity()
    {
        var g = R();
        Assert.Equal(g, PlacementGeometry.CropTop(g, 0));
        Assert.Equal(g, PlacementGeometry.CropBottom(g, -4));
    }

    [Fact]
    public void Crop_AllRowsOrMore_IsEmpty()
    {
        Assert.True(PlacementGeometry.CropTop(R(), 3).IsEmpty);
        Assert.True(PlacementGeometry.CropTop(R(), 99).IsEmpty);
        Assert.True(PlacementGeometry.CropBottom(R(), 3).IsEmpty);
        Assert.True(PlacementGeometry.CropBottom(R(), int.MaxValue).IsEmpty);
    }

    [Fact]
    public void CropTop_Twice_EqualsCropOnceByTotal()
    {
        var g = R(iw: 100, ih: 60);                      // 3 rows, 60px
        var twice = PlacementGeometry.CropTop(PlacementGeometry.CropTop(g, 1), 1);
        var once = PlacementGeometry.CropTop(g, 2);
        Assert.Equal((40, 20), (once.SrcY, once.SrcH));
        Assert.Equal((once.SrcY, once.SrcH, once.EffRows, once.DestH), (twice.SrcY, twice.SrcH, twice.EffRows, twice.DestH));
    }

    [Fact]
    public void CropTop_ConsumesYOffset()
    {
        // box 4x2 (40x40 px), offset 15px down; dropping the first row removes 20-15=5px of the image
        var g = PlacementGeometry.CropTop(R(c: 4, r: 2, yo: 15), 1);
        Assert.Equal(0, g.YOff);
        Assert.Equal(35f, g.DestH);
        Assert.Equal(6, g.SrcY);                         // round(50 * 5/40) = 6
        Assert.Equal(44, g.SrcH);
    }

    [Fact]
    public void Crop_NeverLeavesZeroHeightSourceWhileVisible()
    {
        var g = R(iw: 10, ih: 1, c: 1, r: 100);          // 1px source stretched over 100 rows
        var c = PlacementGeometry.CropTop(g, 99);
        Assert.False(c.IsEmpty);
        Assert.Equal(1, c.SrcH);
    }

    [Fact]
    public void FitIntoBox_LetterboxesWide_Pillarboxes_Tall()
    {
        var f = PlacementGeometry.FitIntoBox(100, 50, 4, 2, 10f, 20f);   // box 40x40
        Assert.Equal(new FitRect(0f, 10f, 40f, 20f), f);
        f = PlacementGeometry.FitIntoBox(50, 100, 4, 2, 10f, 20f);
        Assert.Equal(new FitRect(10f, 0f, 20f, 40f), f);
    }

    [Fact]
    public void FitIntoBox_ExactAspect_FillsBox_AndUpscales()
    {
        Assert.Equal(new FitRect(0f, 0f, 20f, 20f), PlacementGeometry.FitIntoBox(1, 1, 2, 1, 10f, 20f));
        Assert.Equal(new FitRect(0f, 0f, 40f, 40f), PlacementGeometry.FitIntoBox(10, 10, 4, 2, 10f, 20f));
    }

    [Fact]
    public void FitIntoBox_Degenerate_IsEmpty()
    {
        Assert.Equal(default, PlacementGeometry.FitIntoBox(0, 5, 1, 1, 1f, 1f));
        Assert.Equal(default, PlacementGeometry.FitIntoBox(5, 5, 0, 1, 1f, 1f));
        Assert.Equal(default, PlacementGeometry.FitIntoBox(5, 5, 1, 1, 0f, 1f));
        Assert.Equal(default, PlacementGeometry.FitIntoBox(5, 5, 1, 1, float.NaN, 1f));
    }
}
