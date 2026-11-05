using System;

namespace Dotty.Silk.Rendering;

/// <summary>GLFW window-system backend, as reported by <c>glfwGetPlatform</c>.</summary>
public enum DisplayPlatform
{
    Unknown = 0,
    Win32 = 0x00060001,
    Cocoa = 0x00060002,
    Wayland = 0x00060003,
    X11 = 0x00060004,
    Null = 0x00060005,
}

/// <summary>
/// The two scale factors a window needs, which coincide on macOS and Wayland but not on
/// Windows or X11 (where window coordinates already are device pixels).
/// </summary>
/// <param name="ContentScale">
/// Device pixels per logical unit for fonts, cells, padding and chrome (the OS display scale:
/// 1.5 at 150 %). Font pixel size and every configured length are multiplied by this.
/// </param>
/// <param name="WindowUnitsToDevice">
/// Device (framebuffer) pixels per window-coordinate unit: converts pointer positions, IME caret
/// rectangles and client sizes between GLFW window coordinates and the framebuffer.
/// </param>
internal readonly record struct DisplayMetrics(float ContentScale, float WindowUnitsToDevice)
{
    /// <summary>Unscaled metrics: what a 96-DPI display reports.</summary>
    public static DisplayMetrics Identity => new(1f, 1f);

    /// <summary>Converts a window-coordinate length to device pixels.</summary>
    public float WindowToDevice(float windowUnits) => windowUnits * WindowUnitsToDevice;

    /// <summary>Converts a device-pixel length to window coordinates.</summary>
    public float DeviceToWindow(float devicePixels) => devicePixels / WindowUnitsToDevice;
}

/// <summary>
/// Pure selection of <see cref="DisplayMetrics"/> from the facts GLFW reports. The content-scale
/// source differs per platform: Wayland reports the fractional scale exactly (quantised here to
/// the protocol's 1/120 steps), macOS reports a backing framebuffer twice the window size, and
/// Windows/X11 report a framebuffer equal to the window size so only
/// <c>glfwGetWindowContentScale</c> carries the display scale.
/// </summary>
internal static class DisplayScale
{
    public const float MinScale = 0.25f;
    public const float MaxScale = 8f;

    /// <summary>Denominator of the Wayland <c>wp_fractional_scale_v1</c> protocol.</summary>
    public const int WaylandScaleDenominator = 120;

    /// <summary>
    /// Computes the metrics for the current window state. Invalid inputs (minimised 0x0 sizes,
    /// a failed content-scale query returning 0 or NaN) fall back to the framebuffer/window ratio
    /// and then to <paramref name="previous"/>, so a transient bad read never changes the scale.
    /// </summary>
    public static DisplayMetrics Compute(
        DisplayPlatform platform,
        int windowWidth,
        int windowHeight,
        int framebufferWidth,
        int framebufferHeight,
        float contentScaleX,
        float contentScaleY,
        DisplayMetrics previous)
    {
        float previousContent = IsUsable(previous.ContentScale) ? Clamp(previous.ContentScale) : 1f;
        float previousWindow = IsUsable(previous.WindowUnitsToDevice) ? Clamp(previous.WindowUnitsToDevice) : 1f;

        float ratio = FramebufferRatio(windowWidth, windowHeight, framebufferWidth, framebufferHeight);
        float reported = IsUsable(contentScaleX) ? contentScaleX : IsUsable(contentScaleY) ? contentScaleY : float.NaN;

        float windowUnitsToDevice = IsUsable(ratio) ? Clamp(ratio) : previousWindow;
        float content = platform switch
        {
            DisplayPlatform.Cocoa => IsUsable(ratio) ? ratio : reported,
            DisplayPlatform.Wayland => IsUsable(reported) ? reported : ratio,
            _ => IsUsable(reported) ? reported : ratio,
        };
        if (platform == DisplayPlatform.Wayland && IsUsable(content))
            content = QuantizeWayland(content);
        float contentScale = IsUsable(content) ? Clamp(content) : previousContent;
        return new DisplayMetrics(contentScale, windowUnitsToDevice);
    }

    /// <summary>
    /// Rounds a scale to the nearest multiple of 1/120. GLFW computes the Wayland framebuffer as
    /// <c>(width * n + 60) / 120</c>, so the raw framebuffer/window ratio jitters with the window
    /// width; quantising recovers <c>n / 120</c> for any width above 120 window units.
    /// </summary>
    public static float QuantizeWayland(float scale)
    {
        if (!IsUsable(scale))
            return scale;
        double steps = Math.Round(scale * (double)WaylandScaleDenominator, MidpointRounding.AwayFromZero);
        return (float)(Math.Max(1d, steps) / WaylandScaleDenominator);
    }

    /// <summary>True when the two metrics differ enough to require new cell metrics and a new atlas.</summary>
    public static bool ContentScaleChanged(DisplayMetrics a, DisplayMetrics b) =>
        MathF.Abs(a.ContentScale - b.ContentScale) > 1e-4f;

    private static float FramebufferRatio(int windowWidth, int windowHeight, int framebufferWidth, int framebufferHeight)
    {
        if (windowWidth > 0 && framebufferWidth > 0)
            return framebufferWidth / (float)windowWidth;
        if (windowHeight > 0 && framebufferHeight > 0)
            return framebufferHeight / (float)windowHeight;
        return float.NaN;
    }

    private static bool IsUsable(float value) => float.IsFinite(value) && value > 0f;

    private static float Clamp(float value) => Math.Clamp(value, MinScale, MaxScale);
}
