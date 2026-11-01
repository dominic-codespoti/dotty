using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class FontChainResolutionCacheTests
{
    // Requested family lists, separated by '|' ("<null>" is null), and the lists that reach the chain
    // builder, in order. Family lists compare case-insensitively and ignore blanks and repeats.
    [Theory]
    [InlineData("Iosevka|Iosevka|Iosevka", "Iosevka")]
    [InlineData("Iosevka|Fira Code|Fira Code|Iosevka", "Iosevka|Fira Code|Iosevka")]
    [InlineData("<null>|<null>", "<null>")]
    [InlineData("iosevka|Iosevka| Iosevka ,iosevka", "iosevka")]
    [InlineData("A, B|B, A", "A, B|B, A")]
    public void BuildsOnlyWhenTheFontModelChanges(string requested, string expectedBuilds)
    {
        var built = new List<string?>();
        var catalog = new FakeFontCatalog();
        var cache = new FontChainResolutionCache(options =>
        {
            built.Add(string.Join(", ", options.Families));
            return new FontFallbackChain(options, catalog, includeTail: false);
        });

        foreach (string family in requested.Split('|'))
            cache.Resolve(FontOptions.Create(Decode(family), null, null, null, null, null));

        var expected = new List<string?>();
        foreach (string family in expectedBuilds.Split('|'))
            expected.Add(string.Join(", ", FontOptions.Create(Decode(family), null, null, null, null, null).Families));
        Assert.Equal(expected, built);
    }

    [Fact]
    public void WeightFeatureOrStyleFamilyChangeRebuilds()
    {
        int builds = 0;
        var catalog = new FakeFontCatalog();
        var cache = new FontChainResolutionCache(options =>
        {
            builds++;
            return new FontFallbackChain(options, catalog, includeTail: false);
        });

        var first = cache.Resolve(FontOptions.Create("A", null, null, null, "regular", null));
        Assert.Same(first, cache.Resolve(FontOptions.Create("A", "", " ", null, "400", new string[0])));
        cache.Resolve(FontOptions.Create("A", null, null, null, "bold", null));
        cache.Resolve(FontOptions.Create("A", null, null, null, "bold", new[] { "-liga" }));
        cache.Resolve(FontOptions.Create("A", "A Bold", null, null, "bold", new[] { "-liga" }));
        Assert.Equal(4, builds);
    }

    private static string? Decode(string family) => family == "<null>" ? null : family;
}
