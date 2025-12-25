using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Dotty.App.Controls.Rendering;

internal sealed class PowerlineGlyphRenderer
{
    private StreamGeometry? _geoRightArrow;
    private StreamGeometry? _geoLeftArrow;
    private StreamGeometry? _geoRightSemicircle;
    private StreamGeometry? _geoLeftSemicircle;

    private static readonly RenderOptions s_aliasRenderOptions = new() { EdgeMode = EdgeMode.Aliased };

    private enum PowerlineGlyphVariant
    {
        FilledRightArrow,
        ThinRightArrow,
        FilledLeftArrow,
        ThinLeftArrow,
        FilledRightArc,
        ThinRightArc,
        FilledLeftArc,
        ThinLeftArc
    }

    private static readonly Dictionary<char, PowerlineGlyphVariant> s_variants = new()
    {
        ['\uE0B0'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0B1'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0B2'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0B3'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0B4'] = PowerlineGlyphVariant.FilledRightArc,
        ['\uE0B5'] = PowerlineGlyphVariant.ThinRightArc,
        ['\uE0B6'] = PowerlineGlyphVariant.FilledLeftArc,
        ['\uE0B7'] = PowerlineGlyphVariant.ThinLeftArc,
        ['\uE0B8'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0B9'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0BA'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0BB'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0BC'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0BD'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0BE'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0BF'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0C0'] = PowerlineGlyphVariant.FilledRightArc,
        ['\uE0C1'] = PowerlineGlyphVariant.ThinRightArc,
        ['\uE0C2'] = PowerlineGlyphVariant.FilledLeftArc,
        ['\uE0C3'] = PowerlineGlyphVariant.ThinLeftArc,
        ['\uE0C4'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0C5'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0C6'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0C7'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0C8'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0C9'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0CA'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0CB'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0CC'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0CD'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0CE'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0CF'] = PowerlineGlyphVariant.ThinRightArrow,
    };

    public void UpdateGeometries(double cellWidth, double cellHeight)
    {
        double w = cellWidth;
        double h = cellHeight;

        _geoRightArrow = new StreamGeometry();
        using (var ctx = _geoRightArrow.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(0, h));
            ctx.LineTo(new Point(w, h / 2));
            ctx.EndFigure(true);
        }

        _geoLeftArrow = new StreamGeometry();
        using (var ctx = _geoLeftArrow.Open())
        {
            ctx.BeginFigure(new Point(w, 0), true);
            ctx.LineTo(new Point(w, h));
            ctx.LineTo(new Point(0, h / 2));
            ctx.EndFigure(true);
        }

        _geoRightSemicircle = new StreamGeometry();
        using (var ctx = _geoRightSemicircle.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(w, 0));
            ctx.ArcTo(new Point(w, h), new Size(w, h), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0, h));
            ctx.EndFigure(true);
        }

        _geoLeftSemicircle = new StreamGeometry();
        using (var ctx = _geoLeftSemicircle.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.ArcTo(new Point(0, h), new Size(w, h), 0, false, SweepDirection.CounterClockwise);
            ctx.LineTo(new Point(w, h));
            ctx.LineTo(new Point(w, 0));
            ctx.EndFigure(true);
        }
    }

    public void Draw(DrawingContext context, char glyph, double x, double y, IBrush brush, double cellWidth, double cellHeight, double renderScaling)
    {
        if (!s_variants.TryGetValue(glyph, out var variant))
        {
            DrawFallbackRect(context, x, y, brush, cellWidth, cellHeight, renderScaling);
            return;
        }

        switch (variant)
        {
            case PowerlineGlyphVariant.FilledRightArrow:
                DrawFilledGeometry(context, _geoRightArrow, x, y, brush, renderScaling);
                break;
            case PowerlineGlyphVariant.FilledLeftArrow:
                DrawFilledGeometry(context, _geoLeftArrow, x, y, brush, renderScaling);
                break;
            case PowerlineGlyphVariant.FilledRightArc:
                DrawFilledGeometry(context, _geoRightSemicircle, x, y, brush, renderScaling);
                break;
            case PowerlineGlyphVariant.FilledLeftArc:
                DrawFilledGeometry(context, _geoLeftSemicircle, x, y, brush, renderScaling);
                break;
            case PowerlineGlyphVariant.ThinRightArrow:
                DrawOutlineGeometry(context, _geoRightArrow, x, y, brush, cellWidth, renderScaling);
                break;
            case PowerlineGlyphVariant.ThinLeftArrow:
                DrawOutlineGeometry(context, _geoLeftArrow, x, y, brush, cellWidth, renderScaling);
                break;
            case PowerlineGlyphVariant.ThinRightArc:
                DrawOutlineGeometry(context, _geoRightSemicircle, x, y, brush, cellWidth, renderScaling);
                break;
            case PowerlineGlyphVariant.ThinLeftArc:
                DrawOutlineGeometry(context, _geoLeftSemicircle, x, y, brush, cellWidth, renderScaling);
                break;
            default:
                DrawFallbackRect(context, x, y, brush, cellWidth, cellHeight, renderScaling);
                break;
        }
    }

    public bool IsPowerlineGlyph(char c) => s_variants.ContainsKey(c);

    private static void DrawFilledGeometry(DrawingContext context, StreamGeometry? geometry, double x, double y, IBrush brush, double renderScaling)
    {
        if (geometry is null)
        {
            return;
        }

        using var alias = context.PushRenderOptions(s_aliasRenderOptions);
        using var state = context.PushTransform(Matrix.CreateTranslation(Snap(x, renderScaling), Snap(y, renderScaling)));
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawOutlineGeometry(DrawingContext context, StreamGeometry? geometry, double x, double y, IBrush brush, double cellWidth, double renderScaling)
    {
        if (geometry is null)
        {
            return;
        }

        var thickness = Math.Max(1, cellWidth * 0.18);
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };

        using var alias = context.PushRenderOptions(s_aliasRenderOptions);
        using var state = context.PushTransform(Matrix.CreateTranslation(Snap(x, renderScaling), Snap(y, renderScaling)));
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawFallbackRect(DrawingContext context, double x, double y, IBrush brush, double cellWidth, double cellHeight, double renderScaling)
    {
        var rect = new Rect(Snap(x, renderScaling), Snap(y, renderScaling), cellWidth, cellHeight);
        context.FillRectangle(brush, rect);
    }

    private static double Snap(double value, double renderScaling)
    {
        var scale = renderScaling <= 0 ? 1.0 : renderScaling;
        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }
}
