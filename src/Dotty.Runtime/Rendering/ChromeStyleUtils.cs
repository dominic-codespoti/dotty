using System;
using Dotty.Rendering.Gpu;
using SkiaSharp;

namespace Dotty.Runtime.Rendering;

/// <summary>
/// Shared color and text-layout helpers for GPU quad builders that render
/// flat, rounded "chrome" UI (tab bar, context menu, ...) via
/// <see cref="ChromeQuadInstance"/> alongside the character-grid glyph pass.
/// Keeping this logic in one place avoids the tab bar, context menu, and
/// similar overlays drifting into inconsistent color math or centering.
/// </summary>
public static class ChromeStyleUtils
{
    public static void ExtractRgb(uint argb, out byte r, out byte g, out byte b)
    {
        r = (byte)((argb >> 16) & 0xFF);
        g = (byte)((argb >> 8) & 0xFF);
        b = (byte)(argb & 0xFF);
    }

    public static uint Darken(uint color, float factor)
    {
        byte a = (byte)((color >> 24) & 0xFF);
        byte r = (byte)(((color >> 16) & 0xFF) * factor);
        byte g = (byte)(((color >> 8) & 0xFF) * factor);
        byte b = (byte)((color & 0xFF) * factor);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public static uint Lighten(uint color, float factor)
    {
        byte a = (byte)((color >> 24) & 0xFF);
        byte r = (byte)Math.Min(255, ((color >> 16) & 0xFF) * factor);
        byte g = (byte)Math.Min(255, ((color >> 8) & 0xFF) * factor);
        byte b = (byte)Math.Min(255, (color & 0xFF) * factor);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public static (float R, float G, float B, float A) ToFloatColor(uint argb, float alpha)
    {
        ExtractRgb(argb, out byte r, out byte g, out byte b);
        return (r / 255f, g / 255f, b / 255f, alpha);
    }

    /// <summary>
    /// Computes a uniform extra Y offset (added to every glyph's OffY within
    /// a single text run) that vertically centers a text row within an
    /// arbitrary-height box, using the font's ascent/descent rather than
    /// per-glyph ink bounds so the whole row shifts as one block and
    /// different strings on the same row line up consistently.
    /// </summary>
    public static float ComputeCenteredOffsetY(SKTypeface typeface, float fontSize, int row, float cellHeight, float boxTop, float boxHeight)
    {
        using var font = new SKFont(typeface, fontSize);
        float ascent = MathF.Abs(font.Metrics.Ascent);
        float descent = MathF.Abs(font.Metrics.Descent);
        float boxCenter = boxTop + boxHeight * 0.5f;
        float naturalBaselineY = row * cellHeight + ascent;
        float targetBaselineY = boxCenter + (ascent - descent) * 0.5f;
        return targetBaselineY - naturalBaselineY;
    }

    /// <summary>Appends a chrome quad if <paramref name="destination"/> has room.</summary>
    public static void EmitChrome(Span<ChromeQuadInstance> destination, ref int written, ChromeQuadInstance quad)
    {
        if (written >= destination.Length) return;
        destination[written++] = quad;
    }
}
