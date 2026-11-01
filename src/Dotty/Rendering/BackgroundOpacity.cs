using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Background-only window opacity (<c>window.opacity</c> × theme <c>opacity</c>). Only the default
/// background (clear colour and default-background pane fills) is translucent; text, explicit cell
/// backgrounds, selection, cursor, chrome and images stay opaque.
/// </summary>
internal static class BackgroundOpacity
{
    /// <summary>
    /// Combined background alpha, 0-255. <paramref name="configOpacity"/> is 0-1 (clamped; non-finite
    /// means opaque); <paramref name="themeOpacity"/> is the theme's 0-100 percentage (clamped to 100).
    /// </summary>
    public static byte Resolve(double configOpacity, byte themeOpacity)
    {
        double config = double.IsFinite(configOpacity) ? Math.Clamp(configOpacity, 0d, 1d) : 1d;
        double theme = Math.Min((int)themeOpacity, 100) / 100d;
        return (byte)Math.Round(config * theme * 255d, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// True when the window must be created with an alpha framebuffer. This is a creation-time GLFW hint,
    /// so a window created opaque stays opaque until restart.
    /// </summary>
    public static bool RequiresAlphaFramebuffer(double configOpacity, byte themeOpacity) =>
        Resolve(configOpacity, themeOpacity) < 255;

    /// <summary>
    /// Premultiplied GL clear colour (each channel 0-1) for <paramref name="color"/> at <paramref name="alpha"/>.
    /// The renderer blends with <c>(One, OneMinusSrcAlpha)</c>, so the framebuffer holds premultiplied colour.
    /// </summary>
    public static (float R, float G, float B, float A) PremultipliedClear(SgrColorArgb color, byte alpha)
    {
        float a = alpha / 255f;
        return (color.R / 255f * a, color.G / 255f * a, color.B / 255f * a, a);
    }
}
