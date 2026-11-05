using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Rendering.Gpu;

/// <summary>How a grapheme cluster asks to be drawn.</summary>
public enum ClusterPresentation
{
    /// <summary>No preference: the first face in chain order that covers the cluster.</summary>
    Default,
    /// <summary>Text style (VS15): prefer a monochrome face.</summary>
    Text,
    /// <summary>Emoji style: prefer a colour face.</summary>
    Emoji,
}

/// <summary>
/// Pure face selection for one grapheme cluster over an ordered candidate list (symbol-map faces first, then
/// the fallback chain). Rules:
/// <list type="number">
/// <item>Coverage is tested on the base and every non-ignorable scalar; variation selectors, ZWJ/ZWNJ, emoji
/// modifiers (skin tones) and tag characters are not required to be in a face's character map, because a face
/// renders them through its sequences rather than as glyphs of their own.</item>
/// <item>Symbol-map candidates win whenever they cover the cluster.</item>
/// <item>Emoji presentation (VS16, a default-emoji base, a regional-indicator pair, a keycap, a tag sequence, a
/// skin-tone modifier, or a ZWJ sequence that contains a pictograph) prefers the first covering colour face;
/// VS15 prefers the first covering monochrome face; otherwise, and when no preferred face covers, the first
/// covering face in order wins.</item>
/// </list>
/// </summary>
public static class ClusterFontSelector
{
    private const int Zwnj = 0x200C, Zwj = 0x200D, Vs15 = 0xFE0E, Vs16 = 0xFE0F, Keycap = 0x20E3;

    /// <summary>Index of the selected candidate, or -1 when no candidate covers the cluster.</summary>
    public static int Select(ReadOnlySpan<char> cluster, IReadOnlyList<IGlyphCoverage> candidates, int symbolMapCount = 0)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (cluster.IsEmpty || candidates.Count == 0)
            return -1;
        symbolMapCount = Math.Clamp(symbolMapCount, 0, candidates.Count);
        for (int i = 0; i < symbolMapCount; i++)
            if (Covers(candidates[i], cluster))
                return i;

        var presentation = GetPresentation(cluster);
        if (presentation != ClusterPresentation.Default)
        {
            bool wantColor = presentation == ClusterPresentation.Emoji;
            for (int i = symbolMapCount; i < candidates.Count; i++)
                if (candidates[i].IsColor == wantColor && Covers(candidates[i], cluster))
                    return i;
        }
        for (int i = symbolMapCount; i < candidates.Count; i++)
            if (Covers(candidates[i], cluster))
                return i;
        return -1;
    }

    public static ClusterPresentation GetPresentation(ReadOnlySpan<char> cluster)
    {
        int baseCp = -1, regionalIndicators = 0;
        bool vs15 = false, vs16 = false, zwj = false, keycap = false, tag = false, modifier = false, pictograph = false;
        foreach (Rune rune in cluster.EnumerateRunes())
        {
            int cp = rune.Value;
            if (baseCp < 0) baseCp = cp;
            switch (cp)
            {
                case Vs15: vs15 = true; break;
                case Vs16: vs16 = true; break;
                case Zwj: zwj = true; break;
                case Keycap: keycap = true; break;
                default:
                    if (IsTag(cp)) tag = true;
                    else if (IsEmojiModifier(cp)) modifier = true;
                    else if (IsRegionalIndicator(cp)) regionalIndicators++;
                    if (UnicodeWidth.IsDefaultEmojiPresentation(cp)) pictograph = true;
                    break;
            }
        }
        if (baseCp < 0)
            return ClusterPresentation.Default;
        if (vs16 || modifier || tag || regionalIndicators >= 2 || (keycap && IsKeycapBase(baseCp)))
            return ClusterPresentation.Emoji;
        if (zwj && pictograph)
            return ClusterPresentation.Emoji;
        if (vs15)
            return ClusterPresentation.Text;
        return UnicodeWidth.IsDefaultEmojiPresentation(baseCp) ? ClusterPresentation.Emoji : ClusterPresentation.Default;
    }

    /// <summary>Whether <paramref name="face"/> has every scalar of the cluster that must come from its character map.</summary>
    public static bool Covers(IGlyphCoverage face, ReadOnlySpan<char> cluster)
    {
        ArgumentNullException.ThrowIfNull(face);
        bool any = false;
        foreach (Rune rune in cluster.EnumerateRunes())
        {
            int cp = rune.Value;
            if (IsIgnorableForCoverage(cp))
                continue;
            any = true;
            if (!face.HasGlyph(cp))
                return false;
        }
        return any;
    }

    /// <summary>First scalar the cluster needs from a face (its base), or 0 for an empty or all-ignorable cluster.</summary>
    public static int GetCoverageBase(ReadOnlySpan<char> cluster)
    {
        foreach (Rune rune in cluster.EnumerateRunes())
            if (!IsIgnorableForCoverage(rune.Value))
                return rune.Value;
        return 0;
    }

    public static bool IsIgnorableForCoverage(int cp) =>
        cp is Zwnj or Zwj
        || (cp >= 0xFE00 && cp <= 0xFE0F)      // variation selectors
        || (cp >= 0xE0100 && cp <= 0xE01EF)    // variation selectors supplement
        || IsTag(cp)
        || IsEmojiModifier(cp);

    private static bool IsTag(int cp) => cp >= 0xE0020 && cp <= 0xE007F;

    private static bool IsEmojiModifier(int cp) => cp >= 0x1F3FB && cp <= 0x1F3FF;

    private static bool IsRegionalIndicator(int cp) => cp >= 0x1F1E6 && cp <= 0x1F1FF;

    private static bool IsKeycapBase(int cp) => cp is '#' or '*' || (cp >= '0' && cp <= '9');
}
