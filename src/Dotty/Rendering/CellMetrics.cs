using System;
using System.Globalization;
using Dotty.Rendering.Gpu;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Font facts in device pixels, read from <c>SKFontMetrics</c> at the device font size. Distances
/// are positive magnitudes; optional table values are NaN when the font does not provide them.
/// </summary>
/// <param name="Ascent">Distance from the baseline up to the top of the font box.</param>
/// <param name="Descent">Distance from the baseline down to the bottom of the font box.</param>
/// <param name="Advance">Widest of the average advance and the advance of <c>W</c>.</param>
/// <param name="UnderlinePosition">Top of the underline stroke below the baseline (Skia sign: positive is down); NaN if absent.</param>
/// <param name="UnderlineThickness">Underline stroke thickness; NaN or ≤ 0 if absent.</param>
/// <param name="StrikeoutPosition">Bottom of the strikeout stroke relative to the baseline (Skia sign: negative is up); NaN if absent.</param>
/// <param name="StrikeoutThickness">Strikeout stroke thickness; NaN or ≤ 0 if absent.</param>
/// <param name="XHeight">Height of lowercase x above the baseline; NaN or ≤ 0 if absent.</param>
internal readonly record struct FontMetricsInput(
    float Ascent,
    float Descent,
    float Advance,
    float UnderlinePosition = float.NaN,
    float UnderlineThickness = float.NaN,
    float StrikeoutPosition = float.NaN,
    float StrikeoutThickness = float.NaN,
    float XHeight = float.NaN);

/// <summary>
/// A cell-size adjustment (<c>font.adjustCellWidth</c>/<c>font.adjustCellHeight</c>): a signed
/// number of logical pixels (<c>"2"</c>, <c>"-1"</c>, scaled by the content scale) or a signed
/// percentage of the natural size (<c>"10%"</c>). The empty string and <c>"0"</c> leave the cell as measured.
/// </summary>
internal readonly record struct CellAdjustment(double Value, bool IsPercent)
{
    public static CellAdjustment None => default;

    /// <summary>Largest accepted magnitude: ±100 logical px or ±100 %.</summary>
    public const double MaxMagnitude = 100d;

    /// <summary>Parses a config value; false (and <see cref="None"/>) for anything malformed or non-finite.</summary>
    public static bool TryParse(string? text, out CellAdjustment adjustment)
    {
        adjustment = None;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        bool percent = span.EndsWith("%", StringComparison.Ordinal);
        if (percent)
            span = span[..^1].TrimEnd();
        if (!double.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value)
            || Math.Abs(value) > MaxMagnitude)
        {
            return false;
        }

        adjustment = new CellAdjustment(value, percent);
        return true;
    }

    /// <summary>Parses a config value, treating malformed input as no adjustment.</summary>
    public static CellAdjustment Parse(string? text) => TryParse(text, out var adjustment) ? adjustment : None;

    /// <summary>The adjusted size in whole device pixels; never below one pixel.</summary>
    public int Apply(int natural, float contentScale)
    {
        double delta = IsPercent ? natural * Value / 100d : Value * (float.IsFinite(contentScale) && contentScale > 0f ? contentScale : 1f);
        return (int)Math.Max(1d, Math.Round(natural + delta, MidpointRounding.AwayFromZero));
    }
}

/// <summary>Inputs that select one <see cref="CellMetrics"/>; also the value part of the metrics cache key.</summary>
/// <param name="FontSizePx">Font size in device pixels (points × 96/72 × content scale).</param>
/// <param name="LineHeight">Multiplier of the font size giving the minimum cell height.</param>
/// <param name="ContentScale">Display content scale; converts logical-pixel adjustments.</param>
/// <param name="Raster">Hinting/antialiasing shared with the glyph atlas, so metrics match the drawn glyphs.</param>
internal readonly record struct CellMetricsOptions(
    float FontSizePx,
    double LineHeight,
    float ContentScale,
    CellAdjustment AdjustWidth = default,
    CellAdjustment AdjustHeight = default,
    RasterPolicy Raster = default);

/// <summary>
/// Cell geometry in whole device pixels; all vertical positions are measured down from the cell top.
/// This is the contract the renderer, decorations (W4-C3) and procedural sprites (W4-C4) build on.
/// </summary>
/// <param name="CellWidth">Cell advance.</param>
/// <param name="CellHeight">Row height.</param>
/// <param name="Baseline">Baseline position.</param>
/// <param name="Ascent">Rounded font ascent (baseline − ascent is the top of the font box).</param>
/// <param name="Descent">Rounded font descent.</param>
/// <param name="BaselineShift">
/// Half-leading offset of the baseline against a font box anchored at the cell top: glyphs rasterised
/// with their baseline at <c>ascent</c> move down by this many pixels (negative moves up when the line
/// height squeezes the font box).
/// </param>
/// <param name="UnderlinePosition">Top row of the underline stroke.</param>
/// <param name="UnderlineThickness">Underline stroke height, at least one pixel.</param>
/// <param name="StrikethroughPosition">Top row of the strikethrough stroke.</param>
/// <param name="StrikethroughThickness">Strikethrough stroke height, at least one pixel.</param>
/// <param name="OverlinePosition">Top row of the overline stroke (the cell top).</param>
/// <param name="OverlineThickness">Overline stroke height (the underline thickness).</param>
internal readonly record struct CellMetrics(
    int CellWidth,
    int CellHeight,
    int Baseline,
    int Ascent,
    int Descent,
    int BaselineShift,
    int UnderlinePosition,
    int UnderlineThickness,
    int StrikethroughPosition,
    int StrikethroughThickness,
    int OverlinePosition,
    int OverlineThickness);

