using System;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Result of <see cref="PlacementGeometry.Resolve"/>: clamped source rectangle (image pixels), clamped in-cell
/// offsets (pixels), effective cell box, and the destination pixel size at the cell size in force when the
/// placement was resolved (<see cref="BasisCellW"/> × <see cref="BasisCellH"/>).
/// </summary>
internal readonly struct ResolvedGeometry
{
    public int SrcX { get; init; }
    public int SrcY { get; init; }
    public int SrcW { get; init; }
    public int SrcH { get; init; }

    /// <summary>Offset of the image's top-left inside the anchor cell, in basis pixels, always &lt; cell size.</summary>
    public int XOff { get; init; }
    public int YOff { get; init; }

    /// <summary>Cells the placement occupies (box for stretched/aspect sizes; <c>ceil((px+off)/cell)</c> for native).</summary>
    public int EffCols { get; init; }
    public int EffRows { get; init; }

    /// <summary>True when neither columns nor rows were requested: drawn at native pixel size.</summary>
    public bool NativeSize { get; init; }

    /// <summary>Destination size in pixels at the basis cell size (excludes the offset).</summary>
    public float DestW { get; init; }
    public float DestH { get; init; }

    public int BasisCellW { get; init; }
    public int BasisCellH { get; init; }

    public bool IsEmpty => SrcW <= 0 || SrcH <= 0 || EffCols <= 0 || EffRows <= 0;
}

/// <summary>Destination rectangle size in pixels.</summary>
internal readonly record struct DestSize(float W, float H);

/// <summary>Rectangle relative to the top-left of a cell box, in pixels.</summary>
internal readonly record struct FitRect(float X, float Y, float W, float H);

/// <summary>
/// Pure placement sizing for the kitty graphics protocol. No function throws; hostile inputs (zero cell size,
/// huge values) are clamped. Intermediate math is done in <see cref="Int128"/>/<see cref="double"/>.
/// </summary>
internal static class PlacementGeometry
{
    /// <summary>Upper bound for any resolved cell count (keeps downstream int math safe).</summary>
    public const int MaxCells = 65535;

    /// <summary>
    /// Resolves a placement. Rules: source rect clamped to the image (<c>w = min(w, img-min(img,x))</c>, 0 ⇒ the
    /// remainder); offsets <c>min(off, cell-1)</c>; cols+rows ⇒ stretch to the box; one of them ⇒ the other is
    /// <c>ceil(boxPx · srcOther / srcThis / cellOther)</c> and the image is drawn with the source aspect;
    /// neither ⇒ native pixels with <c>ceil((src+off)/cell)</c> cells. Cell sizes &lt; 1 are treated as 1.
    /// An empty image or empty source rectangle yields an empty result (<see cref="ResolvedGeometry.IsEmpty"/>).
    /// </summary>
    public static ResolvedGeometry Resolve(
        uint imageW, uint imageH,
        uint srcX, uint srcY, uint srcW, uint srcH,
        uint xOff, uint yOff,
        uint reqCols, uint reqRows,
        int cellPxW, int cellPxH)
    {
        int cw = Math.Max(cellPxW, 1);
        int ch = Math.Max(cellPxH, 1);
        long iw = imageW, ih = imageH;

        long sx = Math.Min((long)srcX, iw);
        long sy = Math.Min((long)srcY, ih);
        long remW = iw - sx, remH = ih - sy;
        long sw = srcW == 0 ? remW : Math.Min((long)srcW, remW);
        long sh = srcH == 0 ? remH : Math.Min((long)srcH, remH);

        if (sw <= 0 || sh <= 0 || sw > int.MaxValue || sh > int.MaxValue)
            return new ResolvedGeometry { BasisCellW = cw, BasisCellH = ch };

        int ox = (int)Math.Min((long)xOff, cw - 1L);
        int oy = (int)Math.Min((long)yOff, ch - 1L);

        long cols = Math.Min((long)reqCols, MaxCells);
        long rows = Math.Min((long)reqRows, MaxCells);
        bool native = false;
        double destW, destH;

        if (cols > 0 && rows > 0)
        {
            destW = (double)cols * cw;
            destH = (double)rows * ch;
        }
        else if (cols > 0)
        {
            destW = (double)cols * cw;
            destH = destW * sh / sw;
            rows = ClampCells(CeilDiv((Int128)cols * cw * sh, (Int128)sw * ch));
            destH = Math.Min(destH, (double)MaxCells * ch);
        }
        else if (rows > 0)
        {
            destH = (double)rows * ch;
            destW = destH * sw / sh;
            cols = ClampCells(CeilDiv((Int128)rows * ch * sw, (Int128)sh * cw));
            destW = Math.Min(destW, (double)MaxCells * cw);
        }
        else
        {
            native = true;
            destW = sw;
            destH = sh;
            cols = ClampCells(CeilDiv((Int128)sw + ox, cw));
            rows = ClampCells(CeilDiv((Int128)sh + oy, ch));
        }

        return new ResolvedGeometry
        {
            SrcX = (int)sx,
            SrcY = (int)sy,
            SrcW = (int)sw,
            SrcH = (int)sh,
            XOff = ox,
            YOff = oy,
            EffCols = (int)cols,
            EffRows = (int)rows,
            NativeSize = native,
            DestW = (float)destW,
            DestH = (float)destH,
            BasisCellW = cw,
            BasisCellH = ch,
        };
    }

