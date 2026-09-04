using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.Runtime.Search;

/// <summary>
/// GPU quad instance emitter for search highlights across the terminal grid
/// and for the floating search box overlay.
/// </summary>
public static class SearchQuadBuilder
{
    // High-visibility search highlight colors
    // Active match: bright orange/yellow (0xFFFF9800)
    // Other matches: translucent yellow/amber (0x80FFD54F)
    public static readonly SgrColorArgb ActiveMatchBackground = SgrColorArgb.FromRgb(255, 152, 0);
    public static readonly SgrColorArgb ActiveMatchForeground = SgrColorArgb.FromRgb(0, 0, 0);
    public static readonly SgrColorArgb MatchBackground = SgrColorArgb.FromRgb(255, 213, 79);
    public static readonly SgrColorArgb MatchForeground = SgrColorArgb.FromRgb(0, 0, 0);

    // Overlay colors
    public static readonly SgrColorArgb OverlayBoxBg = SgrColorArgb.FromRgb(37, 37, 38);
    public static readonly SgrColorArgb OverlayInputBg = SgrColorArgb.FromRgb(51, 51, 51);
    public static readonly SgrColorArgb OverlayButtonBg = SgrColorArgb.FromRgb(45, 45, 45);
    public static readonly SgrColorArgb OverlayTextFg = SgrColorArgb.FromRgb(204, 204, 204);
    public static readonly SgrColorArgb OverlayMutedFg = SgrColorArgb.FromRgb(150, 150, 150);

    /// <summary>
    /// Emits highlight quads for search matches visible on the terminal grid.
    /// </summary>
    /// <param name="matches">Search matches to render.</param>
    /// <param name="visibleRows">Number of visible rows in the viewport.</param>
    /// <param name="visibleCols">Number of visible columns in the viewport.</param>
    /// <param name="destination">Destination span for generated CellInstances.</param>
    /// <returns>Number of cell instances written.</returns>
    public static int BuildHighlightQuads(
        IReadOnlyList<SearchMatch> matches,
        int visibleRows,
        int visibleCols,
        Span<CellInstance> destination)
    {
        if (matches == null || matches.Count == 0 || destination.IsEmpty)
        {
            return 0;
        }

        int written = 0;

        for (int m = 0; m < matches.Count; m++)
        {
            var match = matches[m];

            // Only highlight visible rows (row >= 0 and row < visibleRows)
            if (match.Row < 0 || match.Row >= visibleRows)
                continue;

            int startCol = Math.Max(0, match.StartCol);
            int endCol = Math.Min(visibleCols, match.EndCol);

            if (startCol >= endCol)
                continue;

            var bg = match.IsActive ? ActiveMatchBackground : MatchBackground;
            var fg = match.IsActive ? ActiveMatchForeground : MatchForeground;

            for (int col = startCol; col < endCol; col++)
            {
                if (written >= destination.Length)
                    return written;

                destination[written++] = new CellInstance
                {
                    Col = (ushort)col,
                    Row = (ushort)match.Row,
                    FgR = fg.R,
                    FgG = fg.G,
                    FgB = fg.B,
                    BgR = bg.R,
                    BgG = bg.G,
                    BgB = bg.B,
                    BgA = 255,
                    Flags = CellFlags.InverseVideo
                };
            }
        }

        return written;
    }

