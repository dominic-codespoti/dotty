using System;
using System.Runtime.CompilerServices;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
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
    /// Measures the cell for <paramref name="typeface"/> in whole device pixels: size, baseline and
    /// decoration strokes (see <see cref="CellMetricsCalculator"/>). This is the thin Skia adapter: it
    /// fills a <see cref="FontMetricsInput"/> from <see cref="SKFontMetrics"/> at the device font size.
    /// Results are cached by value (typeface family and style plus the normalized options) in a bounded
    /// least-recently-used cache, so equal inputs never re-measure and the cache never grows past
    /// <see cref="MetricsCacheCapacity"/> entries.
    /// </summary>
    internal static CellMetrics MeasureCell(SKTypeface typeface, in CellMetricsOptions options)
    {
        lock (MetricsLock)
        {
            return MeasureCell(SharedMetricsCache, typeface, options);
        }
    }

    /// <summary>
    /// <see cref="MeasureCell(SKTypeface, in CellMetricsOptions)"/> against an explicit cache.
    /// The caller synchronizes access to <paramref name="cache"/>.
    /// </summary>
    internal static CellMetrics MeasureCell(CellMetricsCache cache, SKTypeface typeface, in CellMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(typeface);

        var normalized = Normalize(options);
        var key = new CellMetricsKey(GetIdentity(typeface), normalized);
        if (cache.TryGet(key, out CellMetrics cached))
            return cached;

        CellMetrics measured = CellMetricsCalculator.Compute(ReadFontMetrics(typeface, normalized.FontSizePx, normalized.Raster), normalized);
        cache.Set(key, measured);
        return measured;
    }

    /// <summary>Clamps non-finite or out-of-range options so equal effective inputs share one cache entry.</summary>
    internal static CellMetricsOptions Normalize(in CellMetricsOptions options)
    {
        float scale = float.IsFinite(options.ContentScale) && options.ContentScale > 0f
            ? Math.Clamp(options.ContentScale, DisplayScale.MinScale, DisplayScale.MaxScale)
            : 1f;
        float fontSize = float.IsFinite(options.FontSizePx)
            ? Math.Clamp(options.FontSizePx, 1f, 4096f)
            : CellMetricsCalculator.PointsToPixels(DottyDefaults.FontSize, scale);
        double lineHeight = double.IsFinite(options.LineHeight)
            ? Math.Clamp(options.LineHeight, 0.1, 8.0)
            : 1.0;
        return options with { FontSizePx = fontSize, LineHeight = lineHeight, ContentScale = scale };
    }

    /// <summary>
    /// Value identity of <paramref name="typeface"/>, read from Skia once per wrapper object
    /// (the family name read allocates) and remembered for the wrapper's lifetime.
    /// </summary>
    internal static TypefaceIdentity GetIdentity(SKTypeface typeface) =>
        Identities.GetValue(
            typeface,
            static face => new TypefaceIdentity(face.FamilyName ?? string.Empty, face.FontWeight, face.FontWidth, face.FontSlant));

    private static FontMetricsInput ReadFontMetrics(SKTypeface typeface, float fontSizePx, RasterPolicy raster)
    {
        // The same policy object configures the glyph atlas fonts (GlyphAtlas), so metrics and raster agree.
        using var font = new SKFont(typeface, fontSizePx);
        raster.ApplyTo(font);

        var fm = font.Metrics;
        float advance = float.IsFinite(fm.AverageCharacterWidth) ? MathF.Max(0.5f, fm.AverageCharacterWidth) : float.NaN;
        float wideGlyphAdvance = font.MeasureText("W");
        if (float.IsFinite(wideGlyphAdvance))
            advance = float.IsFinite(advance) ? MathF.Max(advance, wideGlyphAdvance) : wideGlyphAdvance;

        return new FontMetricsInput(
            Ascent: fm.Ascent,
            Descent: fm.Descent,
            Advance: advance,
            UnderlinePosition: fm.UnderlinePosition ?? float.NaN,
            UnderlineThickness: fm.UnderlineThickness ?? float.NaN,
            StrikeoutPosition: fm.StrikeoutPosition ?? float.NaN,
            StrikeoutThickness: fm.StrikeoutThickness ?? float.NaN,
            XHeight: fm.XHeight);
    }
}
