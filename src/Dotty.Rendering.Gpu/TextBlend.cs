using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Rendering.Gpu;

/// <summary>How A8 glyph coverage is turned into blend alpha (<c>text.blending</c>).</summary>
public enum TextBlendMode
{
    /// <summary>Coverage is the alpha of a gamma-space blend: the historical look and the default.</summary>
    Native = 0,

    /// <summary>Alpha chosen so the blended luminance equals a linear-light blend of the foreground and background.</summary>
    Linear = 1,

    /// <summary><see cref="Linear"/> plus a contrast boost, strongest for dark text on a light background.</summary>
    LinearCorrected = 2,
}

/// <summary>
/// CPU reference of the glyph-pass coverage correction. The fragment shader in <c>SilkGlShaders</c> mirrors
/// <see cref="Correct"/> line for line in single precision (uniforms <c>uTextBlend</c>, <c>uTextContrast</c>); the
/// framebuffer then blends <c>fg·a + dst·(1 − a)</c> in gamma space, which <see cref="TextBlend.Blend"/> reproduces.
/// Skia's A8 coverage is linear geometric coverage with no gamma pre-blend (gray antialiasing, no LCD), so the
/// correction is applied once, here.
/// </summary>
public static class CoverageCorrection
{
    /// <summary>Default of <c>text.contrast</c>.</summary>
    public const float DefaultContrast = 0.5f;

    /// <summary>Foreground/background encoded luminance closer than this blends identically in every mode.</summary>
    public const float MinLumaDifference = 1f / 255f;

    /// <summary>Parses <c>text.blending</c>: <c>native|linear|linear-corrected</c>, case-insensitive; anything else is native.</summary>
    public static TextBlendMode ParseMode(string? value)
    {
        var text = value.AsSpan().Trim();
        if (text.Equals("linear", StringComparison.OrdinalIgnoreCase))
            return TextBlendMode.Linear;
        if (text.Equals("linear-corrected", StringComparison.OrdinalIgnoreCase))
            return TextBlendMode.LinearCorrected;
        return TextBlendMode.Native;
    }

    /// <summary>Clamps <c>text.contrast</c> to 0-1; non-finite values use <see cref="DefaultContrast"/>.</summary>
    public static float NormalizeContrast(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, 0d, 1d) : DefaultContrast;

    /// <summary>sRGB transfer function, decode (0-1 → linear light).</summary>
    public static float SrgbToLinear(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>sRGB transfer function, encode (linear light → 0-1).</summary>
    public static float LinearToSrgb(float l)
    {
        l = Math.Clamp(l, 0f, 1f);
        return l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Relative (linear-light) luminance of gamma-encoded 0-1 channels (Rec. 709 / WCAG weights).</summary>
    public static float Luminance(float r, float g, float b) =>
        0.2126f * SrgbToLinear(r) + 0.7152f * SrgbToLinear(g) + 0.0722f * SrgbToLinear(b);

    /// <summary>
    /// Blend alpha for <paramref name="coverage"/> (0-1) of a glyph with linear luminance <paramref name="fgLuma"/> over
    /// a background of linear luminance <paramref name="bgLuma"/>. Native returns the coverage unchanged. Linear solves
    /// <c>pb + a·(pf − pb) = encode(mix(bgLuma, fgLuma, coverage))</c> for <c>a</c>, where <c>pf</c>/<c>pb</c> are the
    /// encoded luminances, so a gamma-space blend lands on the linear-light luminance. LinearCorrected then adds
    /// <c>k·a·(1 − a)</c> with <c>k = contrast · clamp(0.5 + 0.5·(pb − pf))</c>: zero and full coverage stay put.
    /// </summary>
    public static float Correct(TextBlendMode mode, float coverage, float fgLuma, float bgLuma, float contrast)
    {
        if (mode == TextBlendMode.Native)
            return coverage;
        float a = Math.Clamp(coverage, 0f, 1f);
        float pf = LinearToSrgb(fgLuma);
        float pb = LinearToSrgb(bgLuma);
        float d = pf - pb;
        if (MathF.Abs(d) >= MinLumaDifference)
        {
            float target = LinearToSrgb(bgLuma + (fgLuma - bgLuma) * a);
            a = Math.Clamp((target - pb) / d, 0f, 1f);
        }
        if (mode == TextBlendMode.LinearCorrected)
        {
            float k = Math.Clamp(contrast, 0f, 1f) * Math.Clamp(0.5f + 0.5f * (pb - pf), 0f, 1f);
            a = Math.Clamp(a + k * a * (1f - a), 0f, 1f);
        }
        return a;
    }
}

/// <summary>Reference of the whole glyph pixel: <see cref="CoverageCorrection"/> plus the premultiplied framebuffer blend.</summary>
public static class TextBlend
{
    /// <summary>
    /// The opaque framebuffer colour after drawing glyph coverage <paramref name="coverage"/> (0-255, the A8 atlas
    /// value) in <paramref name="foreground"/> over opaque <paramref name="background"/>: the shader's
    /// <c>(fg·a·fgA, a·fgA)</c> output through <c>glBlendFunc(ONE, ONE_MINUS_SRC_ALPHA)</c>, rounded to 8 bits.
    /// </summary>
    public static SgrColorArgb Blend(TextBlendMode mode, SgrColorArgb foreground, SgrColorArgb background, byte coverage, float contrast)
    {
        float fr = foreground.R / 255f, fg = foreground.G / 255f, fb = foreground.B / 255f;
        float br = background.R / 255f, bg = background.G / 255f, bb = background.B / 255f;
        float a = CoverageCorrection.Correct(mode, coverage / 255f,
            CoverageCorrection.Luminance(fr, fg, fb), CoverageCorrection.Luminance(br, bg, bb), contrast);
        a *= foreground.A / 255f;
        return SgrColorArgb.FromRgb(Channel(fr, br, a), Channel(fg, bg, a), Channel(fb, bb, a));
    }

    private static byte Channel(float source, float destination, float alpha) =>
        (byte)MathF.Round(Math.Clamp(source * alpha + destination * (1f - alpha), 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
}