    /// <summary>
    /// Destination pixel size at the current cell size. Cell-specified sizes follow the cell size linearly;
    /// native sizes scale by <c>cellNow/basis</c> so zoom/DPI changes keep the image proportional to the grid.
    /// </summary>
    public static DestSize DestRect(in ResolvedGeometry g, float cellNowW, float cellNowH)
    {
        if (g.IsEmpty || !float.IsFinite(cellNowW) || !float.IsFinite(cellNowH) || cellNowW <= 0 || cellNowH <= 0)
            return default;
        int bw = Math.Max(g.BasisCellW, 1), bh = Math.Max(g.BasisCellH, 1);
        return new DestSize(g.DestW * cellNowW / bw, g.DestH * cellNowH / bh);
    }

    /// <summary>
    /// Removes the top <paramref name="rows"/> cell rows from the visible placement. The removed pixel height
    /// (<c>rows·cellH − yOff</c>, clamped) is taken from the source proportionally to the destination height;
    /// the in-cell Y offset is consumed (0) once any row is cropped. Native-size placements stay native.
    /// </summary>
    public static ResolvedGeometry CropTop(in ResolvedGeometry g, int rows)
    {
        if (rows <= 0 || g.IsEmpty) return g;
        if (rows >= g.EffRows) return Emptied(g);
        double removed = Math.Min((double)rows * g.BasisCellH - g.YOff, g.DestH);
        if (removed < 0) removed = 0;
        if (removed >= g.DestH) return Emptied(g);
        int shift = SourceShift(g.SrcH, removed, g.DestH);
        return With(g, srcY: g.SrcY + shift, srcH: g.SrcH - shift, yOff: 0,
            effRows: g.EffRows - rows, destH: (float)(g.DestH - removed));
    }

    /// <summary>
    /// Removes the bottom <paramref name="rows"/> cell rows. Pixels below the new bottom cell edge
    /// (<c>yOff + destH − (effRows−rows)·cellH</c>, clamped) are taken proportionally off the source bottom.
    /// </summary>
    public static ResolvedGeometry CropBottom(in ResolvedGeometry g, int rows)
    {
        if (rows <= 0 || g.IsEmpty) return g;
        if (rows >= g.EffRows) return Emptied(g);
        double visibleBottom = (double)(g.EffRows - rows) * g.BasisCellH;
        double removed = Math.Clamp(g.YOff + (double)g.DestH - visibleBottom, 0, g.DestH);
        if (removed >= g.DestH || visibleBottom <= g.YOff) return Emptied(g);
        int shift = SourceShift(g.SrcH, removed, g.DestH);
        return With(g, srcY: g.SrcY, srcH: g.SrcH - shift, yOff: g.YOff,
            effRows: g.EffRows - rows, destH: (float)(g.DestH - removed));
    }

    /// <summary>
    /// Aspect-preserving fit of a <paramref name="srcW"/>×<paramref name="srcH"/> pixel image into a
    /// <paramref name="cols"/>×<paramref name="rows"/> cell box, centred (letterbox/pillarbox). May upscale.
    /// Coordinates are pixels relative to the box's top-left. Degenerate input returns an empty rect.
    /// </summary>
    public static FitRect FitIntoBox(int srcW, int srcH, int cols, int rows, float cellW, float cellH)
    {
        if (srcW <= 0 || srcH <= 0 || cols <= 0 || rows <= 0
            || !float.IsFinite(cellW) || !float.IsFinite(cellH) || cellW <= 0 || cellH <= 0)
            return default;
        double boxW = (double)cols * cellW, boxH = (double)rows * cellH;
        double scale = Math.Min(boxW / srcW, boxH / srcH);
        double w = srcW * scale, h = srcH * scale;
        return new FitRect((float)((boxW - w) / 2), (float)((boxH - h) / 2), (float)w, (float)h);
    }

    private static int SourceShift(int srcLen, double removedPx, double destPx)
    {
        int shift = (int)Math.Round(srcLen * (removedPx / destPx), MidpointRounding.AwayFromZero);
        return Math.Clamp(shift, 0, srcLen - 1);
    }

    private static ResolvedGeometry Emptied(in ResolvedGeometry g) => With(g, g.SrcX, g.SrcY, 0, 0, 0, 0f);

    private static ResolvedGeometry With(in ResolvedGeometry g, int srcX, int srcY, int srcH, int yOff, int effRows, float destH)
        => new()
        {
            SrcX = srcX,
            SrcY = srcY,
            SrcW = g.SrcW,
            SrcH = srcH,
            XOff = g.XOff,
            YOff = yOff,
            EffCols = g.EffCols,
            EffRows = effRows,
            NativeSize = g.NativeSize,
            DestW = g.DestW,
            DestH = destH,
            BasisCellW = g.BasisCellW,
            BasisCellH = g.BasisCellH,
        };

    private static ResolvedGeometry With(in ResolvedGeometry g, int srcY, int srcH, int yOff, int effRows, float destH)
        => With(g, g.SrcX, srcY, srcH, yOff, effRows, destH);

    private static Int128 CeilDiv(Int128 n, Int128 d) => (n + d - 1) / d;

    private static long ClampCells(Int128 v) => v < 1 ? 1 : v > MaxCells ? MaxCells : (long)v;
}
