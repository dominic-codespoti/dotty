using System;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>W4-C9: constraint table, pure solver, span decision and atlas key/placement behaviour.</summary>
public sealed class GlyphConstraintTests
{
    private const float CellW = 10f, CellH = 20f, Baseline = 16f;

    private static GlyphConstraint C(GlyphSizeMode size, GlyphAlign x, GlyphAlign y, float pad = 0f, byte span = 2) =>
        new(size, x, y, pad, span);

    // ink (left, top, w, h) relative to pen/baseline; expected (sx, sy, x, y) relative to the cell top-left.
    [Theory]
    // Fit: shrink only, never enlarge.
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -10f, 4f, 10f, 1f, 1f, 3f, 5f)]
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -20f, 20f, 40f, 0.5f, 0.5f, 0f, 0f)]
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 2, 0f, -20f, 20f, 40f, 0.5f, 0.5f, 5f, 0f)]
    // Cover: largest uniform scale that fits, up or down.
    [InlineData(GlyphSizeMode.Cover, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -5f, 2f, 5f, 4f, 4f, 1f, 0f)]
    [InlineData(GlyphSizeMode.Cover, GlyphAlign.Center, GlyphAlign.Center, 0f, 2, 0f, -5f, 5f, 5f, 4f, 4f, 0f, 0f)]
    [InlineData(GlyphSizeMode.Cover, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -40f, 40f, 40f, 0.25f, 0.25f, 0f, 5f)]
    // Stretch: per-axis fill.
    [InlineData(GlyphSizeMode.Stretch, GlyphAlign.Start, GlyphAlign.Start, 0f, 1, 0f, -5f, 5f, 5f, 2f, 4f, 0f, 0f)]
    [InlineData(GlyphSizeMode.Stretch, GlyphAlign.Start, GlyphAlign.Start, 0f, 2, 3f, -5f, 5f, 5f, 4f, 4f, 0f, 0f)]
    // Alignment on both axes.
    [InlineData(GlyphSizeMode.None, GlyphAlign.Start, GlyphAlign.Start, 0f, 1, 3f, -5f, 4f, 4f, 1f, 1f, 0f, 0f)]
    [InlineData(GlyphSizeMode.None, GlyphAlign.End, GlyphAlign.End, 0f, 1, 3f, -5f, 4f, 4f, 1f, 1f, 6f, 16f)]
    [InlineData(GlyphSizeMode.None, GlyphAlign.Center, GlyphAlign.End, 0f, 2, 3f, -5f, 4f, 4f, 1f, 1f, 8f, 16f)]
    // Padding insets the box (10% of 10x20 = 1x2 per side).
    [InlineData(GlyphSizeMode.Cover, GlyphAlign.Start, GlyphAlign.Start, 0.1f, 1, 0f, -5f, 8f, 8f, 1f, 1f, 1f, 2f)]
    [InlineData(GlyphSizeMode.Stretch, GlyphAlign.Start, GlyphAlign.Start, 0.1f, 1, 0f, -5f, 4f, 8f, 2f, 2f, 1f, 2f)]
    // Natural position kept when it fits, clamped into the box when it overflows (text fallback rule).
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.None, GlyphAlign.None, 0f, 1, 1f, -12f, 6f, 14f, 1f, 1f, 1f, 4f)]
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.None, GlyphAlign.None, 0f, 1, 7f, -18f, 6f, 14f, 1f, 1f, 4f, 0f)]
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.None, GlyphAlign.None, 0f, 1, -3f, 2f, 6f, 8f, 1f, 1f, 0f, 12f)]
    // Huge ink shrinks to the box; tiny ink stays tiny under Fit.
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -1000f, 1000f, 1000f, 0.01f, 0.01f, 0f, 5f)]
    [InlineData(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 1, 0f, -0.5f, 0.5f, 0.5f, 1f, 1f, 4.75f, 9.75f)]
    public void SolverTable(GlyphSizeMode size, GlyphAlign ax, GlyphAlign ay, float pad, int span,
        float left, float top, float w, float h, float sx, float sy, float x, float y)
    {
        var p = ConstraintSolver.Solve(new InkBounds(left, top, w, h), CellW, CellH, Baseline, span, C(size, ax, ay, pad));
        Assert.Equal(sx, p.ScaleX, 3);
        Assert.Equal(sy, p.ScaleY, 3);
        Assert.Equal(x, p.X, 3);
        Assert.Equal(y, p.Y, 3);
    }

    [Theory]
    [InlineData(0f, 10f, 10f, 20f)]
    [InlineData(5f, 0f, 10f, 20f)]
    [InlineData(5f, 10f, 0f, 20f)]
    [InlineData(5f, 10f, 10f, 0f)]
    [InlineData(float.NaN, 10f, 10f, 20f)]
    [InlineData(5f, 10f, float.PositiveInfinity, 20f)]
    public void ZeroOrInvalidSizesReturnIdentity(float inkW, float inkH, float cellW, float cellH)
    {
        var p = ConstraintSolver.Solve(new InkBounds(1f, -8f, inkW, inkH), cellW, cellH, Baseline, 1, GlyphConstraint.Icon);
        Assert.Equal(new GlyphPlacement(1f, 1f, 1f, Baseline - 8f), p);
    }

    [Fact]
    public void NoConstraintIsIdentity()
    {
        var p = ConstraintSolver.Solve(new InkBounds(-2f, -30f, 50f, 50f), CellW, CellH, Baseline, 2, GlyphConstraint.None);
        Assert.Equal(new GlyphPlacement(1f, 1f, -2f, Baseline - 30f), p);
    }

    [Fact]
    public void SpanIsLimitedByMaxSpanAndAtLeastOne()
    {
        var ink = new InkBounds(0f, -10f, 20f, 10f);
        var one = C(GlyphSizeMode.Fit, GlyphAlign.Start, GlyphAlign.Start, 0f, span: 1);
        Assert.Equal(0.5f, ConstraintSolver.Solve(ink, CellW, CellH, Baseline, 2, one).ScaleX, 3);
        Assert.Equal(0.5f, ConstraintSolver.Solve(ink, CellW, CellH, Baseline, 0, GlyphConstraint.Icon).ScaleX, 3);
        Assert.Equal(1f, ConstraintSolver.Solve(ink, CellW, CellH, Baseline, 5, GlyphConstraint.Icon).ScaleX, 3);
    }

    [Fact]
    public void PaddingIsClamped()
    {
        var p = ConstraintSolver.Solve(new InkBounds(0f, -1f, 1f, 1f), CellW, CellH, Baseline, 1,
            C(GlyphSizeMode.Stretch, GlyphAlign.Start, GlyphAlign.Start, pad: 2f, span: 1));
        Assert.Equal(CellW * (1f - 2f * ConstraintSolver.MaxPadding), p.ScaleX, 3);
        Assert.Equal(CellW * ConstraintSolver.MaxPadding, p.X, 3);
    }

    [Theory]
    [InlineData(0xE000, "icon")]   // Pomicons
    [InlineData(0xE0A0, "icon")]   // Powerline branch
    [InlineData(0xE0B0, "fill")]   // Powerline separator
    [InlineData(0xE0C8, "fill")]
    [InlineData(0xE0C9, "icon")]   // gap in Powerline Extra: generic PUA
    [InlineData(0xE0CA, "fill")]
    [InlineData(0xE0D7, "fill")]
    [InlineData(0xE700, "icon")]   // Devicons
    [InlineData(0xF015, "icon")]   // Font Awesome home
    [InlineData(0xF8FF, "icon")]   // last BMP PUA
    [InlineData(0xF0001, "icon")]  // Material Design
    [InlineData(0x10FFFD, "icon")] // plane 16 PUA
    [InlineData(0x41, "none")]
    [InlineData(0x2500, "none")]   // box drawing: sprites
    [InlineData(0x4E16, "none")]
    [InlineData(0xDFFF, "none")]
    [InlineData(0xF900, "none")]
    public void TableLookup(int codepoint, string expected)
    {
        var c = GlyphConstraints.ForCodepoint(codepoint);
        var want = expected switch
        {
            "icon" => GlyphConstraint.Icon,
            "fill" => GlyphConstraint.CellFill,
            _ => GlyphConstraint.None,
        };
        Assert.Equal(want, c);
    }

    [Theory]
    [InlineData("\uE700", true)]
    [InlineData("\uE700\uFE0F", true)]
    [InlineData("\U000F0001", true)]
    [InlineData("\uE700\uE700", false)]
    [InlineData("A", false)]
    [InlineData("", false)]
    public void GraphemeLookupNeedsOneScalar(string grapheme, bool icon) =>
        Assert.Equal(icon, GlyphConstraints.ForGrapheme(grapheme) == GlyphConstraint.Icon);

    // codepoint, cell width, single scalar, next blank -> span
    [Theory]
    [InlineData(0xE700, 1, true, true, 2)]
    [InlineData(0xE700, 1, true, false, 1)]
    [InlineData(0xE700, 1, false, true, 1)]
    [InlineData(0xE0B0, 1, true, true, 1)]  // separators never spread
    [InlineData(0x41, 1, true, true, 1)]
    [InlineData(0x4E16, 2, true, false, 2)] // wide cell is always two
    [InlineData(0xE700, 2, true, false, 2)]
    [InlineData(0xF0001, 1, true, true, 2)]
    public void SpanDecisionTable(int codepoint, int width, bool single, bool nextBlank, int expected) =>
        Assert.Equal(expected, GlyphCellSpan.Decide(codepoint, width, single, nextBlank));

    [Fact]
    public void BlankCellDecision()
    {
        var row = new CellHot[5];
        row[0].Rune = 0xE700;
        row[1].Rune = 0;
        row[2].Rune = ' ';
        row[3].Rune = 'x';
        row[4].IsContinuation = true;
        Assert.True(GlyphCellSpan.IsBlank(row, 1, 5));
        Assert.True(GlyphCellSpan.IsBlank(row, 2, 5));
        Assert.False(GlyphCellSpan.IsBlank(row, 3, 5));
        Assert.False(GlyphCellSpan.IsBlank(row, 4, 5));
        Assert.False(GlyphCellSpan.IsBlank(row, 5, 5));
        Assert.False(GlyphCellSpan.IsBlank(row, 2, 2)); // beyond the drawn row length
    }

    [Fact]
    public void KeysDifferBySpan()
    {
        var a = new GlyphKey("\uE700", SKTypeface.Default, 16f, GlyphStyle.Regular, 1);
        var b = new GlyphKey("\uE700", SKTypeface.Default, 16f, GlyphStyle.Regular, 2);
        Assert.NotEqual(a, b);
        Assert.Equal(1, new GlyphKey("A", SKTypeface.Default, 16f, GlyphStyle.Regular).CellSpan);
        Assert.Equal(2, new GlyphKey("\u4E16", SKTypeface.Default, 16f, GlyphStyle.Regular).CellSpan);
        Assert.Equal(2, new GlyphKey("A", SKTypeface.Default, 16f, GlyphStyle.Regular, 7).CellSpan);
        Assert.Equal(a, new GlyphKey("\uE700", SKTypeface.Default, 16f, GlyphStyle.Regular, 1));
    }

    [Fact]
    public void AtlasStoresSpanVariantsSeparatelyAndCentresIcons()
    {
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 256);
        Assert.True(atlas.SetCellGeometry(10, 20, 16, 0));
        Assert.False(atlas.SetCellGeometry(10, 20, 16, 0));
        Assert.Equal(10f, atlas.CellWidth);
        Assert.Equal(20f, atlas.CellHeight);

        // The default face draws .notdef for an unmapped PUA code point; it is still an icon and gets constrained.
        var one = new GlyphKey("\uE700", SKTypeface.Default, 16f, GlyphStyle.Regular, 1);
        var two = new GlyphKey("\uE700", SKTypeface.Default, 16f, GlyphStyle.Regular, 2);
        Assert.True(atlas.EnsureGlyph(one, out var g1));
        Assert.True(atlas.EnsureGlyph(two, out var g2));
        Assert.False(g1.X == g2.X && g1.Y == g2.Y);
        Assert.True(g1.Width <= 10 && g1.Height <= 20);
        Assert.True(g2.Width <= 20 && g2.Height <= 20);
        // Centred: equal margins (±1 px rounding) inside one and two cells.
        Assert.InRange(g1.LeftBearing * 2 + g1.Width, 9f, 11f);
        Assert.InRange(g2.LeftBearing * 2 + g2.Width, 19f, 21f);
        float top1 = g1.BaselineOffset + g1.TopBearing;
        Assert.InRange(top1 * 2 + g1.Height, 19f, 21f);
    }

    [Fact]
    public void CellGeometryChangeRebuilds()
    {
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 256);
        atlas.SetCellGeometry(10, 20, 16, 0);
        int epoch = atlas.LayoutEpoch;
        Assert.True(atlas.SetCellGeometry(12, 20, 16, 0));
        Assert.NotEqual(epoch, atlas.LayoutEpoch);
        Assert.False(atlas.SetCellGeometry(0, 20, 16, 0));
    }
}
