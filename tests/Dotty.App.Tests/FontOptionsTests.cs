using System.Collections.Generic;
using System.Linq;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class FontFeatureParserTests
{
    [Theory]
    [InlineData("+calt", "calt", 1u)]
    [InlineData("-liga", "liga", 0u)]
    [InlineData("ss01", "ss01", 1u)]
    [InlineData("cv01=2", "cv01", 2u)]
    [InlineData(" cv01 = 3 ", "cv01", 3u)]
    [InlineData("liga=off", "liga", 0u)]
    [InlineData("liga=ON", "liga", 1u)]
    [InlineData("zero=0", "zero", 0u)]
    [InlineData("salt=65535", "salt", 65535u)]
    public void ValidEntries(string entry, string tag, uint value)
    {
        Assert.True(FontFeatureParser.TryParse(entry, out var feature, out string? error));
        Assert.Null(error);
        Assert.Equal(new FontFeature(tag, value), feature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("[3:5]calt")]
    [InlineData("calt[3:5]")]
    [InlineData("cal")]
    [InlineData("calts")]
    [InlineData("+")]
    [InlineData("c-lt")]
    [InlineData("calt=")]
    [InlineData("calt=-1")]
    [InlineData("calt=65536")]
    [InlineData("calt=x")]
    [InlineData("+calt=1")]
    [InlineData("kérn")]
    public void InvalidEntriesAreRejectedWithAMessage(string? entry)
    {
        Assert.False(FontFeatureParser.TryParse(entry, out _, out string? error));
        Assert.StartsWith("font.features:", error);
    }

    [Fact]
    public void LaterSettingOfATagWinsAndInvalidEntriesAreSkippedAndReported()
    {
        var errors = new List<string>();
        var features = FontFeatureParser.Parse(new[] { "+calt", "bad!", "-liga", "-calt", "[1:2]ss01" }, errors);
        Assert.Equal(new[] { "-liga", "-calt" }, features.Select(f => f.ToString()));
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void NullListParsesToEmpty() => Assert.Empty(FontFeatureParser.Parse(null));
}

public sealed class FontOptionsTests
{
    [Theory]
    [InlineData("thin", 100)]
    [InlineData("ExtraLight", 200)]
    [InlineData("extra-light", 200)]
    [InlineData("light", 300)]
    [InlineData("regular", 400)]
    [InlineData("Normal", 400)]
    [InlineData("medium", 500)]
    [InlineData("semi bold", 600)]
    [InlineData("bold", 700)]
    [InlineData("extrabold", 800)]
    [InlineData("black", 900)]
    [InlineData("1", 1)]
    [InlineData("350", 350)]
    [InlineData("1000", 1000)]
    public void WeightNamesAndNumbers(string text, int expected)
    {
        Assert.True(FontWeights.TryParse(text, out int weight));
        Assert.Equal(expected, weight);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("-400")]
    [InlineData("heavyish")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidWeights(string? text)
    {
        Assert.False(FontWeights.TryParse(text, out int weight));
        Assert.Equal(400, weight);
    }

    [Theory]
    [InlineData("regular", 400, 700)]
    [InlineData("light", 300, 700)]
    [InlineData("medium", 500, 700)]
    [InlineData("semibold", 600, 900)]
    [InlineData("bold", 700, 900)]
    public void BoldWeightFollowsTheRegularWeight(string weight, int regular, int bold)
    {
        var options = FontOptions.Create("Mono", null, null, null, weight, null);
        Assert.Equal(regular, options.Weight);
        Assert.Equal(bold, options.BoldWeight);
        Assert.Equal(new FontFaceStyle(regular, false), options.StyleFor(GlyphStyle.Regular));
        Assert.Equal(new FontFaceStyle(bold, true), options.StyleFor(GlyphStyle.Bold | GlyphStyle.Italic));
    }

    [Fact]
    public void InvalidWeightIsReportedAndUsesRegular()
    {
        var errors = new List<string>();
        var options = FontOptions.Create("Mono", null, null, null, "chunky", null, errors);
        Assert.Equal(400, options.Weight);
        Assert.Single(errors);
        Assert.StartsWith("font.weight:", errors[0]);
    }

    [Theory]
    [InlineData("A, B ,C", "A|B|C")]
    [InlineData("A,,  ,B", "A|B")]
    [InlineData("A, a, B, A", "A|B")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void FamilyListParsing(string? list, string expected) =>
        Assert.Equal(expected, string.Join("|", FontOptions.ParseFamilyList(list)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void EmptyFamilyUsesTheOsStack(string? family) =>
        Assert.Equal(FontOptions.ParseFamilyList(DottyDefaults.FontFamily),
            FontOptions.Create(family, null, null, null, null, null).Families);

    [Fact]
    public void StyleFamiliesAreSelectedPerStyle()
    {
        var options = FontOptions.Create("Mono", "B1, B2", "I1", "BI1", null, null);
        Assert.Empty(options.FamiliesFor(GlyphStyle.Regular));
        Assert.Equal(new[] { "B1", "B2" }, options.FamiliesFor(GlyphStyle.Bold));
        Assert.Equal(new[] { "I1" }, options.FamiliesFor(GlyphStyle.Italic));
        Assert.Equal(new[] { "BI1" }, options.FamiliesFor(GlyphStyle.Bold | GlyphStyle.Italic));
    }

    [Fact]
    public void EqualityCoversEveryField()
    {
        var baseline = FontOptions.Create("A, B", "Bb", "Ii", "BbIi", "light", new[] { "-liga" });
        Assert.Equal(baseline, FontOptions.Create("a,b", "bb", "ii", "bbii", "300", new[] { "-liga" }));
        Assert.Equal(baseline.GetHashCode(), FontOptions.Create("a,b", "bb", "ii", "bbii", "300", new[] { "-liga" }).GetHashCode());
        Assert.NotEqual(baseline, FontOptions.Create("B, A", "Bb", "Ii", "BbIi", "light", new[] { "-liga" }));
        Assert.NotEqual(baseline, FontOptions.Create("A, B", "X", "Ii", "BbIi", "light", new[] { "-liga" }));
        Assert.NotEqual(baseline, FontOptions.Create("A, B", "Bb", "X", "BbIi", "light", new[] { "-liga" }));
        Assert.NotEqual(baseline, FontOptions.Create("A, B", "Bb", "Ii", "X", "light", new[] { "-liga" }));
        Assert.NotEqual(baseline, FontOptions.Create("A, B", "Bb", "Ii", "BbIi", "bold", new[] { "-liga" }));
        Assert.NotEqual(baseline, FontOptions.Create("A, B", "Bb", "Ii", "BbIi", "light", new[] { "+liga" }));
    }
}
