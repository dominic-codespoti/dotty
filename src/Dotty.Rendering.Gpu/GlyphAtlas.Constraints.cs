using System;
using System.Threading;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>Real cell geometry the atlas fits constrained glyphs into (W4-C9).</summary>
public sealed partial class GlyphAtlas
{
    /// <summary>Cell box in device pixels. <paramref name="BaselineShift"/> is the host's half-leading shift that the
    /// frame builder adds to every glyph's baseline-relative offset.</summary>
    private sealed record CellGeometry(float Width, float Height, float Baseline, float BaselineShift);

    private CellGeometry? _cellGeometry;

    /// <summary>Cell width the atlas currently fits glyphs into.</summary>
    public float CellWidth => CurrentCellGeometry().Width;

    /// <summary>Cell height the atlas currently fits glyphs into.</summary>
    public float CellHeight => CurrentCellGeometry().Height;

    /// <summary>
    /// Supplies the host's real cell size (device pixels), baseline from the cell top and half-leading shift. Call at
    /// a frame boundary: a change rebuilds the atlas because constrained glyphs were fitted to the previous cell.
    /// Non-positive sizes are ignored.
    /// </summary>
    /// <returns>True when the geometry changed (and the atlas was rebuilt).</returns>
    public bool SetCellGeometry(int cellWidth, int cellHeight, int baseline, int baselineShift)
    {
        if (cellWidth <= 0 || cellHeight <= 0)
            return false;
        var next = new CellGeometry(cellWidth, cellHeight, Math.Clamp(baseline, 0, cellHeight), baselineShift);
        var previous = Interlocked.Exchange(ref _cellGeometry, next);
        if (previous == next)
            return false;
        Rebuild();
        return true;
    }

    private CellGeometry CurrentCellGeometry() => Volatile.Read(ref _cellGeometry) ?? _defaultCellGeometry;

    private CellGeometry _defaultCellGeometry = null!;

    private void InitDefaultCellGeometry(SKFont primaryFont)
    {
        float advance = MathF.Max(1f, MathF.Round(primaryFont.MeasureText("M")));
        _defaultCellGeometry = new CellGeometry(advance, _primaryCellHeight, _primaryBaseline, 0f);
    }

    /// <summary>
    /// Rasterises <paramref name="key"/> with <paramref name="face"/> and places it with <see cref="ConstraintSolver"/>
    /// inside <see cref="GlyphKey.CellSpan"/> real cells. Scaling re-rasterises at the solved size (non-uniform via
    /// <see cref="SKFont.ScaleX"/>) rather than resampling the bitmap; a raster that still cannot fit is returned with
    /// zero size so the caller draws the reserved tofu glyph.
    /// </summary>
    private GlyphRaster RasterizeConstrained(GlyphKey key, SKTypeface face, in GlyphConstraint constraint,
        bool syntheticBold, bool syntheticItalic)
    {
        var cell = CurrentCellGeometry();
        int cells = Math.Clamp(key.CellSpan, 1, Math.Max(1, (int)constraint.MaxSpan));
        float boxWidth = cell.Width * cells;
        using var font = new SKFont(face, key.TextSize)
        {
            Edging = _rasterPolicy.SkiaEdging,
            Subpixel = false,
            Hinting = _rasterPolicy.SkiaHinting,
            SkewX = syntheticItalic ? SyntheticItalicSkewX : 0f,
        };
        float scaleX = 1f, scaleY = 1f;
        var raster = RasterizeTightCore(key, blob: null, baselineOverride: _primaryBaseline,
            maxAdvance: boxWidth, fontOverride: font, syntheticBold: syntheticBold);
        for (int attempt = 0; ; attempt++)
        {
            if (raster.Width <= 0 || raster.Height <= 0)
                return raster;
            var ink = new InkBounds(raster.LeftBearing, raster.TopBearing, raster.Width, raster.Height);
            var placement = ConstraintSolver.Solve(ink, cell.Width, cell.Height, cell.Baseline, key.CellSpan, constraint);
            if (!placement.IsUnscaled && attempt < 3)
            {
                scaleX *= placement.ScaleX;
                scaleY *= placement.ScaleY;
                font.Size = MathF.Max(0.5f, key.TextSize * scaleY);
                font.ScaleX = scaleX / scaleY;
                raster.Image.Dispose();
                raster = RasterizeTightCore(key, blob: null, baselineOverride: _primaryBaseline,
                    maxAdvance: boxWidth, fontOverride: font, syntheticBold: syntheticBold);
                continue;
            }

            if (!placement.IsUnscaled)
            {
                // Hinting kept the size from converging: position the last raster as it is.
                placement = ConstraintSolver.Solve(ink, cell.Width, cell.Height, cell.Baseline, key.CellSpan,
                    constraint with { Size = GlyphSizeMode.None });
            }
            if (raster.Width > MathF.Ceiling(boxWidth) || raster.Height > MathF.Ceiling(cell.Height))
            {
                return new GlyphRaster(raster.Image, 0, 0, 0, 0,
                    MathF.Min(raster.Advance, boxWidth), _primaryBaseline, 0f, 0f);
            }
            // The frame builder draws at BaselineOffset + TopBearing + BaselineShift from the cell top.
            return new GlyphRaster(raster.Image, raster.Left, raster.Top, raster.Width, raster.Height,
                MathF.Min(raster.Advance, boxWidth), _primaryBaseline,
                MathF.Round(placement.X),
                MathF.Round(placement.Y) - cell.BaselineShift - _primaryBaseline,
                raster.IsColor);
        }
    }
}
