using System;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Rendering;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

namespace Dotty.Runtime.Scrollbar;

/// <summary>
/// Builds pixel-precise rounded chrome for an in-window scrollbar indicator.
/// The scrollbar remains inside the pane's right edge so the input controller's
/// existing right-edge hit strip continues to map clicks and drags unchanged.
/// </summary>
public static class ScrollbarQuadBuilder
{
    /// <summary>
    /// Builds at most two chrome quads: an idle thumb, or an emphasized track
    /// and accent thumb. Scrollback offset zero is the bottom of the track;
    /// increasing offsets move the thumb toward the top.
    /// </summary>
    public static int Build(
        float paneX,
        float paneY,
        float paneWidth,
        float paneHeight,
        int scrollbackCount,
        int scrollOffset,
        float cellHeight,
        IColorScheme theme,
        Span<ChromeQuadInstance> destination,
        bool isHoveredOrDragging = false)
    {
        ArgumentNullException.ThrowIfNull(theme);

        if (scrollbackCount <= 0 || paneWidth <= 0f || paneHeight <= 0f || destination.IsEmpty)
            return 0;

        ChromePalette palette = ResolvePalette(theme);
        ChromeMetrics metrics = ResolveMetrics(cellHeight);
        float scale = metrics.Scale;

        // The input strip is max(cell width, 14px), while the visual chrome is
        // deliberately narrower and inset so every hit still lands in the
        // established right-edge target.
        float thumbWidth = isHoveredOrDragging ? 8f * scale : 4f * scale;
        float rightInset = 3f * scale;
        float thumbX = paneX + paneWidth - rightInset - thumbWidth;

        float viewportRows = cellHeight > 0f ? paneHeight / cellHeight : paneHeight;
        float totalRows = Math.Max(1f, scrollbackCount + viewportRows);
        float thumbHeight = Math.Clamp(
            paneHeight * viewportRows / totalRows,
            Math.Min(paneHeight, Math.Max(18f * scale, metrics.RadiusLarge * 2f)),
            paneHeight);
        float availableTrack = Math.Max(0f, paneHeight - thumbHeight);
        float progress = 1f - Math.Clamp((float)scrollOffset / scrollbackCount, 0f, 1f);
        float thumbY = paneY + progress * availableTrack;

        int written = 0;
        if (isHoveredOrDragging)
        {
            float trackWidth = 10f * scale;
            float trackX = paneX + paneWidth - rightInset - trackWidth;
            var (trackR, trackG, trackB, trackA) = ToFloatColor(Mix(palette.Rail, palette.SurfaceRaised, 0.55f), 0.72f);
            EmitChrome(destination, ref written, new ChromeQuadInstance
            {
                X = trackX,
                Y = paneY,
                W = trackWidth,
                H = paneHeight,
                Radius = Math.Min(metrics.RadiusSmall, trackWidth * 0.5f),
                Blur = 0f,
                TopR = trackR,
                TopG = trackG,
                TopB = trackB,
                TopA = trackA,
                BottomR = trackR,
                BottomG = trackG,
                BottomB = trackB,
                BottomA = trackA,
            });
        }

        uint thumbColor = isHoveredOrDragging ? palette.Accent : palette.TextSecondary;
        float thumbAlpha = isHoveredOrDragging ? 1f : (scrollOffset > 0 ? 0.82f : 0.56f);
        var (thumbR, thumbG, thumbB, _) = ToFloatColor(thumbColor, 1f);
        EmitChrome(destination, ref written, new ChromeQuadInstance
        {
            X = thumbX,
            Y = thumbY,
            W = thumbWidth,
            H = thumbHeight,
            Radius = Math.Min(metrics.RadiusSmall, thumbWidth * 0.5f),
            Blur = 0f,
            TopR = thumbR,
            TopG = thumbG,
            TopB = thumbB,
            TopA = thumbAlpha,
            BottomR = thumbR,
            BottomG = thumbG,
            BottomB = thumbB,
            BottomA = thumbAlpha,
        });

        return written;
    }
}