/// <summary>
/// Pure derivation of <see cref="CellMetrics"/> from font metrics. Rounding rules: cell width is the
/// rounded advance; cell height is the rounded larger of <c>fontSize × lineHeight</c> and the font
/// box, then adjusted; extra height (positive or negative) is split half above and half below the
/// font box (decision F4, floor above); strokes are at least one pixel and are kept inside the cell.
/// </summary>
internal static class CellMetricsCalculator
{
    /// <summary>CSS reference: 96 pixels per inch, 72 points per inch.</summary>
    public const double PixelsPerPoint = 96d / 72d;

    /// <summary>Fallback stroke thickness as a fraction of the font size, for fonts without a post/OS2 value.</summary>
    public const float FallbackStrokeEm = 1f / 14f;

    /// <summary><c>font.size</c> points to device pixels: <c>pt × 96/72 × contentScale</c>.</summary>
    public static float PointsToPixels(double points, float contentScale)
    {
        double scale = float.IsFinite(contentScale) && contentScale > 0f ? contentScale : 1d;
        return (float)(points * PixelsPerPoint * scale);
    }

    public static CellMetrics Compute(in FontMetricsInput font, in CellMetricsOptions options)
    {
        float fontSize = float.IsFinite(options.FontSizePx) && options.FontSizePx > 0f ? options.FontSizePx : 1f;
        double lineHeight = double.IsFinite(options.LineHeight) && options.LineHeight > 0d ? options.LineHeight : 1d;

        float ascent = Positive(font.Ascent, fontSize);
        float descent = Positive(font.Descent, 0f);
        float advance = Positive(font.Advance, fontSize * 0.6f);

        int ascentPx = RoundPx(ascent);
        int descentPx = RoundPx(descent);
        int faceHeight = Math.Max(1, ascentPx + descentPx);

        int naturalWidth = Math.Max(1, RoundPx(advance));
        int naturalHeight = Math.Max(1, RoundPx(MathF.Max(fontSize * (float)lineHeight, MathF.Max(fontSize, ascent + descent))));
        int cellWidth = options.AdjustWidth.Apply(naturalWidth, options.ContentScale);
        int cellHeight = options.AdjustHeight.Apply(naturalHeight, options.ContentScale);

        // Half-leading: the font box sits in the middle of the row; an odd extra pixel goes below.
        int extra = cellHeight - faceHeight;
        int shift = (int)Math.Floor(extra / 2d);
        int baseline = Math.Clamp(ascentPx + shift, 0, cellHeight);

        float fallbackStroke = fontSize * FallbackStrokeEm;
        int underlineThickness = StrokePx(font.UnderlineThickness, fallbackStroke, cellHeight);
        float underlineOffset = float.IsFinite(font.UnderlinePosition)
            ? font.UnderlinePosition
            : MathF.Max(1f, descent * 0.5f - underlineThickness * 0.5f);
        int underlinePosition = Fit(baseline + RoundPx(underlineOffset), underlineThickness, cellHeight);

        int strikeThickness = StrokePx(font.StrikeoutThickness, fallbackStroke, cellHeight);
        float strikeTop;
        if (float.IsFinite(font.StrikeoutPosition))
        {
            strikeTop = baseline + font.StrikeoutPosition - strikeThickness;
        }
        else
        {
            float xHeight = float.IsFinite(font.XHeight) && font.XHeight > 0f ? font.XHeight : ascent * 0.5f;
            strikeTop = baseline - xHeight * 0.5f - strikeThickness * 0.5f;
        }
        int strikePosition = Fit(RoundPx(strikeTop), strikeThickness, cellHeight);

        return new CellMetrics(
            cellWidth,
            cellHeight,
            baseline,
            ascentPx,
            descentPx,
            shift,
            underlinePosition,
            underlineThickness,
            strikePosition,
            strikeThickness,
            OverlinePosition: 0,
            OverlineThickness: underlineThickness);
    }

    private static float Positive(float value, float fallback) =>
        float.IsFinite(value) ? MathF.Abs(value) : fallback;

    private static int RoundPx(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static int StrokePx(float thickness, float fallback, int cellHeight)
    {
        float value = float.IsFinite(thickness) && thickness > 0f ? thickness : fallback;
        return Math.Clamp(RoundPx(value), 1, Math.Max(1, cellHeight));
    }

    /// <summary>Clamps a stroke's top row so the whole stroke stays inside the cell.</summary>
    private static int Fit(int top, int thickness, int cellHeight) =>
        Math.Clamp(top, 0, Math.Max(0, cellHeight - thickness));
}
