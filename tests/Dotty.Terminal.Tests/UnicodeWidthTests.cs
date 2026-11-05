using System.Text;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Cell width of scalars (generated tables, checked against the generator's per-code-point golden) and of clusters
/// (the rules in <see cref="UnicodeWidth"/>).
/// </summary>
public sealed class UnicodeWidthTests
{
    [Fact]
    public void ScalarWidths_MatchTheGeneratedGolden()
    {
        int[] pairs = UnicodeWidthGolden.Pairs;
        Assert.True(pairs.Length > 1000);
        Assert.Equal(0, pairs.Length % 2);
        var mismatches = new StringBuilder();
        for (int i = 0; i < pairs.Length; i += 2)
        {
            uint codePoint = (uint)pairs[i];
            int expected = pairs[i + 1];
            int scalar = UnicodeWidth.GetScalarWidth(codePoint);
            int cluster = UnicodeWidth.GetWidth(new Rune(codePoint).ToString());
            if (scalar != expected || cluster != expected)
                mismatches.Append($"U+{codePoint:X4}: scalar {scalar}, cluster {cluster}, expected {expected}\n");
        }
        Assert.True(mismatches.Length == 0, mismatches.ToString());
    }

    [Fact]
    public void GeneratedTables_AreSortedDisjointInclusivePairs()
    {
        Assert.Equal("16.0.0", UnicodeWidthTables.UnicodeVersion);
        AssertSortedPairs(UnicodeWidthTables.ZeroWidth);
        AssertSortedPairs(UnicodeWidthTables.Wide);
        AssertSortedPairs(UnicodeWidthTables.EmojiVariationBases);

        int bases = 0;
        ReadOnlySpan<uint> ranges = UnicodeWidthTables.EmojiVariationBases;
        for (int i = 0; i < ranges.Length; i += 2) bases += (int)(ranges[i + 1] - ranges[i] + 1);
        Assert.Equal(371, bases);

        ReadOnlySpan<uint> zero = UnicodeWidthTables.ZeroWidth;
        ReadOnlySpan<uint> wide = UnicodeWidthTables.Wide;
        for (int z = 0; z < zero.Length; z += 2)
        {
            for (int w = 0; w < wide.Length; w += 2)
                Assert.False(zero[z] <= wide[w + 1] && wide[w] <= zero[z + 1], $"zero range U+{zero[z]:X4} overlaps wide U+{wide[w]:X4}");
        }
    }

    private static void AssertSortedPairs(ReadOnlySpan<uint> ranges)
    {
        Assert.True(ranges.Length >= 2);
        Assert.Equal(0, ranges.Length % 2);
        for (int i = 0; i < ranges.Length; i += 2)
        {
            Assert.True(ranges[i] <= ranges[i + 1], $"range U+{ranges[i]:X4} ends before it starts");
            if (i + 2 < ranges.Length)
                Assert.True(ranges[i + 1] < ranges[i + 2], $"range U+{ranges[i]:X4} overlaps the next one");
        }
    }

    public static TheoryData<string, string, int> Clusters => new()
    {
        { "empty", "", 0 },
        { "ASCII", "a", 1 },
        { "C0 control", "\u0007", 0 },
        { "C1 control", "\u0085", 0 },
        { "soft hyphen stays narrow", "\u00AD", 1 },
        { "zero width space", "\u200B", 0 },
        { "word joiner", "\u2060", 0 },
        { "byte order mark", "\uFEFF", 0 },
        { "left-to-right mark", "\u200E", 0 },
        { "lone combining mark", "\u0301", 0 },
        { "base + combining marks", "e\u0301\u0302", 1 },
        { "lone enclosing keycap", "\u20E3", 0 },
        { "spacing mark alone is narrow", "\u093F", 1 },
        { "Devanagari base + spacing mark", "\u0915\u093F", 1 },
        { "Indic ZWJ does not widen", "\u0915\u094D\u200D\u0937", 1 },
        { "lone ZWJ", "\u200D", 0 },
        { "CJK ideograph", "\u4E2D", 2 },
        { "fullwidth form", "\uFF21", 2 },
        { "halfwidth katakana", "\uFF71", 1 },
        { "ambiguous width is narrow", "\u00B1", 1 },
        { "Hangul syllable", "\uAC00", 2 },
        { "Hangul L+V+T", "\u1100\u1161\u11A8", 2 },
        { "Hangul LV+T", "\uAC00\u11A8", 2 },
        { "lone Hangul medial vowel", "\u1160", 0 },
        { "lone Hangul final consonant", "\u11A8", 0 },
        { "lone Hangul extended-B jamo", "\uD7B0", 0 },
        { "lone regional indicator", "\U0001F1FA", 1 },
        { "regional indicator pair", "\U0001F1FA\U0001F1F8", 2 },
        { "wide emoji (Emoji_Presentation)", "\U0001F600", 2 },
        { "large orange circle (missed by the old table)", "\U0001F7E0", 2 },
        { "watch, BMP emoji", "\u231A", 2 },
        { "legacy computing sextant is narrow", "\U0001FB00", 1 },
        { "heavy heart text default", "\u2764", 1 },
        { "heavy heart + VS16", "\u2764\uFE0F", 2 },
        { "smiley + VS15", "\u263A\uFE0E", 1 },
        { "wide variation base + VS15 narrows", "\u231A\uFE0E", 1 },
        { "VS16 on a non-base does not widen", "a\uFE0F", 1 },
        { "VS16 on an accented letter does not widen", "\u00E9\uFE0F", 1 },
        { "VS15 on a non-base does not narrow", "\u4E2D\uFE0E", 2 },
        { "VS16 not directly after the base", "\u2764\u0301\uFE0F", 1 },
        { "keycap with VS16", "1\uFE0F\u20E3", 2 },
        { "keycap without VS16", "1\u20E3", 1 },
        { "number sign keycap", "#\uFE0F\u20E3", 2 },
        { "ZWJ family", "\U0001F468\u200D\U0001F469\u200D\U0001F467", 2 },
        { "rainbow flag", "\U0001F3F3\uFE0F\u200D\U0001F308", 2 },
        { "heart on fire", "\u2764\uFE0F\u200D\U0001F525", 2 },
        { "ZWJ after a narrow base does not widen", "\u2764\u200D\U0001F525", 1 },
        { "skin tone on a wide base", "\U0001F44D\U0001F3FD", 2 },
        { "skin tone does not widen a narrow base", "\u261D\U0001F3FD", 1 },
        { "lone skin tone modifier", "\U0001F3FD", 2 },
        { "tag sequence flag", "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", 2 },
        { "prepend defers to its base", "\u0600\u0661", 1 },
        { "prepend before a wide base", "\u0600\u4E2D", 2 },
        { "lone prepend format character", "\u0600", 0 },
        { "placeholder scalar", "\U0010EEEE", 1 },
        { "lone surrogate measures as U+FFFD", "\uD800", 1 },
    };

    [Theory]
    [MemberData(nameof(Clusters))]
    public void ClusterWidth(string name, string cluster, int expected)
    {
        Assert.True(UnicodeWidth.GetWidth(cluster) == expected, $"{name}: width {UnicodeWidth.GetWidth(cluster)}, expected {expected}");
        Assert.Equal(expected, UnicodeWidth.GetWidth(cluster.AsSpan()));
    }

    [Fact]
    public void EmojiVariationBases_AreExactlyTheListedOnes()
    {
        Assert.True(UnicodeWidth.IsEmojiVariationBase('#'));
        Assert.True(UnicodeWidth.IsEmojiVariationBase('0'));
        Assert.True(UnicodeWidth.IsEmojiVariationBase(0x2764));
        Assert.True(UnicodeWidth.IsEmojiVariationBase(0x1F3F3));
        Assert.False(UnicodeWidth.IsEmojiVariationBase('a'));
        Assert.False(UnicodeWidth.IsEmojiVariationBase(0x4E2D));
        Assert.False(UnicodeWidth.IsEmojiVariationBase(0x1F600)); // emoji, but has no variation sequence
    }
}
