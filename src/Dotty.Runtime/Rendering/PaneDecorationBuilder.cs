using System;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Panes;

namespace Dotty.Runtime.Rendering;

/// <summary>
/// Builds the split-pane decorations: one filled quad per <see cref="SplitPaneNode.DividerBounds"/> and, when
/// <c>panes.activeBorder</c> is on and the tab has splits, an accent ring around the active pane. The ring lies in
/// the divider gap that surrounds the pane (the pane rect inflated by the divider thickness, clipped to the
/// terminal area), so it never covers a cell of any pane. A lone pane gets neither. Edges are snapped to whole
/// framebuffer pixels. Pure and allocation-free.
/// </summary>
public static class PaneDecorationBuilder
{
    /// <summary>Largest honoured <c>panes.dividerThickness</c>, in logical pixels.</summary>
    public const double MaxDividerThickness = 32;

    /// <summary>Quads <see cref="Build"/> can write for a tree with <paramref name="leafCount"/> leaves.</summary>
    public static int MaxQuadCount(int leafCount) => leafCount <= 1 ? 0 : leafCount - 1 + 4;

    /// <summary>
    /// Divider thickness in framebuffer pixels: <paramref name="configured"/> logical pixels times
    /// <paramref name="scale"/>, rounded to whole device pixels. Zero, negative or non-finite values give 0 (no gap,
    /// nothing drawn); any positive value gives at least 1 pixel; values above <see cref="MaxDividerThickness"/> are
    /// clamped.
    /// </summary>
    public static float ResolveThickness(double configured, float scale)
    {
        if (!double.IsFinite(configured) || configured <= 0 || !float.IsFinite(scale) || scale <= 0f)
            return 0f;

        double logical = Math.Min(configured, MaxDividerThickness);
        return Math.Max(1f, MathF.Floor((float)(logical * scale) + 0.5f));
    }

    /// <summary>
    /// Writes divider quads (in tree order) followed by the active-pane ring (top, bottom, left, right pieces that
    /// survive clipping). Pane and divider rects are relative to the terminal area whose top-left framebuffer pixel
    /// is (<paramref name="originX"/>, <paramref name="originY"/>). Returns the number of quads written; stops early
    /// when <paramref name="destination"/> is full.
    /// </summary>
    public static int Build(
        PaneNode root,
        PaneRect activeBounds,
        float originX,
        float originY,
        float areaWidth,
        float areaHeight,
        float thickness,
        bool activeBorder,
        uint dividerColor,
        uint activeColor,
        Span<ChromeQuadInstance> destination)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is not SplitPaneNode || thickness <= 0f)
            return 0;

        float clipLeft = Snap(originX);
        float clipTop = Snap(originY);
        float clipRight = Snap(originX + areaWidth);
        float clipBottom = Snap(originY + areaHeight);
        int written = 0;
        AppendDividers(root, originX, originY, clipLeft, clipTop, clipRight, clipBottom, dividerColor, destination, ref written);

        if (!activeBorder)
            return written;

        float left = Snap(originX + activeBounds.X);
        float top = Snap(originY + activeBounds.Y);
        float right = Snap(originX + activeBounds.Right);
        float bottom = Snap(originY + activeBounds.Bottom);
        AppendClipped(left - thickness, top - thickness, right + thickness, top,
            clipLeft, clipTop, clipRight, clipBottom, activeColor, destination, ref written);
        AppendClipped(left - thickness, bottom, right + thickness, bottom + thickness,
            clipLeft, clipTop, clipRight, clipBottom, activeColor, destination, ref written);
        AppendClipped(left - thickness, top, left, bottom,
            clipLeft, clipTop, clipRight, clipBottom, activeColor, destination, ref written);
        AppendClipped(right, top, right + thickness, bottom,
            clipLeft, clipTop, clipRight, clipBottom, activeColor, destination, ref written);
        return written;
    }

    private static void AppendDividers(
        PaneNode node,
        float originX,
        float originY,
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom,
        uint color,
        Span<ChromeQuadInstance> destination,
        ref int written)
    {
        if (node is not SplitPaneNode split)
            return;

        PaneRect divider = split.DividerBounds;
        AppendClipped(
            Snap(originX + divider.X), Snap(originY + divider.Y),
            Snap(originX + divider.Right), Snap(originY + divider.Bottom),
            clipLeft, clipTop, clipRight, clipBottom, color, destination, ref written);
        AppendDividers(split.First, originX, originY, clipLeft, clipTop, clipRight, clipBottom, color, destination, ref written);
        AppendDividers(split.Second, originX, originY, clipLeft, clipTop, clipRight, clipBottom, color, destination, ref written);
    }

    private static void AppendClipped(
        float left,
        float top,
        float right,
        float bottom,
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom,
        uint color,
        Span<ChromeQuadInstance> destination,
        ref int written)
    {
        left = Math.Max(left, clipLeft);
        top = Math.Max(top, clipTop);
        right = Math.Min(right, clipRight);
        bottom = Math.Min(bottom, clipBottom);
        if (right <= left || bottom <= top || written >= destination.Length)
            return;

        var (r, g, b, a) = ChromeStyleUtils.ToFloatColor(color, 1f);
        destination[written++] = new ChromeQuadInstance
        {
            X = left,
            Y = top,
            W = right - left,
            H = bottom - top,
            Radius = 0f,
            Blur = 0f,
            TopR = r,
            TopG = g,
            TopB = b,
            TopA = a,
            BottomR = r,
            BottomG = g,
            BottomB = b,
            BottomA = a,
        };
    }

    // Round half up (not to even) so a rect's snapped width does not depend on its position.
    private static float Snap(float value) => MathF.Floor(value + 0.5f);
}
