using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class SymbolMapTests
{
    [Theory]
    [InlineData("U+2500-U+257F: First Font, Second Font", 0x2500, 0x257F, "First Font")]
    [InlineData("U+E000: MyIcons", 0xE000, 0xE000, "MyIcons")]
    [InlineData("0x1F300-0x1FAFF: Noto Color Emoji", 0x1F300, 0x1FAFF, "Noto Color Emoji")]
    public void ParsesRangeAndFirstFamily(string line, int start, int end, string family)
    {
        Assert.True(SymbolMap.TryParseLine(line, out int s, out int e, out string? f));
        Assert.Equal(start, s);
        Assert.Equal(end, e);
        Assert.Equal(family, f);
    }

    [Fact]
    public void KeepsCommaSeparatedFamiliesInPriorityOrder()
    {
        var map = SymbolMap.Parse(new[] { "U+2500-U+257F: Missing First, Missing Second, Missing Third" });
        Assert.True(SymbolMap.TryParseLine("U+2500: First, Second", out _, out _, out string[] families));
        Assert.Equal(new[] { "First", "Second" }, families);
        Assert.True(map.TryResolveFamily(0x2550, out string? family));
        Assert.Equal("Missing First", family);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon-here")]
    [InlineData("U+ZZZZ: Family")]
    [InlineData("U+2600-U+2500: Family")]
    [InlineData("U+2500: , ,")]
    public void RejectsMalformedLines(string line)
    {
        Assert.False(SymbolMap.TryParseLine(line, out _, out _, out string? family));
    }

    [Fact]
    public void FirstOverlappingRangeWins()
    {
        var map = SymbolMap.Parse(new[] { "U+2500-U+257F: First", "U+2500: Second" });
        Assert.True(map.TryResolveFamily(0x2550, out string? family));
        Assert.Equal("First", family);
    }

    [Fact]
    public void MapEqualityUsesOrderedRangeAndFamilyEntries()
    {
        var original = SymbolMap.Parse(new[] { "U+2500-U+257F: First, Second", "U+E000: Icons" });
        Assert.True(original.HasSameEntries(SymbolMap.Parse(new[] { "U+2500-U+257F: First, Second", "U+E000: Icons" })));
        Assert.False(original.HasSameEntries(SymbolMap.Parse(new[] { "U+2500-U+257F: Second, First", "U+E000: Icons" })));
        Assert.False(original.HasSameEntries(SymbolMap.Parse(new[] { "U+E000: Icons", "U+2500-U+257F: First, Second" })));
    }

    [Fact]
    public void MissingFamiliesResolveWithoutThrowing()
    {
        var map = SymbolMap.Parse(new[] { "U+E000: Definitely Missing Dotty Test Family" });
        Assert.Null(map.ResolveTypeface(0xE000));
        Assert.Null(map.ResolveTypeface(0xE000));
    }

    [Fact]
    public void EqualMapAssignmentDoesNotSignalInvalidation()
    {
        using var chain = new FontFallbackChain(SKTypeface.Default);
        int changes = 0;
        chain.SymbolMapChanged += (_, _) => changes++;
        chain.SymbolMap = SymbolMap.Parse(new[] { "U+2500-U+257F: First, Second" });
        Assert.Equal(1, changes);
        chain.SymbolMap = SymbolMap.Parse(new[] { "U+2500-U+257F: First, Second" });
        Assert.Equal(1, changes);
        chain.SymbolMap = SymbolMap.Parse(new[] { "U+2500-U+257F: Second, First" });
        Assert.Equal(2, changes);
    }
}
