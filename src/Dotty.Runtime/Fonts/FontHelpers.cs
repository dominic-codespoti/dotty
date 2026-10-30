using System;

namespace Dotty.Runtime.Fonts;

/// <summary>
/// Small helper functions around font/glyph heuristics that are safe to unit-test.
/// Kept free of host and rendering dependencies so it can be unit-tested directly.
/// </summary>
public static class FontHelpers
{
    private static readonly string[] SymbolFontNameHints =
    {
            "SYMBOL",
            "MATERIAL SYMBOL",
            "ICON",
            "EMOJI",
            "WINGDINGS",
            "WEBDINGS",
            "FONT AWESOME",
            "SEGOE MDL2"
        };

    /// <summary>
    /// Returns true when a font family name likely represents an icon/symbol font.
    /// </summary>
    public static bool IsLikelySymbolFontName(string? fontFamilyName)
    {
        if (string.IsNullOrWhiteSpace(fontFamilyName))
        {
            return false;
        }

        var normalized = fontFamilyName.ToUpperInvariant();
        for (int i = 0; i < SymbolFontNameHints.Length; i++)
        {
            if (normalized.Contains(SymbolFontNameHints[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
