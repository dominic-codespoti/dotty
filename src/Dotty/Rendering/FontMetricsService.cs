using System;
using Dotty.Abstractions.Config;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Service for resolving font typefaces from family lists and measuring cell grid dimensions.
/// </summary>
public static class FontMetricsService
{
    /// <summary>
    /// Matches the first available font family from a comma-separated list, falling back to <see cref="SKTypeface.Default"/>.
    /// </summary>
    public static SKTypeface ResolveTypeface(string? familyList)
    {
        if (string.IsNullOrWhiteSpace(familyList))
        {
            familyList = DottyDefaults.FontFamily;
        }

        foreach (var name in familyList.Split(','))
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            var matched = SKFontManager.Default.MatchFamily(trimmed);
            if (matched != null)
            {
                return matched;
            }
        }

        return SKTypeface.Default;
    }

    /// <summary>
    /// Measures character cell width and height in device pixels, accounting for font metrics, line height, and DPI scale.
    /// </summary>
    public static (float CellWidth, float CellHeight) MeasureCell(SKTypeface typeface, float fontSize, double lineHeight, float scale)
    {
        ArgumentNullException.ThrowIfNull(typeface);

        fontSize = float.IsFinite(fontSize)
            ? Math.Clamp(fontSize, 1f, 512f)
            : (float)DottyDefaults.FontSize;
        lineHeight = double.IsFinite(lineHeight)
            ? Math.Clamp(lineHeight, 0.1, 8.0)
            : 1.0;
        scale = float.IsFinite(scale)
            ? Math.Clamp(scale, 0.1f, 16f)
            : 1.0f;

        float scaledFontSize = fontSize * scale;
        using var font = new SKFont(typeface, scaledFontSize)
        {
            Subpixel = true,
            Hinting = SKFontHinting.Full,
            Edging = SKFontEdging.SubpixelAntialias,
        };

        var fm = font.Metrics;
        float ascent = float.IsFinite(fm.Ascent) ? MathF.Abs(fm.Ascent) : scaledFontSize;
        float descent = float.IsFinite(fm.Descent) ? MathF.Abs(fm.Descent) : 0f;
        float glyphHeight = MathF.Max(scaledFontSize, ascent + descent);
        float glyphAdvance = float.IsFinite(fm.AverageCharacterWidth)
            ? MathF.Max(0.5f, fm.AverageCharacterWidth)
            : scaledFontSize * 0.6f;
        float wideGlyphAdvance = font.MeasureText("W");
        if (float.IsFinite(wideGlyphAdvance))
            glyphAdvance = MathF.Max(glyphAdvance, wideGlyphAdvance);

        float cellWidth = MathF.Round(MathF.Max(4, glyphAdvance / scale));
        float cellHeight = MathF.Round(MathF.Max(fontSize * (float)lineHeight, glyphHeight / scale));
        return (cellWidth, cellHeight);
    }
}
