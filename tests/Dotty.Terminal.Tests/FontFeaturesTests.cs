using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// P2 font features + symbol-map parsing: malformed entries dropped, valid
/// entries applied. Rendering proof stays in Skia golden/screenshot tests.
/// </summary>
public sealed class FontFeaturesTests
{
    [Theory]
    [InlineData("calt", true)]
    [InlineData("liga=0", true)]
    [InlineData("ss01", true)]
    [InlineData("ss01=1", true)]
    [InlineData(" liga ", true)]
    [InlineData("", false)]
    [InlineData("toolongtagname", false)]
    [InlineData("ab", false)]
    public void FontFeatureList_ParsesKnownForms(string entry, bool expected)
    {
        Assert.Equal(expected, FontFeatureList.TryParse(entry, out _));
    }

    [Fact]
    public void FontFeatureList_Parse_DropsMalformedKeepsValid()
    {
        var features = FontFeatureList.Parse(new[] { "calt", "", "toolongtagname", "liga=0" });
        Assert.Equal(2, features.Length);
    }

    [Fact]
    public void FontFeatureList_Normalize_IsStableAcrossOrder()
    {
        string a = FontFeatureList.Normalize(new[] { "liga=0", "calt" });
        string b = FontFeatureList.Normalize(new[] { "calt", "liga=0" });
        Assert.Equal(a, b);
        Assert.NotEqual(string.Empty, a);
    }

    [Theory]
    [InlineData("U+2500-U+257F: Symbols Nerd Font Mono", 0x2500, 0x257F, "Symbols Nerd Font Mono")]
    [InlineData("U+E000: MyIcons", 0xE000, 0xE000, "MyIcons")]
    [InlineData("0x1F300-0x1FAFF: Noto Color Emoji", 0x1F300, 0x1FAFF, "Noto Color Emoji")]
    public void SymbolMap_ParsesRanges(string line, int start, int end, string family)
    {
        Assert.True(SymbolMap.TryParseLine(line, out int s, out int e, out string? f));
        Assert.Equal(start, s);
        Assert.Equal(end, e);
        Assert.Equal(family, f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon-here")]
    [InlineData("U+ZZZZ: Family")]
    [InlineData("U+2600-U+2500: Family")]
    [InlineData("U+2500: ")]
    public void SymbolMap_RejectsMalformedLines(string line)
    {
        Assert.False(SymbolMap.TryParseLine(line, out _, out _, out _));
    }

    [Fact]
    public void SymbolMap_ResolvesFirstMatchingRange()
    {
        var map = SymbolMap.Parse(new[] { "U+2500-U+257F: BoxFont", "U+2500: ExactFont" });
        Assert.Equal(2, map.Count);
        Assert.True(map.TryResolveFamily(0x2550, out string? family));
        Assert.Equal("BoxFont", family);
        Assert.False(map.TryResolveFamily(0x0041, out _));
    }
}
