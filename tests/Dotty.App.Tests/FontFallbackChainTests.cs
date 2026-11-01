using System;
using System.Linq;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class FontFallbackChainTests
{
    private static readonly int[] Ascii = FakeFace.Range(0x20, 0x7E).ToArray();

    private static FontFallbackChain Chain(FakeFontCatalog catalog, string family, string? weight = null,
        string? bold = null, string? italic = null, string? boldItalic = null, bool tail = false) =>
        new(FontOptions.Create(family, bold, italic, boldItalic, weight, null), catalog, tail);

    private static string Describe(FontResolution r) =>
        $"{r.Face.FamilyName}/{r.Face.Weight}{(r.Face.IsItalic ? "i" : "")}"
        + (r.IsFallback ? " fallback" : "")
        + (r.SyntheticBold ? " +sb" : "")
        + (r.SyntheticItalic ? " +si" : "");

    [Fact]
    public void ConfiguredListIsTheFallbackOrderSkippingMissingAndDuplicateFamilies()
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("B", Ascii.Concat(new[] { 0x2500 })),
            new FakeFace("C", new[] { 0x2500, 0x4E16 }),
            new FakeFace("D", new[] { 0x4E16, 0x1F600 }));
        using var chain = Chain(catalog, "Missing, B, C, b, D");

        Assert.Equal(new[] { "B", "C", "D" }, chain.Faces.Select(f => f.FamilyName));
        Assert.Equal("B", chain.Resolve("A").Face.FamilyName);
        Assert.Equal("B", chain.Resolve("\u2500").Face.FamilyName);   // B before C
        Assert.Equal("C", chain.Resolve("\u4E16").Face.FamilyName);   // C before D
        Assert.Equal("D", chain.Resolve("\U0001F600").Face.FamilyName);
        Assert.False(chain.Resolve("A").IsFallback);
        Assert.True(chain.Resolve("\u4E16").IsFallback);
    }

    [Fact]
    public void ReorderingTheListChangesThePrimaryAndTheWinner()
    {
        var catalog = new FakeFontCatalog(new FakeFace("B", Ascii), new FakeFace("C", Ascii));
        Assert.Equal("C", Chain(catalog, "C, B").Resolve("A").Face.FamilyName);
        Assert.Equal("B", Chain(catalog, "B, C").Resolve("A").Face.FamilyName);
    }

    [Fact]
    public void TailFamiliesFollowTheConfiguredListWhenInstalled()
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("Mono", Ascii),
            new FakeFace("Noto Color Emoji", new[] { 0x1F600 }, color: true),
            new FakeFace("Symbols Nerd Font Mono", new[] { 0xE0B0 }));
        using var chain = Chain(catalog, "Mono", tail: true);
        var families = chain.Faces.Select(f => f.FamilyName).ToArray();
        Assert.Equal("Mono", families[0]);
        Assert.Equal(new[] { "Mono", "Symbols Nerd Font Mono", "Noto Color Emoji" }, families);
    }

    [Fact]
    public void NoInstalledFamilyUsesTheCatalogDefault()
    {
        var catalog = new FakeFontCatalog();
        using var chain = Chain(catalog, "Nope, Neither");
        Assert.Same(catalog.DefaultFace, chain.PrimaryFace);
        Assert.Same(catalog.DefaultFace, chain.Resolve("A").Face);
    }

    [Fact]
    public void EmojiPresentationPrefersTheColourFaceOverAnEarlierTextFace()
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("Mono", Ascii.Concat(new[] { 0x2764 })),
            new FakeFace("Emoji", new[] { 0x2764, 0x1F600 }, color: true));
        using var chain = Chain(catalog, "Mono, Emoji");
        Assert.Equal("Mono", chain.Resolve("\u2764").Face.FamilyName);
        Assert.Equal("Emoji", chain.Resolve("\u2764\uFE0F").Face.FamilyName);
    }

    // Style resolution table. Installed: Mono regular/bold/italic (no bold-italic), Plain regular only,
    // Heavy at 700 and 900, Alt Bold (an explicit bold family). Expected: family/weight[i] + flags.
    [Theory]
    [InlineData("Mono", null, null, "A", GlyphStyle.Regular, "Mono/400")]
    [InlineData("Mono", null, null, "A", GlyphStyle.Bold, "Mono/700")]
    [InlineData("Mono", null, null, "A", GlyphStyle.Italic, "Mono/400i")]
    [InlineData("Mono", null, null, "A", GlyphStyle.Bold | GlyphStyle.Italic, "Mono/400i +sb")]  // no bold italic: real italic + synthetic bold
    [InlineData("Plain", null, null, "A", GlyphStyle.Bold, "Plain/400 +sb")]
    [InlineData("Plain", null, null, "A", GlyphStyle.Italic, "Plain/400 +si")]
    [InlineData("Plain", null, null, "A", GlyphStyle.Bold | GlyphStyle.Italic, "Plain/400 +sb +si")]
    [InlineData("Heavy", "bold", null, "A", GlyphStyle.Regular, "Heavy/700")]
    [InlineData("Heavy", "bold", null, "A", GlyphStyle.Bold, "Heavy/900")]
    [InlineData("Mono", "bold", null, "A", GlyphStyle.Bold, "Mono/700 +sb")]                    // regular already 700
    [InlineData("Mono", null, "Alt Bold", "A", GlyphStyle.Bold, "Alt Bold/700")]               // explicit family wins
    [InlineData("Mono", null, "Missing Bold", "A", GlyphStyle.Bold, "Mono/700")]               // missing explicit: family bold
    [InlineData("Mono", null, "Alt Bold", "\u00E9", GlyphStyle.Bold, "Mono/400 +sb")]          // styled face lacks é: regular + synthetic
    [InlineData("Mono, Plain", null, null, "\u2500", GlyphStyle.Bold, "Plain/400 fallback +sb")] // fallback family without bold
    public void StyleResolution(string family, string? weight, string? boldFamily, string cluster, GlyphStyle style, string expected)
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("Mono", Ascii.Concat(new[] { 0xE9 })),
            new FakeFace("Mono", Ascii.Concat(new[] { 0xE9 }), weight: 700),
            new FakeFace("Mono", Ascii.Concat(new[] { 0xE9 }), italic: true),
            new FakeFace("Plain", Ascii.Concat(new[] { 0x2500 })),
            new FakeFace("Heavy", Ascii, weight: 700),
            new FakeFace("Heavy", Ascii, weight: 900),
            new FakeFace("Alt Bold", Ascii, weight: 700));
        using var chain = Chain(catalog, family, weight, bold: boldFamily);
        Assert.Equal(expected, Describe(chain.Resolve(cluster, style)));
    }

    [Fact]
    public void ItalicAndBoldItalicFamiliesApplyOnlyToThePrimaryFamily()
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("Mono", Ascii),
            new FakeFace("Other", new[] { 0x2500 }),
            new FakeFace("Script", Ascii.Concat(new[] { 0x2500 }), italic: true),
            new FakeFace("Script BI", Ascii.Concat(new[] { 0x2500 }), weight: 700, italic: true));
        using var chain = Chain(catalog, "Mono, Other", italic: "Script", boldItalic: "Script BI");
        Assert.Equal("Script/400i", Describe(chain.Resolve("A", GlyphStyle.Italic)));
        Assert.Equal("Script BI/700i", Describe(chain.Resolve("A", GlyphStyle.Bold | GlyphStyle.Italic)));
        Assert.Equal("Other/400 fallback +si", Describe(chain.Resolve("\u2500", GlyphStyle.Italic)));
    }

    [Fact]
    public void SymbolMapFacesComeFirstAndChangingTheMapClearsResolutions()
    {
        var catalog = new FakeFontCatalog(
            new FakeFace("Mono", Ascii.Concat(new[] { 0x2500 })),
            new FakeFace("Box", new[] { 0x2500 }),
            new FakeFace("Box2", new[] { 0x2501 }));
        using var chain = Chain(catalog, "Mono");
        Assert.Equal("Mono", chain.Resolve("\u2500").Face.FamilyName);

        int changes = 0;
        chain.SymbolMapChanged += (_, _) => changes++;
        chain.SymbolMap = SymbolMap.Parse(new[] { "U+2500-U+257F: Missing, Box, Box2" });
        Assert.Equal(1, changes);
        Assert.Equal("Box", chain.Resolve("\u2500").Face.FamilyName);
        Assert.True(chain.Resolve("\u2500").IsFallback);
        // Box lacks U+2501; Box2 is the next listed family.
        Assert.Equal("Box2", chain.Resolve("\u2501").Face.FamilyName);

        chain.SymbolMap = null;
        Assert.Equal(2, changes);
        Assert.Equal("Mono", chain.Resolve("\u2500").Face.FamilyName);
    }

    [Fact]
    public void UncoveredScalarsUseABoundedSetOfSystemFaces()
    {
        var catalog = new FakeFontCatalog(new FakeFace("Mono", Ascii));
        for (int i = 0; i < FontFallbackChain.MaxDynamicFaces + 4; i++)
            catalog.AddSystem(new FakeFace("System" + i, new[] { 0x10000 + i }));
        using var chain = Chain(catalog, "Mono");

        for (int i = 0; i < FontFallbackChain.MaxDynamicFaces + 4; i++)
        {
            var resolution = chain.Resolve(char.ConvertFromUtf32(0x10000 + i));
            Assert.Equal("System" + i, resolution.Face.FamilyName);
            Assert.True(resolution.IsFallback);
        }
        Assert.Equal(FontFallbackChain.MaxDynamicFaces, chain.DynamicFaceCount);
        Assert.Equal(1 + FontFallbackChain.MaxDynamicFaces, chain.Faces.Count);
    }

    [Fact]
    public void SystemFaceThatDoesNotCoverTheClusterIsRejected()
    {
        var catalog = new FakeFontCatalog(new FakeFace("Mono", Ascii))
            .AddSystem(new FakeFace("Partial", new[] { 0x10000 }));
        using var chain = Chain(catalog, "Mono");
        var resolution = chain.Resolve("\U00010000\u0301");
        Assert.Same(chain.PrimaryFace, resolution.Face);
        Assert.Equal(0, chain.DynamicFaceCount);
    }

    [Fact]
    public void ResolutionsAreCachedAndTheCacheIsBounded()
    {
        var catalog = new FakeFontCatalog(new FakeFace("Mono", Ascii));
        using var chain = Chain(catalog, "Mono");
        chain.Resolve("\U00010000");
        chain.Resolve("\U00010000");
        Assert.Equal(1, catalog.CharacterQueries);

        for (int i = 0; i < FontFallbackChain.ResolutionCacheCapacity + 100; i++)
            chain.Resolve("A" + i);
        Assert.Equal(FontFallbackChain.ResolutionCacheCapacity, chain.ResolutionCacheCount);
    }

    [Fact]
    public void FeaturesAreKeptOnTheChain()
    {
        var options = FontOptions.Create("Mono", null, null, null, null, new[] { "-liga", "+calt", "cv01=2" });
        using var chain = new FontFallbackChain(options, new FakeFontCatalog(new FakeFace("Mono", Ascii)), includeTail: false);
        Assert.Equal(new[] { "-liga", "+calt", "cv01=2" }, chain.Features.Select(f => f.ToString()));
    }

    [Fact]
    public void EmptyClusterResolvesToThePrimaryFace()
    {
        using var chain = Chain(new FakeFontCatalog(new FakeFace("Mono", Ascii)), "Mono");
        Assert.Equal("Mono/400", Describe(chain.Resolve(string.Empty, GlyphStyle.Bold)));
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new FontFallbackChain(null!, new FakeFontCatalog()));
        Assert.Throws<ArgumentNullException>(() => new FontFallbackChain(FontOptions.Default, null!));
    }
}
