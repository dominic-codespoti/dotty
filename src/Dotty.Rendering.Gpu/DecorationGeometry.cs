using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;

namespace Dotty.Rendering.Gpu;

/// <summary>Integer pixel rectangle relative to the cell's top-left corner.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Pure geometry of text decorations (SGR 4:1-4:5 underline styles, SGR 9 strikethrough, SGR 53 overline) in whole
/// device pixels, rasterised into A8 sprites that the frame builder emits as ordinary glyph-pass instances.
/// Positions and thicknesses come from <see cref="CellSpriteMetrics"/>; every pattern repeats per cell
/// (<c>x mod CellWidth</c>), so a span of cells, wide or not, continues seamlessly: dotted and dashed marks are
/// centred in equal segments of the cell, and the curly wave's period is the cell width.
/// </summary>
public static class DecorationGeometry
{
    /// <summary>Sprite code of the strikethrough/overline sprite; the low two bits carry the lines.</summary>
    private const int LinesCodeBase = -16;
    private const int StrikeBit = 1;
    private const int OverlineBit = 2;

    /// <summary>Negative sprite code of an underline style (-1 single … -5 dashed).</summary>
    public static int UnderlineCode(UnderlineStyle style) => -(int)style;

    /// <summary>Negative sprite code of the combined strikethrough / overline sprite (-17, -18 or -19).</summary>
    public static int LinesCode(bool strikethrough, bool overline) =>
        LinesCodeBase - ((strikethrough ? StrikeBit : 0) | (overline ? OverlineBit : 0));

    /// <summary>True for codes produced by <see cref="UnderlineCode"/> or <see cref="LinesCode"/>.</summary>
    public static bool IsDecorationCode(int code) =>
        code is >= -(int)UnderlineStyle.Dashed and <= -(int)UnderlineStyle.Single
        || code is >= LinesCodeBase - (StrikeBit | OverlineBit) and <= LinesCodeBase - StrikeBit;

    /// <summary>Gap between the two strokes of a double underline: the stroke thickness, at least one pixel.</summary>
    public static int DoubleGap(int thickness) => Math.Max(1, thickness);

    /// <summary>Peak-to-peak amplitude of the curly underline's centre line for a cell width.</summary>
    public static int CurlyAmplitude(int cellWidth) => Math.Max(1, (cellWidth + 4) / 8);

    /// <summary>
    /// Vertical band <c>[Top, Top + Height)</c> occupied by an underline of <paramref name="style"/>; always inside the
    /// cell.
    /// </summary>
    public static (int Top, int Height) UnderlineBand(UnderlineStyle style, in CellSpriteMetrics metrics)
    {
        int cellHeight = metrics.CellHeight;
        int thickness = metrics.UnderlineThickness;
        int height = style switch
        {
            UnderlineStyle.Double => DoubleStrokes(metrics).Height,
            UnderlineStyle.Curl => Math.Min(cellHeight, thickness + 2 * CurlyAmplitude(metrics.CellWidth)),
            _ => thickness,
        };
        if (style == UnderlineStyle.Double)
            return (DoubleStrokes(metrics).Top, height);
        return (Math.Clamp(metrics.UnderlinePosition, 0, cellHeight - height), height);
    }

    /// <summary>
    /// Filled rectangles of a straight underline style (single, double, dotted, dashed) across
    /// <paramref name="width"/> pixels. Curly underlines are not rectangles: see <see cref="CurlyCentre"/>.
    /// </summary>
    public static List<PixelRect> UnderlineRects(UnderlineStyle style, int width, in CellSpriteMetrics metrics)
    {
        var rects = new List<PixelRect>();
        if (width <= 0 || !metrics.IsValid)
            return rects;
        int thickness = metrics.UnderlineThickness;
        int top = Math.Clamp(metrics.UnderlinePosition, 0, metrics.CellHeight - thickness);
        switch (style)
        {
            case UnderlineStyle.Single:
                rects.Add(new PixelRect(0, top, width, thickness));
                break;
            case UnderlineStyle.Double:
                {
                    var (bandTop, _, stroke, gap) = DoubleStrokes(metrics);
                    rects.Add(new PixelRect(0, bandTop, width, stroke));
                    if (gap >= 0)
                        rects.Add(new PixelRect(0, bandTop + stroke + gap, width, stroke));
                    break;
                }
            case UnderlineStyle.Dotted:
                {
                    int dot = thickness;
                    int segments = Math.Clamp(metrics.CellWidth / (2 * dot), 1, Math.Max(1, metrics.CellWidth / 2));
                    AddSegmentMarks(rects, width, metrics.CellWidth, segments, top, thickness, segmentLength => dot);
                    break;
                }
            case UnderlineStyle.Dashed:
                {
                    int segments = Math.Clamp(metrics.CellWidth / (4 * Math.Max(2, thickness)), 1, Math.Max(1, metrics.CellWidth / 2));
                    AddSegmentMarks(rects, width, metrics.CellWidth, segments, top, thickness,
                        segmentLength => (segmentLength * 3 + 2) / 5);
                    break;
                }
        }
        return rects;
    }

    /// <summary>
    /// Centre line of the curly underline at horizontal position <paramref name="x"/> (pixels from the span's left
    /// edge, continuous): a raised cosine of period <c>CellWidth</c> inside <see cref="UnderlineBand"/>, so
    /// <c>CurlyCentre(x) == CurlyCentre(x + CellWidth)</c> and adjacent cells join without a phase step.
    /// </summary>
    public static float CurlyCentre(float x, in CellSpriteMetrics metrics)
    {
        var (top, height) = UnderlineBand(UnderlineStyle.Curl, metrics);
        float half = metrics.UnderlineThickness * 0.5f;
        float amplitude = (height - metrics.UnderlineThickness) * 0.5f;
        float phase = 2f * MathF.PI * (x / metrics.CellWidth);
        return top + half + amplitude * (1f - MathF.Cos(phase));
    }