    /// <summary>
    /// Emits <see cref="CellInstance"/> quads for the floating search box overlay
    /// into the destination span, placing characters using the provided glyph atlas.
    /// </summary>
    /// <param name="layout">Computed search overlay layout.</param>
    /// <param name="cellWidth">Width of one terminal grid cell in pixels.</param>
    /// <param name="cellHeight">Height of one terminal grid cell in pixels.</param>
    /// <param name="atlas">Glyph atlas for text rendering.</param>
    /// <param name="typeface">Typeface used for atlas lookup.</param>
    /// <param name="textSize">Text size for atlas lookup.</param>
    /// <param name="destination">Destination span to receive quad instances.</param>
    /// <param name="dirtyAtlasRows">Optional tracker for dirty atlas rows.</param>
    /// <returns>Number of cell instances emitted.</returns>
    public static int BuildOverlayQuads(
        in SearchOverlayLayout layout,
        float cellWidth,
        float cellHeight,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float textSize,
        Span<CellInstance> destination,
        HashSet<int>? dirtyAtlasRows = null)
    {
        if (cellWidth <= 0 || cellHeight <= 0 || destination.IsEmpty)
            return 0;

        int written = 0;

        // Convert overlay pixel coordinates to cell grid coordinates
        int startCol = (int)(layout.X / cellWidth);
        int startRow = (int)(layout.Y / cellHeight);
        int colSpan = Math.Max(1, (int)MathF.Ceiling(layout.Width / cellWidth));
        int rowSpan = Math.Max(1, (int)MathF.Ceiling(layout.Height / cellHeight));

        // 1. Emit background box quads for overlay panel
        for (int r = 0; r < rowSpan; r++)
        {
            int gridRow = startRow + r;
            for (int c = 0; c < colSpan; c++)
            {
                if (written >= destination.Length)
                    return written;

                int gridCol = startCol + c;

                destination[written++] = new CellInstance
                {
                    Col = (ushort)gridCol,
                    Row = (ushort)gridRow,
                    BgR = OverlayBoxBg.R,
                    BgG = OverlayBoxBg.G,
                    BgB = OverlayBoxBg.B,
                    BgA = 255
                };
            }
        }

        // 2. Render input query text inside the input box
        int inputStartCol = (int)(layout.InputBoxRect.X / cellWidth);
        int inputRow = (int)(layout.InputBoxRect.Y / cellHeight);
        int inputColCount = (int)(layout.InputBoxRect.Width / cellWidth);

        string query = layout.Query;
        if (!string.IsNullOrEmpty(query))
        {
            int queryLen = Math.Min(query.Length, inputColCount);
            for (int i = 0; i < queryLen; i++)
            {
                if (written >= destination.Length)
                    return written;

                char ch = query[i];
                string grapheme = ch.ToString();
                var key = new GlyphKey(grapheme, typeface, textSize, false);

                int countBefore = atlas.EntryCount;
                bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
                if (!glyphOk)
                    glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
                if (glyphOk)
                {
                    if (atlas.EntryCount > countBefore)
                        dirtyAtlasRows?.Add(inputRow);

                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)(inputStartCol + i),
                        Row = (ushort)inputRow,
                        OffX = (short)glyphInfo.LeftBearing,
                        OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing),
                        GlyphX = (short)glyphInfo.X,
                        GlyphY = (short)glyphInfo.Y,
                        GlyphW = (short)glyphInfo.Width,
                        GlyphH = (short)glyphInfo.Height,
                        FgR = OverlayTextFg.R,
                        FgG = OverlayTextFg.G,
                        FgB = OverlayTextFg.B,
                        BgR = OverlayInputBg.R,
                        BgG = OverlayInputBg.G,
                        BgB = OverlayInputBg.B,
                        BgA = 255
                    };
                }
            }
        }

        // 3. Render match count badge (e.g. "3/42")
        int badgeStartCol = (int)(layout.MatchCountRect.X / cellWidth);
        int badgeRow = (int)(layout.MatchCountRect.Y / cellHeight);
        string badge = layout.MatchBadgeText;

        if (!string.IsNullOrEmpty(badge))
        {
            for (int i = 0; i < badge.Length; i++)
            {
                if (written >= destination.Length)
                    return written;

                char ch = badge[i];
                string grapheme = ch.ToString();
                var key = new GlyphKey(grapheme, typeface, textSize, false);

                int countBefore = atlas.EntryCount;
                bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
                if (!glyphOk)
                    glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
                if (glyphOk)
                {
                    if (atlas.EntryCount > countBefore)
                        dirtyAtlasRows?.Add(badgeRow);

                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)(badgeStartCol + i),
                        Row = (ushort)badgeRow,
                        OffX = (short)glyphInfo.LeftBearing,
                        OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing),
                        GlyphX = (short)glyphInfo.X,
                        GlyphY = (short)glyphInfo.Y,
                        GlyphW = (short)glyphInfo.Width,
                        GlyphH = (short)glyphInfo.Height,
                        FgR = OverlayMutedFg.R,
                        FgG = OverlayMutedFg.G,
                        FgB = OverlayMutedFg.B,
                        BgR = OverlayBoxBg.R,
                        BgG = OverlayBoxBg.G,
                        BgB = OverlayBoxBg.B,
                        BgA = 255
                    };
                }
            }
        }

        // 4. Render control buttons: Prev (▲), Next (▼), Close (×)

        RenderButtonGlyph(destination, ref written, layout.PrevButtonRect, "▲", OverlayTextFg, cellWidth, cellHeight, typeface, textSize, atlas, dirtyAtlasRows);
        RenderButtonGlyph(destination, ref written, layout.NextButtonRect, "▼", OverlayTextFg, cellWidth, cellHeight, typeface, textSize, atlas, dirtyAtlasRows);
        RenderButtonGlyph(destination, ref written, layout.CloseButtonRect, "×", OverlayTextFg, cellWidth, cellHeight, typeface, textSize, atlas, dirtyAtlasRows);

        return written;
    }
    private static void RenderButtonGlyph(
        Span<CellInstance> destination,
        ref int written,
        OverlayRect rect,
        string glyphText,
        SgrColorArgb fg,
        float cellWidth,
        float cellHeight,
        SKTypeface typeface,
        float textSize,
        GlyphAtlas atlas,
        HashSet<int>? dirtyAtlasRows)
    {
        if (written >= destination.Length || string.IsNullOrEmpty(glyphText))
            return;

        int col = (int)(rect.X / cellWidth);
        int row = (int)(rect.Y / cellHeight);

        var key = new GlyphKey(glyphText, typeface, textSize, false);
        int countBefore = atlas.EntryCount;
        bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
        if (!glyphOk)
            glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
        if (glyphOk)
        {
            if (atlas.EntryCount > countBefore)
                dirtyAtlasRows?.Add(row);

            destination[written++] = new CellInstance
            {
                Col = (ushort)col,
                Row = (ushort)row,
                OffX = (short)glyphInfo.LeftBearing,
                OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing),
                GlyphX = (short)glyphInfo.X,
                GlyphY = (short)glyphInfo.Y,
                GlyphW = (short)glyphInfo.Width,
                GlyphH = (short)glyphInfo.Height,
                FgR = fg.R,
                FgG = fg.G,
                FgB = fg.B,
                BgR = OverlayButtonBg.R,
                BgG = OverlayButtonBg.G,
                BgB = OverlayButtonBg.B,
                BgA = 255
            };
        }
    }
}
