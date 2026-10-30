using System;
using System.Runtime.CompilerServices;
using Dotty.Abstractions.Config;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Service for resolving font typefaces from family lists and measuring cell grid dimensions.
/// </summary>
public static class FontMetricsService
{
    /// <summary>
    /// Entry bound of the shared metrics cache. Each distinct face, size, line height and scale
    /// takes one entry; zoom steps and monitor moves stay far below this.
    /// </summary>
    internal const int MetricsCacheCapacity = 32;

    private static readonly object MetricsLock = new();
    private static readonly CellMetricsCache SharedMetricsCache = new(MetricsCacheCapacity);
    private static readonly ConditionalWeakTable<SKTypeface, TypefaceIdentity> Identities = new();

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
    /// Measures character cell width and height in logical (window-coordinate) units, accounting
    /// for font metrics, line height, and DPI scale; the device cell is the result times
    /// <paramref name="scale"/>. Results are cached by value (typeface family and style, size,
    /// line height, scale) in a bounded least-recently-used cache, so equal inputs never re-measure
    /// and the cache never grows past <see cref="MetricsCacheCapacity"/> entries.
    /// </summary>
    public static (float CellWidth, float CellHeight) MeasureCell(SKTypeface typeface, float fontSize, double lineHeight, float scale)
    {
        lock (MetricsLock)
        {
            return MeasureCell(SharedMetricsCache, typeface, fontSize, lineHeight, scale);
        }
    }

    /// <summary>
    /// <see cref="MeasureCell(SKTypeface, float, double, float)"/> against an explicit cache.
    /// The caller synchronizes access to <paramref name="cache"/>.
    /// </summary>
    internal static (float CellWidth, float CellHeight) MeasureCell(
        CellMetricsCache cache, SKTypeface typeface, float fontSize, double lineHeight, float scale)
    {
        ArgumentNullException.ThrowIfNull(cache);
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

        var key = new CellMetricsKey(GetIdentity(typeface), fontSize, lineHeight, scale);
        if (cache.TryGet(key, out CellMetrics cached))
            return (cached.Width, cached.Height);

        CellMetrics measured = MeasureCellCore(typeface, fontSize, lineHeight, scale);
        cache.Set(key, measured);
        return (measured.Width, measured.Height);
    }

    /// <summary>
    /// Value identity of <paramref name="typeface"/>, read from Skia once per wrapper object
    /// (the family name read allocates) and remembered for the wrapper's lifetime.
    /// </summary>
    internal static TypefaceIdentity GetIdentity(SKTypeface typeface) =>
        Identities.GetValue(
            typeface,
            static face => new TypefaceIdentity(face.FamilyName ?? string.Empty, face.FontWeight, face.FontWidth, face.FontSlant));

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
