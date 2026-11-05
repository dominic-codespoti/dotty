using System.Linq;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClusterFontSelectorTests
{
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";   // 👨‍👩‍👧
    private const string Flag = "\U0001F1FA\U0001F1F8";                            // 🇺🇸
    private const string ThumbsSkin = "\U0001F44D\U0001F3FD";                      // 👍🏽
    private const string Keycap = "1\uFE0F\u20E3";                                 // 1️⃣
    private const string Scotland = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F";

    // Text face: ASCII, ❤ ☺ ⌚ (has text forms), keycap mark, combining acute, a Devanagari conjunct.
    private static readonly FakeFace Text = new("Mono",
        FakeFace.Scalars("1#*❤☺⌚\u20E3\u0301\u0915\u094D\u0937").Concat(FakeFace.Range(0x20, 0x7E)));

    // Colour face: the emoji in the cases below (not ZWJ/VS/tags/modifiers themselves).
    private static readonly FakeFace Emoji = new("Emoji",
        FakeFace.Scalars("\U0001F468\U0001F469\U0001F467\U0001F1FA\U0001F1F8\U0001F44D❤☺⌚1#*\u20E3\U0001F3F4\U0001F600"),
        color: true);

    private static readonly FakeFace Cjk = new("CJK", FakeFace.Scalars("世界한"));
    private static readonly FakeFace Icons = new("Icons", FakeFace.Range(0xE000, 0xF8FF));

    [Theory]
    [InlineData("A", ClusterPresentation.Default)]
    [InlineData("❤", ClusterPresentation.Default)]           // text-default base
    [InlineData("❤\uFE0F", ClusterPresentation.Emoji)]       // VS16
    [InlineData("⌚", ClusterPresentation.Emoji)]             // Emoji_Presentation base
    [InlineData("⌚\uFE0E", ClusterPresentation.Text)]        // VS15 overrides default emoji
    [InlineData("☺\uFE0E", ClusterPresentation.Text)]
    [InlineData(Family, ClusterPresentation.Emoji)]         // ZWJ sequence with pictographs
    [InlineData(Flag, ClusterPresentation.Emoji)]           // RI pair
    [InlineData("\U0001F1FA", ClusterPresentation.Default)] // lone RI
    [InlineData(ThumbsSkin, ClusterPresentation.Emoji)]     // modifier
    [InlineData(Keycap, ClusterPresentation.Emoji)]
    [InlineData("1\u20E3", ClusterPresentation.Emoji)]      // keycap without VS16
    [InlineData("A\u20E3", ClusterPresentation.Default)]    // enclosing keycap on a non-keycap base
    [InlineData(Scotland, ClusterPresentation.Emoji)]       // tag sequence
    [InlineData("\u0915\u094D\u200D\u0937", ClusterPresentation.Default)] // ZWJ without pictograph
    [InlineData("世", ClusterPresentation.Default)]          // wide, but not emoji
    [InlineData("\uE0B0", ClusterPresentation.Default)]     // PUA
    [InlineData("\u2329", ClusterPresentation.Default)]     // wide angle bracket is not emoji
    [InlineData("e\u0301", ClusterPresentation.Default)]
    public void PresentationTable(string cluster, ClusterPresentation expected) =>
        Assert.Equal(expected, ClusterFontSelector.GetPresentation(cluster));

    // Candidate order: [symbol-map faces..] Text, Cjk, Emoji, Icons. Expected = family name or "none".
    [Theory]
    [InlineData("A", 0, "Mono")]
    [InlineData(Family, 0, "Emoji")]          // ZWJ itself is not required in any cmap
    [InlineData(Flag, 0, "Emoji")]
    [InlineData(ThumbsSkin, 0, "Emoji")]      // skin-tone modifier not required
    [InlineData(Keycap, 0, "Emoji")]          // emoji preferred although Mono has 1 and U+20E3
    [InlineData("1\u20E3", 0, "Emoji")]
    [InlineData("1", 0, "Mono")]
    [InlineData("❤", 0, "Mono")]              // text default: chain order
    [InlineData("❤\uFE0F", 0, "Emoji")]       // VS16 prefers colour
    [InlineData("⌚", 0, "Emoji")]             // default emoji presentation
    [InlineData("⌚\uFE0E", 0, "Mono")]        // VS15 prefers monochrome
    [InlineData("☺\uFE0E", 0, "Mono")]
    [InlineData(Scotland, 0, "Emoji")]        // tags not required
    [InlineData("世", 0, "CJK")]
    [InlineData("\uE0B0", 0, "Icons")]
    [InlineData("e\u0301", 0, "Mono")]        // combining mark is required and covered
    [InlineData("\u0915\u094D\u200D\u0937", 0, "Mono")]
    [InlineData("\U0001F99C", 0, "none")]     // nothing covers it
    [InlineData("\u200D", 0, "none")]         // only ignorable scalars
    [InlineData("A", 1, "SymbolA")]           // symbol-map face wins when it covers
    [InlineData("\U0001F600", 1, "Emoji")]    // symbol-map face lacks it: normal rules
    public void SelectionTable(string cluster, int symbolFaces, string expected)
    {
        var candidates = new List<IGlyphCoverage>();
        if (symbolFaces > 0)
            candidates.Add(new FakeFace("SymbolA", FakeFace.Range(0x41, 0x5A)));
        candidates.AddRange(new IGlyphCoverage[] { Text, Cjk, Emoji, Icons });

        int index = ClusterFontSelector.Select(cluster, candidates, symbolFaces);
        Assert.Equal(expected, index < 0 ? "none" : ((FakeFace)candidates[index]).FamilyName);
    }

    [Fact]
    public void EmojiPreferenceFallsBackToChainOrderWhenNoColourFaceCovers()
    {
        var candidates = new IGlyphCoverage[] { Text, Cjk };
        Assert.Equal(0, ClusterFontSelector.Select("❤\uFE0F", candidates));
    }

    [Fact]
    public void TextPreferenceFallsBackToColourFaceWhenNoMonochromeFaceCovers()
    {
        var candidates = new IGlyphCoverage[] { Cjk, Emoji };
        Assert.Equal(1, ClusterFontSelector.Select("\U0001F600\uFE0E", candidates));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\uFE0F\u200D", 0)]
    [InlineData("\u200DA", 0x41)]
    [InlineData(Keycap, '1')]
    [InlineData(ThumbsSkin, 0x1F44D)]
    public void CoverageBaseSkipsIgnorables(string cluster, int expected) =>
        Assert.Equal(expected, ClusterFontSelector.GetCoverageBase(cluster));

    [Theory]
    [InlineData(0xFE0E, true)]
    [InlineData(0xFE0F, true)]
    [InlineData(0x200D, true)]
    [InlineData(0x200C, true)]
    [InlineData(0x1F3FB, true)]
    [InlineData(0x1F3FF, true)]
    [InlineData(0xE0020, true)]
    [InlineData(0xE007F, true)]
    [InlineData(0xE0100, true)]
    [InlineData(0x20E3, false)]
    [InlineData(0x0301, false)]
    [InlineData(0x1F3FA, false)]
    [InlineData(0x1F400, false)]
    public void IgnorableScalars(int codepoint, bool expected) =>
        Assert.Equal(expected, ClusterFontSelector.IsIgnorableForCoverage(codepoint));

    [Fact]
    public void EmptyInputsSelectNothing()
    {
        Assert.Equal(-1, ClusterFontSelector.Select("A", new IGlyphCoverage[0]));
        Assert.Equal(-1, ClusterFontSelector.Select("", new IGlyphCoverage[] { Text }));
    }

    [Fact]
    public void SymbolMapCountIsClampedToTheCandidateCount()
    {
        Assert.Equal(0, ClusterFontSelector.Select("A", new IGlyphCoverage[] { Text }, symbolMapCount: 5));
        Assert.Equal(0, ClusterFontSelector.Select("A", new IGlyphCoverage[] { Text }, symbolMapCount: -1));
    }
}