    /// <summary>Rasterises an underline sprite <paramref name="width"/> pixels wide (one or more cells).</summary>
    public static SpriteBitmap RasterizeUnderline(UnderlineStyle style, int width, in CellSpriteMetrics metrics)
    {
        var (top, height) = UnderlineBand(style, metrics);
        var sprite = new SpriteBitmap(Math.Max(1, width), Math.Max(1, height), 0, top);
        if (style == UnderlineStyle.Curl)
        {
            RasterizeCurly(sprite, metrics, top);
            return sprite;
        }
        foreach (var rect in UnderlineRects(style, width, metrics))
            sprite.FillRect(rect.X, rect.Y - top, rect.Width, rect.Height);
        return sprite;
    }

    /// <summary>Rows <c>[Top, Top + Height)</c> covered by the combined strikethrough / overline sprite.</summary>
    public static (int Top, int Height) LinesBand(bool strikethrough, bool overline, in CellSpriteMetrics metrics)
    {
        int top = int.MaxValue, bottom = int.MinValue;
        if (strikethrough)
        {
            top = Math.Min(top, metrics.StrikethroughPosition);
            bottom = Math.Max(bottom, metrics.StrikethroughPosition + metrics.StrikethroughThickness);
        }
        if (overline)
        {
            top = Math.Min(top, metrics.OverlinePosition);
            bottom = Math.Max(bottom, metrics.OverlinePosition + metrics.OverlineThickness);
        }
        return top == int.MaxValue ? (0, 0) : (top, bottom - top);
    }

    /// <summary>Rasterises the strikethrough and/or overline strokes across <paramref name="width"/> pixels.</summary>
    public static SpriteBitmap RasterizeLines(bool strikethrough, bool overline, int width, in CellSpriteMetrics metrics)
    {
        var (top, height) = LinesBand(strikethrough, overline, metrics);
        var sprite = new SpriteBitmap(Math.Max(1, width), Math.Max(1, height), 0, top);
        if (strikethrough)
            sprite.FillRect(0, metrics.StrikethroughPosition - top, width, metrics.StrikethroughThickness);
        if (overline)
            sprite.FillRect(0, metrics.OverlinePosition - top, width, metrics.OverlineThickness);
        return sprite;
    }

    /// <summary>Rasterises the sprite for a decoration <paramref name="code"/> (see <see cref="IsDecorationCode"/>).</summary>
    public static SpriteBitmap Rasterize(int code, int width, in CellSpriteMetrics metrics)
    {
        if (code <= LinesCodeBase)
        {
            int bits = LinesCodeBase - code;
            return RasterizeLines((bits & StrikeBit) != 0, (bits & OverlineBit) != 0, width, metrics);
        }
        return RasterizeUnderline((UnderlineStyle)(-code), width, metrics);
    }

    private static (int Top, int Height, int Stroke, int Gap) DoubleStrokes(in CellSpriteMetrics metrics)
    {
        int cellHeight = metrics.CellHeight;
        int stroke = metrics.UnderlineThickness;
        int gap = DoubleGap(stroke);
        if (2 * stroke + gap > cellHeight)
        {
            // Too short a cell for two full strokes: thin them, keep a one-pixel gap, else draw one stroke.
            stroke = Math.Max(1, (cellHeight - 1) / 2);
            gap = cellHeight - 2 * stroke;
            if (gap < 1)
                return (Math.Clamp(metrics.UnderlinePosition, 0, cellHeight - 1), 1, 1, -1);
            gap = 1;
        }
        int height = 2 * stroke + gap;
        return (Math.Clamp(metrics.UnderlinePosition, 0, cellHeight - height), height, stroke, gap);
    }

    // Splits each cell of the span into `segments` integer segments and centres one mark per segment.
    private static void AddSegmentMarks(
        List<PixelRect> rects, int width, int cellWidth, int segments, int top, int height, Func<int, int> markLength)
    {
        for (int cellLeft = 0; cellLeft < width; cellLeft += cellWidth)
        {
            for (int i = 0; i < segments; i++)
            {
                int start = i * cellWidth / segments;
                int end = (i + 1) * cellWidth / segments;
                int length = end - start;
                int mark = length <= 1 ? length : Math.Clamp(markLength(length), 1, length - 1);
                int x = cellLeft + start + (length - mark) / 2;
                int clipped = Math.Min(mark, width - x);
                if (clipped > 0)
                    rects.Add(new PixelRect(x, top, clipped, height));
            }
        }
    }

    private static void RasterizeCurly(SpriteBitmap sprite, in CellSpriteMetrics metrics, int top)
    {
        float half = metrics.UnderlineThickness * 0.5f;
        float amplitude = (sprite.Height - metrics.UnderlineThickness) * 0.5f;
        float omega = 2f * MathF.PI / metrics.CellWidth;
        for (int x = 0; x < sprite.Width; x++)
        {
            // Sample the column at its centre; the slope term turns vertical distance into normal distance so the
            // stroke keeps its thickness on the flanks.
            float px = x + 0.5f;
            float centre = CurlyCentre(px, metrics) - top;
            float slope = amplitude * omega * MathF.Sin(omega * px);
            float normal = 1f / MathF.Sqrt(1f + slope * slope);
            for (int y = 0; y < sprite.Height; y++)
            {
                float distance = MathF.Abs(y + 0.5f - centre) * normal;
                float coverage = Math.Clamp(half + 0.5f - distance, 0f, 1f);
                sprite[x, y] = (byte)MathF.Round(coverage * 255f);
            }
        }
    }
}
