using System;

namespace Dotty.Silk.Rendering;

/// <summary>Window padding in whole device pixels.</summary>
internal readonly record struct DevicePadding(int Left, int Top, int Right, int Bottom)
{
    public int Horizontal => Left + Right;

    public int Vertical => Top + Bottom;

    /// <summary>
    /// Converts logical padding (config units) to device pixels: <c>round(logical × contentScale)</c>
    /// with midpoints away from zero, so 14 × 1.25 = 17.5 always becomes 18. Negative or non-finite
    /// values become 0.
    /// </summary>
    public static DevicePadding FromLogical(double left, double top, double right, double bottom, float contentScale) =>
        new(GridFit.ToDevicePixels(left, contentScale),
            GridFit.ToDevicePixels(top, contentScale),
            GridFit.ToDevicePixels(right, contentScale),
            GridFit.ToDevicePixels(bottom, contentScale));
}

/// <summary>
/// Integer-only grid layout: how many whole device-pixel cells fit a framebuffer, and the inverse
/// (the window client size that holds a requested grid). Everything is integer device pixels, so the
/// layout, the renderer and the cell size reported to applications (CSI 14/16 t, kitty graphics)
/// agree exactly.
/// </summary>
internal static class GridFit
{
    /// <summary>Upper bound for a computed client dimension (GLFW and the OS reject absurd sizes).</summary>
    public const int MaxClientUnits = 65_536;

    /// <summary>Rounds a non-negative logical length times <paramref name="contentScale"/> to device pixels.</summary>
    public static int ToDevicePixels(double logical, float contentScale)
    {
        if (!double.IsFinite(logical) || logical <= 0d || !float.IsFinite(contentScale) || contentScale <= 0f)
            return 0;
        double device = Math.Round(logical * contentScale, MidpointRounding.AwayFromZero);
        return (int)Math.Min(device, MaxClientUnits);
    }

    /// <summary>
    /// Columns and rows that fit the framebuffer below <paramref name="barRows"/> tab-bar rows and
    /// inside <paramref name="padding"/>; at least one of each. Satisfies
    /// <c>cols × cellWidth + padding ≤ framebuffer</c> whenever one column fits at all.
    /// </summary>
    public static (int Columns, int Rows) Compute(
        int framebufferWidth, int framebufferHeight, int cellWidth, int cellHeight, int barRows, DevicePadding padding)
    {
        cellWidth = Math.Max(1, cellWidth);
        cellHeight = Math.Max(1, cellHeight);
        int usableWidth = framebufferWidth - padding.Horizontal;
        int usableHeight = framebufferHeight - padding.Vertical - Math.Max(0, barRows) * cellHeight;
        return (Math.Max(1, usableWidth / cellWidth), Math.Max(1, usableHeight / cellHeight));
    }

    /// <summary>
    /// Client size in window units that holds <paramref name="columns"/> × <paramref name="rows"/>
    /// cells plus tab-bar rows and padding. Rounds up, so <see cref="Compute"/> on the resulting
    /// framebuffer returns the same grid. Used for the initial window size, the control-port resize
    /// and CSI 8 t.
    /// </summary>
    public static (int Width, int Height) ClientSize(
        int columns, int rows, int cellWidth, int cellHeight, int barRows, DevicePadding padding, float windowUnitsToDevice)
    {
        float ratio = float.IsFinite(windowUnitsToDevice) && windowUnitsToDevice > 0f ? windowUnitsToDevice : 1f;
        long deviceWidth = (long)Math.Max(1, columns) * Math.Max(1, cellWidth) + padding.Horizontal;
        long deviceHeight = ((long)Math.Max(1, rows) + Math.Max(0, barRows)) * Math.Max(1, cellHeight) + padding.Vertical;
        return (ToWindowUnits(deviceWidth, ratio), ToWindowUnits(deviceHeight, ratio));
    }

    private static int ToWindowUnits(long devicePixels, float ratio)
    {
        // Subtract a tiny epsilon before the ceiling so exact quotients (900 / 1.5) do not round up
        // by float noise; the framebuffer of the result is still at least devicePixels.
        double units = Math.Ceiling(devicePixels / (double)ratio - 1e-6);
        return (int)Math.Clamp(units, 1d, MaxClientUnits);
    }
}
