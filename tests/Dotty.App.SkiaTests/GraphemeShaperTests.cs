using System;
using System.IO;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class GraphemeShaperTests
{
    private const string DejaVuSans = "DejaVuSans.ttf";
    private const string DejaVuSansBold = "DejaVuSans-Bold.ttf";
    private const string DejaVuSansOblique = "DejaVuSans-Oblique.ttf";
    private static readonly string[] DejaVuDirectories =
    {
        "/usr/share/fonts/dejavu",
        "/usr/share/fonts/truetype/dejavu",
        "/usr/share/fonts/TTF"
    };

    private static SKTypeface Load(string fileName)
    {
        foreach (string directory in DejaVuDirectories)
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path)) return SKTypeface.FromFile(path)!;
        }

        Assert.Skip($"requires {fileName} in a known DejaVu font directory");
        throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("e", false)]
    [InlineData("é", false)]
    [InlineData("e\u0301", true)]
    [InlineData("🇺🇸", true)]
    [InlineData("👍🏽", true)]
    [InlineData("", false)]
    public void ShapingThresholdIsTwoScalars(string text, bool expected) =>
        Assert.Equal(expected, GraphemeShaper.HasMultipleScalars(text));

    [Fact]
    public void BasePlusCombiningAcuteShapesToOneClusterWithBaseAdvance()
    {
        using var typeface = Load(DejaVuSans);
        using var shaper = new GraphemeShaper();
        Span<ushort> glyphs = stackalloc ushort[8];
        Span<SKPoint> positions = stackalloc SKPoint[8];
        int count = shaper.Shape("e\u0301", typeface, 20f, glyphs, positions, out float advance);
        Assert.InRange(count, 1, 2);
        using var font = new SKFont(typeface, 20f);
        Assert.Equal(font.MeasureText("e"), advance, 0.5f);
        for (int i = 0; i < count; i++) Assert.NotEqual(0, glyphs[i]);
        if (count == 2)
        {
            // The mark sits over the base (GPOS mark attachment), not after its advance.
            Assert.True(positions[1].X < advance, $"mark x {positions[1].X} must lie within base advance {advance}");
        }
    }

    [Fact]
    public void MarkWithoutPrecomposedFormIsPositionedOverBaseAndHonoursFeatures()
    {
        using var typeface = Load(DejaVuSans);
        using var shaper = new GraphemeShaper();
        Span<ushort> glyphs = stackalloc ushort[8];
        Span<SKPoint> positions = stackalloc SKPoint[8];
        int count = shaper.Shape("q\u0301", typeface, 20f, glyphs, positions, out float advance);
        Assert.Equal(2, count);
        using var font = new SKFont(typeface, 20f);
        Assert.Equal(font.MeasureText("q"), advance, 0.5f);
        Assert.True(positions[1].X < advance);
        Assert.True(positions[1].X > 0f || positions[1].Y != 0f);

        // font.features reach HarfBuzz: disabling GPOS mark attachment moves the mark.
        using var noMark = new GraphemeShaper(FontFeatureParser.Parse(new[] { "-mark" }));
        Span<SKPoint> plain = stackalloc SKPoint[8];
        Assert.Equal(2, noMark.Shape("q\u0301", typeface, 20f, glyphs, plain, out _));
        Assert.NotEqual(positions[1], plain[1]);
    }

    [Theory]
    [InlineData("🇺🇸")]
    [InlineData("👨\u200D👩\u200D👧")]
    [InlineData("1\uFE0F\u20E3")]
    [InlineData("👍🏽")]
    public void EmojiSequencesWithoutColourFontReportNotdef(string text)
    {
        using var typeface = Load(DejaVuSans);
        using var font = new SKFont(typeface, 20f);
        if (font.ContainsGlyphs(text)) Assert.Skip("DejaVu unexpectedly covers the sequence");
        using var shaper = new GraphemeShaper();
        Span<ushort> glyphs = stackalloc ushort[GraphemeShaper.MaxGlyphs];
        Span<SKPoint> positions = stackalloc SKPoint[GraphemeShaper.MaxGlyphs];
        Assert.Equal(0, shaper.Shape(text, typeface, 20f, glyphs, positions, out _));
        Assert.Null(shaper.CreateBlob(text, font));
    }

    [Theory]
    [InlineData("q\u0301")]
    [InlineData("🇺🇸")]
    [InlineData("👨\u200D👩\u200D👧")]
    [InlineData("1\uFE0F\u20E3")]
    public void AtlasRastersMultiScalarGraphemesWithoutCrashing(string text)
    {
        using var typeface = Load(DejaVuSans);
        using var atlas = new GlyphAtlas(typeface, 20f, initialSize: 256);
        atlas.EnsureGlyph(new GlyphKey(text, typeface, 20f, GlyphStyle.Regular), out _);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("q\u0301", typeface, 20f, GlyphStyle.Regular), out var mark));
        Assert.True(mark.Width > 0 && mark.Height > 0);
    }

    [Fact]
    public void ShapedMarkInkMatchesBlobDrawnAtCellOrigin()
    {
        using var typeface = Load(DejaVuSans);
        using var atlas = new GlyphAtlas(typeface, 20f, initialSize: 256);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("q\u0301", typeface, 20f, GlyphStyle.Regular), out var shaped));
        Assert.True(atlas.EnsureGlyph(new GlyphKey("q", typeface, 20f, GlyphStyle.Regular), out var bare));
        // The acute sits above the x-height: the cluster's ink extends higher than the bare base.
        Assert.True(shaped.Height > bare.Height);
        Assert.True(shaped.Width <= bare.Advance + 2f);
    }

    [Fact]
    public void FaceCacheIsBoundedLeastRecentlyUsed()
    {
        using var a = Load(DejaVuSans);
        using var b = Load(DejaVuSansBold);
        using var c = Load(DejaVuSansOblique);
        using var shaper = new GraphemeShaper(capacity: 2);
        Span<ushort> glyphs = stackalloc ushort[8];
        Span<SKPoint> positions = stackalloc SKPoint[8];
        shaper.Shape("q\u0301", a, 20f, glyphs, positions, out _);
        shaper.Shape("q\u0301", b, 20f, glyphs, positions, out _);
        Assert.Equal(2, shaper.CachedFaceCount);
        shaper.Shape("q\u0301", c, 20f, glyphs, positions, out _);
        Assert.Equal(2, shaper.CachedFaceCount);
        Assert.Equal(2, shaper.Shape("q\u0301", a, 20f, glyphs, positions, out _));
        Assert.Equal(2, shaper.CachedFaceCount);
    }

    [Fact]
    public void ShapingOnCacheHitDoesNotAllocate()
    {
        using var typeface = Load(DejaVuSans);
        using var shaper = new GraphemeShaper(FontFeatureParser.Parse(new[] { "+kern" }));
        var glyphs = new ushort[8];
        var positions = new SKPoint[8];
        const string text = "q\u0301";
        shaper.Shape(text, typeface, 20f, glyphs, positions, out _);
        shaper.Shape(text, typeface, 20f, glyphs, positions, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            shaper.Shape(text, typeface, 20f, glyphs, positions, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
