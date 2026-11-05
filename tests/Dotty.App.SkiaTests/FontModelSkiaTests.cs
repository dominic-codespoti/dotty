using System;
using System.Linq;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>Font model and atlas hygiene against real faces. Tests that need DejaVu Sans Mono skip without it.</summary>
public sealed class FontModelSkiaTests
{
    private const string DejaVu = "DejaVu Sans Mono";

    private static IFontFace RequireDejaVu(FontFaceStyle style)
    {
        var face = SkiaFontCatalog.Shared.MatchFamily(DejaVu, style);
        if (face == null || !string.Equals(face.FamilyName, DejaVu, StringComparison.OrdinalIgnoreCase))
            Assert.Skip($"{DejaVu} is not installed");
        return face!;
    }

    [Fact]
    public void DejaVuIsAMonochromeFace()
    {
        var face = (SkiaFontFace)RequireDejaVu(FontFaceStyle.Regular);
        Assert.False(face.IsColor);
        Assert.False(ColorFontTables.IsColorFace(face.Typeface));
        Assert.True(face.Typeface.TryGetTableTags(out uint[] tags));
        Assert.Contains(ColorFontTables.Tag("glyf"), tags);
    }

    [Theory]
    [InlineData("CBDT", true)]
    [InlineData("CBLC", true)]
    [InlineData("sbix", true)]
    [InlineData("COLR", true)]
    [InlineData("SVG ", true)]
    [InlineData("CPAL", false)]
    [InlineData("glyf", false)]
    [InlineData("cmap", false)]
    public void ColourTableTags(string tag, bool color)
    {
        Assert.Equal(color, ColorFontTables.ContainsColorTable(new[] { ColorFontTables.Tag("cmap"), ColorFontTables.Tag(tag) }));
    }

    [Fact]
    public void TagRequiresFourCharacters() =>
        Assert.Throws<ArgumentException>(() => ColorFontTables.Tag("SVG"));

    [Fact]
    public void MissingFamilyIsNotInstalled() =>
        Assert.Null(SkiaFontCatalog.Shared.MatchFamily("Definitely Missing Dotty Test Family", FontFaceStyle.Regular));

    [Fact]
    public void ChainUsesRealBoldAndObliqueFacesOfDejaVu()
    {
        RequireDejaVu(FontFaceStyle.Regular);
        using var chain = new FontFallbackChain(FontOptions.Create(DejaVu, null, null, null, null, null), SkiaFontCatalog.Shared);

        var regular = chain.Resolve("A");
        var bold = chain.Resolve("A", GlyphStyle.Bold);
        var italic = chain.Resolve("A", GlyphStyle.Italic);
        var boldItalic = chain.Resolve("A", GlyphStyle.Bold | GlyphStyle.Italic);

        Assert.Equal(DejaVu, regular.Face.FamilyName);
        Assert.Equal(400, regular.Face.Weight);
        foreach (var r in new[] { bold, italic, boldItalic })
        {
            Assert.False(r.IsFallback);
            Assert.False(r.SyntheticBold);
            Assert.False(r.SyntheticItalic);
            Assert.NotSame(regular.Typeface, r.Typeface);
        }
        Assert.True(bold.Face.Weight >= 600);
        Assert.True(italic.Face.IsItalic);
        Assert.True(boldItalic.Face.IsItalic && boldItalic.Face.Weight >= 600);
    }

