using Dotty.Abstractions.Config;
using System;
using Dotty.Rendering.Gpu;
using SkiaSharp;

namespace Dotty.Runtime.Rendering;
/// <summary>
/// Semantic colors shared by terminal chrome. Values are derived from the
/// active terminal theme so tabs, menus, and scrollbars stay coherent without
/// expanding the terminal color-scheme contract.
/// </summary>
public readonly record struct ChromePalette(
    uint Canvas,
    uint Rail,
    uint Surface,
    uint SurfaceRaised,
    uint SurfaceHover,
    uint Border,
    uint Divider,
    uint TextPrimary,
    uint TextSecondary,
    uint TextMuted,
    uint Accent,
    uint AccentSoft,
    uint Shadow,
    uint Danger,
    uint Warning);

/// <summary>Scale-aware geometry tokens for rounded terminal chrome.</summary>
public readonly record struct ChromeMetrics(
    float Scale,
    float RadiusSmall,
    float Radius,
    float RadiusLarge,
    float Hairline,
    float ShadowBlur);


/// <summary>
/// Shared color and text-layout helpers for GPU quad builders that render
/// flat, rounded "chrome" UI (tab bar, context menu, ...) via
/// <see cref="ChromeQuadInstance"/> alongside the character-grid glyph pass.
/// Keeping this logic in one place avoids the tab bar, context menu, and
/// similar overlays drifting into inconsistent color math or centering.
/// </summary>
public static class ChromeStyleUtils
{
    public static ChromePalette ResolvePalette(IColorScheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        uint background = OpaqueOrDefault(theme.Background, 0xFF1E1E1E);
        uint foreground = OpaqueOrDefault(theme.Foreground, 0xFFD4D4D4);
        bool dark = RelativeLuminance(background) < 0.5f;
        uint neutral = dark ? 0xFFFFFFFF : 0xFF000000;
        uint railTarget = dark ? 0xFF000000 : 0xFFFFFFFF;
        uint accent = OpaqueOrDefault(
            theme.AnsiBrightBlue != 0 ? theme.AnsiBrightBlue : theme.AnsiBlue,
            0xFF5E8BFF);
        uint danger = OpaqueOrDefault(
            theme.AnsiBrightRed != 0 ? theme.AnsiBrightRed : theme.AnsiRed,
            0xFFFF5C72);
        uint warning = OpaqueOrDefault(
            theme.AnsiBrightYellow != 0 ? theme.AnsiBrightYellow : theme.AnsiYellow,
            0xFFFFC857);

        uint rail = Mix(background, railTarget, dark ? 0.28f : 0.18f);
        uint surface = Mix(background, neutral, dark ? 0.065f : 0.04f);
        uint raised = Mix(background, neutral, dark ? 0.12f : 0.075f);
        uint hover = Mix(raised, accent, dark ? 0.16f : 0.11f);

        return new ChromePalette(
            Canvas: background,
            Rail: rail,
            Surface: surface,
            SurfaceRaised: raised,
            SurfaceHover: hover,
            Border: Mix(background, neutral, dark ? 0.18f : 0.20f),
            Divider: Mix(background, neutral, dark ? 0.12f : 0.14f),
            TextPrimary: foreground,
            TextSecondary: Mix(foreground, background, 0.30f),
            TextMuted: Mix(foreground, background, 0.52f),
            Accent: accent,
            AccentSoft: Mix(surface, accent, dark ? 0.24f : 0.17f),
            Shadow: dark ? 0xFF000000 : 0xFF18202B,
            Danger: danger,
            Warning: warning);
    }

    public static ChromeMetrics ResolveMetrics(float cellHeight)
    {
        float scale = Math.Clamp(cellHeight > 0f ? cellHeight / 20f : 1f, 0.75f, 2.5f);
        return new ChromeMetrics(
            Scale: scale,
            RadiusSmall: 5f * scale,
            Radius: 8f * scale,
            RadiusLarge: 12f * scale,
            Hairline: Math.Max(1f, MathF.Round(scale)),
            ShadowBlur: 10f * scale);
    }

    public static uint Mix(uint from, uint to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        from = OpaqueOrDefault(from, 0xFF000000);
        to = OpaqueOrDefault(to, 0xFF000000);

        byte a = LerpByte((byte)(from >> 24), (byte)(to >> 24), amount);
        byte r = LerpByte((byte)(from >> 16), (byte)(to >> 16), amount);
        byte g = LerpByte((byte)(from >> 8), (byte)(to >> 8), amount);
        byte b = LerpByte((byte)from, (byte)to, amount);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public static float RelativeLuminance(uint color)
    {
        ExtractRgb(OpaqueOrDefault(color, 0xFF000000), out byte r, out byte g, out byte b);
        return 0.2126f * Linearize(r) + 0.7152f * Linearize(g) + 0.0722f * Linearize(b);
    }

    private static uint OpaqueOrDefault(uint color, uint fallback)
    {
        if (color == 0)
            return fallback;
        return (color & 0xFF000000) == 0 ? color | 0xFF000000 : color;
    }

    private static byte LerpByte(byte from, byte to, float amount) =>
        (byte)Math.Clamp((int)MathF.Round(from + (to - from) * amount), 0, 255);

    private static float Linearize(byte channel)
    {
        float value = channel / 255f;
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

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
