using System.Collections.Generic;
using Dotty.Silk.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TypefaceResolutionCacheTests
{
    // Requested family lists, separated by '|' ("<null>" is null), and the lists that reach the
    // font manager, in order.
    [Theory]
    [InlineData("Iosevka|Iosevka|Iosevka", "Iosevka")]
    [InlineData("Iosevka|Fira Code|Fira Code|Iosevka", "Iosevka|Fira Code|Iosevka")]
    [InlineData("<null>|<null>", "<null>")]
    [InlineData("<null>|Iosevka|<null>", "<null>|Iosevka|<null>")]
    [InlineData("iosevka|Iosevka", "iosevka|Iosevka")]
    public void ResolvesOnlyWhenTheFamilyListChanges(string requested, string expectedResolutions)
    {
        var resolved = new List<string?>();
        var cache = new TypefaceResolutionCache(family =>
        {
            resolved.Add(family);
            return SKTypeface.Default;
        });

        foreach (string family in requested.Split('|'))
            Assert.Same(SKTypeface.Default, cache.Resolve(Decode(family)));

        var expected = new List<string?>();
        foreach (string family in expectedResolutions.Split('|'))
            expected.Add(Decode(family));
        Assert.Equal(expected, resolved);
    }

    private static string? Decode(string family) => family == "<null>" ? null : family;
}
