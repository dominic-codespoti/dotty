using System;

namespace Dotty.Rendering.Gpu;

/// <summary>Tight ink box of a rasterised glyph, in pixels, relative to its pen origin on the baseline (y grows down).</summary>
public readonly record struct InkBounds(float Left, float Top, float Width, float Height);

/// <summary>
/// Solver output: per-axis scale applied to the ink, and the top-left corner of the scaled ink relative to the
/// top-left of the glyph's first cell.
/// </summary>
public readonly record struct GlyphPlacement(float ScaleX, float ScaleY, float X, float Y)
{
    public bool IsUnscaled => MathF.Abs(ScaleX - 1f) < 0.005f && MathF.Abs(ScaleY - 1f) < 0.005f;
}

/// <summary>
/// Pure placement of a glyph's ink into a cell box under a <see cref="GlyphConstraint"/>. Replaces the atlas'
/// former fallback height-ratio heuristic: the decision is taken from the real ink box and the real cell size.
/// </summary>
public static class ConstraintSolver
{
    public const float MaxPadding = 0.45f;
    public const float MinScale = 0.01f;

    /// <param name="ink">Ink box relative to the pen origin on the baseline.</param>
    /// <param name="cellWidth">Cell width in pixels.</param>
    /// <param name="cellHeight">Cell height in pixels.</param>
    /// <param name="baseline">Baseline position measured down from the cell top.</param>
    /// <param name="span">Cells the glyph may occupy (1 or 2); clamped to 1..<see cref="GlyphConstraint.MaxSpan"/>.</param>
    /// <param name="constraint">Sizing and alignment rule.</param>
    public static GlyphPlacement Solve(InkBounds ink, float cellWidth, float cellHeight, float baseline, int span,
        in GlyphConstraint constraint)
    {
        var identity = new GlyphPlacement(1f, 1f, ink.Left, baseline + ink.Top);
        if (constraint.IsNone
            || !(ink.Width > 0f) || !(ink.Height > 0f) || !float.IsFinite(ink.Width) || !float.IsFinite(ink.Height)
            || !float.IsFinite(ink.Left) || !float.IsFinite(ink.Top)
            || !(cellWidth > 0f) || !(cellHeight > 0f) || !float.IsFinite(cellWidth) || !float.IsFinite(cellHeight)
            || !float.IsFinite(baseline))
        {
            return identity;
        }

        int cells = Math.Clamp(span, 1, Math.Max(1, (int)constraint.MaxSpan));
        float boxWidth = cellWidth * cells;
        float padding = float.IsFinite(constraint.Padding) ? Math.Clamp(constraint.Padding, 0f, MaxPadding) : 0f;
        float boxX = boxWidth * padding;
        float boxY = cellHeight * padding;
        float innerW = boxWidth - 2f * boxX;
        float innerH = cellHeight - 2f * boxY;

        float fit = MathF.Min(innerW / ink.Width, innerH / ink.Height);
        float sx, sy;
        switch (constraint.Size)
        {
            case GlyphSizeMode.Fit:
                sx = sy = MathF.Min(1f, fit);
                break;
            case GlyphSizeMode.Cover:
                sx = sy = fit;
                break;
            case GlyphSizeMode.Stretch:
                sx = innerW / ink.Width;
                sy = innerH / ink.Height;
                break;
            default:
                sx = sy = 1f;
                break;
        }
        sx = MathF.Max(MinScale, sx);
        sy = MathF.Max(MinScale, sy);

        float w = ink.Width * sx;
        float h = ink.Height * sy;
        float x = Align(constraint.AlignX, ink.Left * sx, w, boxX, innerW);
        float y = Align(constraint.AlignY, baseline + ink.Top * sy, h, boxY, innerH);
        return new GlyphPlacement(sx, sy, x, y);
    }

    private static float Align(GlyphAlign align, float natural, float size, float start, float extent) => align switch
    {
        GlyphAlign.Start => start,
        GlyphAlign.Center => start + (extent - size) * 0.5f,
        GlyphAlign.End => start + extent - size,
        // Natural position, moved inside only when it overflows; ink larger than the box is centred.
        _ => size > extent
            ? start + (extent - size) * 0.5f
            : Math.Clamp(natural, start, start + extent - size),
    };
}