    [Fact]
    public void AtlasDrawsBoldWithTheRealFace()
    {
        var face = (SkiaFontFace)RequireDejaVu(FontFaceStyle.Regular);
        using var chain = new FontFallbackChain(FontOptions.Create(DejaVu, null, null, null, null, null), SkiaFontCatalog.Shared);
        using var atlas = new GlyphAtlas(face.Typeface, 24f, fallbackChain: chain);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("l", face.Typeface, 24f, GlyphStyle.Regular), out var regular));
        Assert.True(atlas.EnsureGlyph(new GlyphKey("l", face.Typeface, 24f, GlyphStyle.Bold), out var bold));
        Assert.False(regular.IsColor);
        Assert.False(bold.IsColor);
        // DejaVu Sans Mono Bold's stem is heavier than the regular one: more ink in the A8 page.
        long regularInk = Ink(atlas, regular), boldInk = Ink(atlas, bold);
        Assert.True(boldInk > regularInk * 5 / 4, $"bold ink {boldInk} vs regular ink {regularInk}");
    }

    private static long Ink(GlyphAtlas atlas, GlyphInfo glyph)
    {
        long sum = 0;
        atlas.WithAtlasBitmap(bitmap =>
        {
            for (int y = glyph.Y; y < glyph.Y + glyph.Height; y++)
                for (int x = glyph.X; x < glyph.X + glyph.Width; x++)
                    sum += bitmap.GetPixel(x, y).Alpha;
        });
        return sum;
    }

    [Fact]
    public void AddingGlyphsAndGrowingKeepTheLayoutEpoch()
    {
        using var atlas = new GlyphAtlas(SKTypeface.Default, 48f, initialSize: 64);
        int epoch = atlas.LayoutEpoch;
        int content = atlas.ContentVersion;
        int width = atlas.Width;
        for (char c = 'A'; c <= 'Z'; c++)
            Assert.True(atlas.EnsureGlyph(new GlyphKey(c.ToString(), SKTypeface.Default, 48f, GlyphStyle.Regular), out _));
        Assert.True(atlas.Width > width, "the 64 px atlas should have grown");
        Assert.True(atlas.ContentVersion > content);
        Assert.Equal(epoch, atlas.LayoutEpoch);
        Assert.False(atlas.NeedsRebuild);
    }

    [Fact]
    public void FullAtlasRequestsRebuildAndRecoversAfterIt()
    {
        var font = SKTypeface.Default;
        // 160 px keeps every "g<n>" glyph under the 512 px entry limit, so only a full page stops placement.
        using var atlas = new GlyphAtlas(font, 160f, initialSize: 64);
        int epoch = atlas.LayoutEpoch;
        int i = 0;
        while (atlas.EnsureGlyph(new GlyphKey($"g{i}", font, 160f, GlyphStyle.Regular), out _))
            Assert.True(++i < 2000, "atlas never filled within 2000 glyphs at 160px");
        atlas.PublishPendingGlyphs();

        Assert.True(atlas.NeedsRebuild);
        Assert.Equal(epoch, atlas.LayoutEpoch);
        Assert.False(atlas.EnsureGlyph(new GlyphKey($"g{i}", font, 160f, GlyphStyle.Regular), out _));
        Assert.True(atlas.TryGetFallbackGlyph(out _));

        int content = atlas.ContentVersion;
        atlas.Rebuild();
        Assert.False(atlas.NeedsRebuild);
        Assert.Equal(epoch + 1, atlas.LayoutEpoch);
        Assert.True(atlas.ContentVersion > content);
        Assert.Equal(0, atlas.EntryCount);
        Assert.False(atlas.TryGetGlyph(new GlyphKey("g0", font, 160f, GlyphStyle.Regular), out _));
        Assert.True(atlas.TryGetFallbackGlyph(out _));
        Assert.True(atlas.EnsureGlyph(new GlyphKey($"g{i}", font, 160f, GlyphStyle.Regular), out _));
        Assert.Equal(1, atlas.RebuildCount);
    }

    [Fact]
    public void OversizedGlyphFailsWithoutRequestingRebuild()
    {
        // A 2000 px glyph exceeds the 512 px entry limit: no rebuild can help it.
        using var atlas = new GlyphAtlas(SKTypeface.Default, 2000f, initialSize: 64);
        Assert.False(atlas.EnsureGlyph(new GlyphKey("W", SKTypeface.Default, 2000f, GlyphStyle.Regular), out _));
        Assert.False(atlas.NeedsRebuild);
    }

    [Fact]
    public void ReplacingTheChainOrSymbolMapBumpsTheEpoch()
    {
        var font = SKTypeface.Default;
        using var first = new FontFallbackChain(font);
        using var second = new FontFallbackChain(font);
        using var atlas = new GlyphAtlas(font, 16f, fallbackChain: first);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("A", font, 16f, GlyphStyle.Regular), out _));
        int epoch = atlas.LayoutEpoch;

        atlas.FallbackChain = first;                      // same chain: no change
        Assert.Equal(epoch, atlas.LayoutEpoch);
        atlas.FallbackChain = second;
        Assert.Equal(epoch + 1, atlas.LayoutEpoch);
        Assert.Equal(0, atlas.EntryCount);

        second.SymbolMap = SymbolMap.Parse(new[] { "U+2500: Some Family" });
        Assert.Equal(epoch + 2, atlas.LayoutEpoch);
        first.SymbolMap = SymbolMap.Parse(new[] { "U+2500: Some Family" });   // detached chain
        Assert.Equal(epoch + 2, atlas.LayoutEpoch);
    }

    [Fact]
    public void ServiceSwapsInANewChainForAnExistingAtlas()
    {
        var font = SKTypeface.Default;
        using var first = new FontFallbackChain(font);
        using var second = new FontFallbackChain(font);
        var atlas = GlyphAtlasService.GetOrCreateAtlas(font, 17.5f, fallbackChain: first);
        Assert.Same(first, atlas.FallbackChain);
        Assert.Same(atlas, GlyphAtlasService.GetOrCreateAtlas(font, 17.5f));
        Assert.Same(first, atlas.FallbackChain);
        Assert.Same(atlas, GlyphAtlasService.GetOrCreateAtlas(font, 17.5f, fallbackChain: second));
        Assert.Same(second, atlas.FallbackChain);
    }

    [Fact]
    public void DefaultChainDoesNotGrowPastTheDynamicBound()
    {
        using var chain = FontFallbackChain.CreateDefault(SKTypeface.Default);
        int configured = chain.Faces.Count;
        foreach (int cp in Enumerable.Range(0x4E00, 64).Concat(Enumerable.Range(0x1F600, 32)).Concat(Enumerable.Range(0x0900, 64)))
            chain.Resolve(char.ConvertFromUtf32(cp));
        Assert.InRange(chain.Faces.Count - configured, 0, FontFallbackChain.MaxDynamicFaces);
    }
}
