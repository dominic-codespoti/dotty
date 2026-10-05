using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

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

    /// <summary>
    /// Emits highlight quads for search matches visible on the terminal grid.
    /// </summary>
    /// <param name="matches">Search matches to render.</param>
    /// <param name="visibleRows">Number of visible rows in the viewport.</param>
    /// <param name="visibleCols">Number of visible columns in the viewport.</param>
    /// <param name="destination">Destination span for generated CellInstances.</param>
    /// <param name="logicalRowOffset">Offset from a match's logical row to its visible row.</param>
    /// <param name="globalRowOffset">Global row offset of the pane in the composed frame.</param>
    /// <param name="globalColumnOffset">Global column offset of the pane in the composed frame.</param>
    /// <returns>Number of cell instances written.</returns>
    public static int BuildHighlightQuads(
        IReadOnlyList<SearchMatch> matches,
        int visibleRows,
        int visibleCols,
        Span<CellInstance> destination,
        int logicalRowOffset = 0,
        int globalRowOffset = 0,
        int globalColumnOffset = 0)
    {
        if (matches == null || matches.Count == 0 || destination.IsEmpty
            || visibleRows <= 0 || visibleCols <= 0)
        {
            return 0;
        }

        int written = 0;

        for (int m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            long visibleRow = (long)match.Row + logicalRowOffset;

            // Match rows are logical rows. Translate them into the pane viewport
            // before clipping, then place the surviving cells globally.
            if (visibleRow < 0 || visibleRow >= visibleRows)
                continue;

            int startCol = Math.Max(0, match.StartCol);
            int endCol = Math.Min(visibleCols, match.EndCol);

            if (startCol >= endCol)
                continue;

            var bg = match.IsActive ? ActiveMatchBackground : MatchBackground;
            var fg = match.IsActive ? ActiveMatchForeground : MatchForeground;
            int targetRow = checked((int)visibleRow + globalRowOffset);

            for (int col = startCol; col < endCol; col++)
            {
                if (written >= destination.Length)
                    return written;

                destination[written++] = new CellInstance
                {
                    Col = checked((ushort)(col + globalColumnOffset)),
                    Row = checked((ushort)targetRow),
                    FgR = fg.R,
                    FgG = fg.G,
                    FgB = fg.B,
                    FgA = 255,
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

    /// <summary>Pixel-bounded search chrome and clipped text/vector icons.</summary>
    public static int BuildOverlayQuads(
        in SearchOverlayLayout layout, float cellWidth, float cellHeight, GlyphAtlas atlas,
        SKTypeface typeface, float textSize, Span<CellInstance> destination,
        Span<ChromeQuadInstance> chromeDestination, out int chromeWritten,
        float scale = 1f)
    {
        chromeWritten = 0;
        if (cellWidth <= 0 || cellHeight <= 0 || destination.IsEmpty || layout.Width <= 0 || layout.Height <= 0)
            return 0;
        int written = 0;
        var panel = new SKRect(layout.X, layout.Y, layout.X + layout.Width, layout.Y + layout.Height);
        EmitOverlayBox(chromeDestination, ref chromeWritten, panel, OverlayBoxBg);
        var input = ToRect(layout.InputBoxRect);
        EmitOverlayBox(chromeDestination, ref chromeWritten, input, OverlayInputBg);
        EmitUiText(destination, ref written, layout.Query, input, panel, 0xFFCCCCCC,
            atlas, typeface, textSize, cellWidth, cellHeight);
        EmitUiText(destination, ref written, layout.MatchBadgeText, ToRect(layout.MatchCountRect),
            panel, 0xFF969696, atlas, typeface, textSize, cellWidth, cellHeight);
        EmitButton(destination, ref written, chromeDestination, ref chromeWritten,
            layout.PrevButtonRect, UiIcon.ChevronUp, panel, atlas, cellWidth, cellHeight, scale);
        EmitButton(destination, ref written, chromeDestination, ref chromeWritten,
            layout.NextButtonRect, UiIcon.ChevronDown, panel, atlas, cellWidth, cellHeight, scale);
        EmitButton(destination, ref written, chromeDestination, ref chromeWritten,
            layout.CloseButtonRect, UiIcon.Close, panel, atlas, cellWidth, cellHeight, scale);
        return written;
    }

    private static SKRect ToRect(OverlayRect rect) =>
        new(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);

    private static void EmitButton(Span<CellInstance> destination, ref int written,
        Span<ChromeQuadInstance> chromeDestination, ref int chromeWritten, OverlayRect rect,
        UiIcon icon, SKRect clip, GlyphAtlas atlas, float cellWidth, float cellHeight,
        float scale)
    {
        var box = ToRect(rect);
        EmitOverlayBox(chromeDestination, ref chromeWritten, box, OverlayButtonBg);
        EmitUiIcon(destination, ref written, icon, box, clip, 0xFFCCCCCC, atlas,
            cellWidth, cellHeight, 16f * scale);
    }

    private static void EmitOverlayBox(Span<ChromeQuadInstance> destination, ref int written,
        SKRect rect, SgrColorArgb color)
    {
        EmitChrome(destination, ref written, new ChromeQuadInstance
        {
            X = rect.Left,
            Y = rect.Top,
            W = rect.Width,
            H = rect.Height,
            TopR = color.R / 255f,
            TopG = color.G / 255f,
            TopB = color.B / 255f,
            TopA = 1f,
            BottomR = color.R / 255f,
            BottomG = color.G / 255f,
            BottomB = color.B / 255f,
            BottomA = 1f
        });
    }
}
