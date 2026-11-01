using System;

namespace Dotty.Runtime.Panes;

/// <summary>
/// Pure split-ratio maths for divider drags and keyboard resizes: ratios are clamped so each side keeps at least
/// <see cref="MinColumns"/> columns (vertical split) or <see cref="MinRows"/> rows (horizontal split).
/// </summary>
public static class PaneResizeMath
{
    /// <summary>Smallest pane width, in cells, a resize may leave on either side of a vertical divider.</summary>
    public const int MinColumns = 2;

    /// <summary>Smallest pane height, in cells, a resize may leave on either side of a horizontal divider.</summary>
    public const int MinRows = 1;

    /// <summary>Device-pixel distance from a divider that starts a hover (before any scaling).</summary>
    public const float EnterTolerance = 3f;

    /// <summary>Device-pixel distance a hovered divider keeps the hover; larger than <see cref="EnterTolerance"/> so the cursor does not flicker at the edge.</summary>
    public const float KeepTolerance = 7f;

    /// <summary>
    /// Clamps <paramref name="ratio"/> so both sides of a split with <paramref name="available"/> pixels (divider
    /// excluded) keep <paramref name="minPixels"/>. When both minimums cannot fit, the split is centred.
    /// </summary>
    public static float ClampRatio(float ratio, float available, float minPixels)
    {
        if (float.IsNaN(ratio)) ratio = 0.5f;
        if (available <= 0) return Math.Clamp(ratio, 0.01f, 0.99f);
        if (minPixels * 2 >= available) return 0.5f;
        float min = minPixels / available;
        return Math.Clamp(ratio, Math.Max(0.01f, min), Math.Min(0.99f, 1f - min));
    }

    /// <summary>
    /// Ratio that puts the divider's centre under <paramref name="pointer"/>, for a split starting at
    /// <paramref name="start"/> spanning <paramref name="length"/> pixels including a divider of
    /// <paramref name="dividerThickness"/>.
    /// </summary>
    public static float RatioFromPointer(float pointer, float start, float length, float dividerThickness, float minPixels)
    {
        float available = Math.Max(0, length - dividerThickness);
        if (available <= 0) return 0.5f;
        float first = pointer - start - (dividerThickness / 2f);
        return ClampRatio(first / available, available, minPixels);
    }

    /// <summary>Ratio after moving the divider by <paramref name="deltaPixels"/> (positive grows the first child).</summary>
    public static float RatioAfterStep(float ratio, float available, float deltaPixels, float minPixels)
    {
        if (available <= 0) return ClampRatio(ratio, available, minPixels);
        return ClampRatio(((ratio * available) + deltaPixels) / available, available, minPixels);
    }

    /// <summary>Minimum side length in pixels for a split along <paramref name="direction"/>.</summary>
    public static float MinPixels(SplitDirection direction, float cellWidth, float cellHeight) =>
        direction == SplitDirection.Vertical ? MinColumns * Math.Max(0, cellWidth) : MinRows * Math.Max(0, cellHeight);

    /// <summary>True when (x, y) lies within <paramref name="tolerance"/> of <paramref name="divider"/>.</summary>
    public static bool IsNear(PaneRect divider, float x, float y, float tolerance) =>
        divider.Width > 0 && divider.Height > 0 &&
        x >= divider.X - tolerance && x <= divider.Right + tolerance &&
        y >= divider.Y - tolerance && y <= divider.Bottom + tolerance;

    /// <summary>
    /// Hover hysteresis: a divider already hovered stays hovered within the keep band; otherwise the freshly hit
    /// divider (enter band) wins.
    /// </summary>
    public static T? ResolveHover<T>(T? current, bool currentWithinKeep, T? enterHit) where T : class =>
        current != null && currentWithinKeep ? current : enterHit;
}
