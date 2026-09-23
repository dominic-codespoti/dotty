using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dotty.Abstractions.Config;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Service for resolving font typefaces from family lists and measuring cell grid dimensions.
/// </summary>
public static class FontMetricsService
{
    private readonly struct MeasureKey : IEquatable<MeasureKey>
    {
        private readonly SKTypeface _typeface;
        private readonly float _fontSize;
        private readonly double _lineHeight;
        private readonly float _scale;

        public MeasureKey(SKTypeface typeface, float fontSize, double lineHeight, float scale)
        {
            _typeface = typeface;
            _fontSize = fontSize;
            _lineHeight = lineHeight;
            _scale = scale;
        }

        public bool Equals(MeasureKey other) =>
            ReferenceEquals(_typeface, other._typeface)
            && _fontSize.Equals(other._fontSize)
            && _lineHeight.Equals(other._lineHeight)
            && _scale.Equals(other._scale);

        public override bool Equals(object? obj) => obj is MeasureKey other && Equals(other);
        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(_typeface), _fontSize, _lineHeight, _scale);
    }

    private readonly record struct CellMetrics(float Width, float Height);
    private static readonly object MetricsLock = new();
    private static readonly Dictionary<MeasureKey, CellMetrics> MetricsCache = new();
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

        var key = new MeasureKey(typeface, fontSize, lineHeight, scale);
        lock (MetricsLock)
        {
            if (MetricsCache.TryGetValue(key, out CellMetrics cached))
                return (cached.Width, cached.Height);

            CellMetrics measured = MeasureCellCore(typeface, fontSize, lineHeight, scale);
            MetricsCache.Add(key, measured);
            return (measured.Width, measured.Height);
        }
    }

    private static CellMetrics MeasureCellCore(SKTypeface typeface, float fontSize, double lineHeight, float scale)
    {
        float scaledFontSize = fontSize * scale;
        using var font = new SKFont(typeface, scaledFontSize)
        {
            // Keep cell metrics identical to the A8 glyph atlas raster policy.
            // Subpixel coverage requires RGB channels and cannot be represented
            // by the single-channel atlas.
            Subpixel = false,
            Hinting = SKFontHinting.Full,
            Edging = SKFontEdging.Antialias,
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
        return new CellMetrics(cellWidth, cellHeight);
    }
}
