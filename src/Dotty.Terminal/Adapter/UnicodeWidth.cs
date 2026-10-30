namespace Dotty.Terminal.Adapter;

/// <summary>
/// Terminal cell width of grapheme clusters, from tables generated out of the Unicode Character Database
/// (<see cref="UnicodeWidthTables"/>, written by <c>tools/gen-width-tables.py</c>). Segmentation into clusters is
/// .NET's <see cref="System.Globalization.StringInfo"/> (UAX #29); this class only measures a cluster.
/// </summary>
/// <remarks>
/// <para>Scalar widths: 0 for General_Category Cc, Cf, Mn and Me (except U+00AD SOFT HYPHEN, which is 1) and for
/// Hangul medial/final jamo U+1160..U+11FF, U+D7B0..U+D7FF; 2 for East_Asian_Width W or F (which covers every
/// Emoji_Presentation character); 1 otherwise, including East Asian ambiguous width.</para>
/// <para>A cluster takes the width of its first scalar that is not zero-width, so a Prepend format character such
/// as U+0600 defers to the base after it and Hangul L+V+T takes the leading consonant's width. The scalar right after
/// that base then adjusts it: a regional-indicator pair is 2 (a lone indicator is 1); U+FE0F widens exactly the
/// emoji-variation bases to 2 and U+FE0E narrows exactly those bases to 1, leaving any other base unchanged. ZWJ,
/// skin-tone modifiers and other extenders never widen a cluster. A cluster of zero-width scalars only is 0.</para>
/// </remarks>
public static class UnicodeWidth
{
    private const uint VariationSelector15 = 0xFE0E;
    private const uint VariationSelector16 = 0xFE0F;
    private const uint RegionalIndicatorFirst = 0x1F1E6;
    private const uint RegionalIndicatorLast = 0x1F1FF;

    public static int GetWidth(string grapheme) => GetWidth(grapheme.AsSpan());

    public static int GetWidth(ReadOnlySpan<char> grapheme)
    {
        var runes = grapheme.EnumerateRunes();
        uint baseCodePoint = 0;
        int width = 0;
        while (runes.MoveNext())
        {
            baseCodePoint = (uint)runes.Current.Value;
            width = GetScalarWidth(baseCodePoint);
            if (width != 0) break;
        }

        if (width == 0 || !runes.MoveNext()) return width;

        uint next = (uint)runes.Current.Value;
        if (IsRegionalIndicator(baseCodePoint)) return IsRegionalIndicator(next) ? 2 : width;
        if (next == VariationSelector16) return IsEmojiVariationBase(baseCodePoint) ? 2 : width;
        if (next == VariationSelector15) return IsEmojiVariationBase(baseCodePoint) ? 1 : width;
        return width;
    }

    /// <summary>Width of one scalar value: 0, 1 or 2 (see the class remarks).</summary>
    internal static int GetScalarWidth(uint codePoint)
    {
        // Below U+0300 the only zero-width scalars are the C0/C1 controls (U+00AD is 1) and nothing is wide.
        if (codePoint < 0x300) return codePoint < 0x20 || (codePoint >= 0x7F && codePoint < 0xA0) ? 0 : 1;
        if (InRanges(UnicodeWidthTables.ZeroWidth, codePoint)) return 0;
        return InRanges(UnicodeWidthTables.Wide, codePoint) ? 2 : 1;
    }

    /// <summary>Whether <paramref name="codePoint"/> is the base of an emoji-style variation sequence (X U+FE0F).</summary>
    internal static bool IsEmojiVariationBase(uint codePoint) =>
        InRanges(UnicodeWidthTables.EmojiVariationBases, codePoint);

    private static bool IsRegionalIndicator(uint codePoint) =>
        codePoint - RegionalIndicatorFirst <= RegionalIndicatorLast - RegionalIndicatorFirst;

    /// <summary>Binary search over sorted, non-overlapping inclusive [start, end] pairs.</summary>
    private static bool InRanges(ReadOnlySpan<uint> ranges, uint codePoint)
    {
        if (codePoint < ranges[0] || codePoint > ranges[^1]) return false;
        int lo = 0;
        int hi = (ranges.Length >> 1) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (codePoint < ranges[mid << 1]) hi = mid - 1;
            else if (codePoint > ranges[(mid << 1) + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
