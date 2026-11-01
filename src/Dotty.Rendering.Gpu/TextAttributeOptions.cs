using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Per-frame inputs for SGR attributes that are resolved on the CPU (no shader flag bits):
/// faint (SGR 2) colour mixing and the shared blink phase for SGR 5/6 text.
/// </summary>
/// <param name="FaintOpacity">Weight of the foreground when faint text is mixed toward its background, 0-1.</param>
/// <param name="BlinkVisible">False in the off phase of the shared blink timer: blinking glyphs are not drawn.</param>
/// <param name="BaselineShift">
/// Device pixels the cell baseline sits below the atlas baseline (the half-leading split of extra line
/// height, <c>CellMetrics.BaselineShift</c>); added to every cell glyph's vertical offset.
/// </param>
/// <param name="Sprites">
/// Cell geometry decoration sprites are built against; <c>default</c> (unmeasured) emits no decorations.
/// </param>
/// <param name="BuiltinGlyphs">
/// <c>font.builtinGlyphs</c>: box drawing, blocks, braille, sextants, octants and Powerline come from
/// <see cref="SpriteRasterizer"/> instead of the font (needs valid <paramref name="Sprites"/>).
/// </param>
public readonly record struct TextAttributeOptions(
    float FaintOpacity, bool BlinkVisible, int BaselineShift = 0, CellSpriteMetrics Sprites = default,
    bool BuiltinGlyphs = false)
{
    /// <summary>Default of <c>font.faintOpacity</c>.</summary>
    public const float DefaultFaintOpacity = 0.5f;

    public static TextAttributeOptions Default => new(DefaultFaintOpacity, BlinkVisible: true);

    /// <summary>
    /// <c>text.minimumContrast</c> floor (WCAG ratio) applied to the visible text colour after inverse and faint;
    /// values at or below <see cref="Gpu.MinimumContrast.Off"/> (including <c>default</c>) are off.
    /// </summary>
    public float MinimumContrast { get; init; } = Gpu.MinimumContrast.Off;

    /// <summary>Programming-ligature state (<c>font.ligatures</c>, W4-C11); null draws every cell unshaped.</summary>
    public LigatureRenderer? Ligatures { get; init; }

    /// <summary>Clamps a configured faint opacity to 0-1; non-finite values use <see cref="DefaultFaintOpacity"/>.</summary>
    public static float NormalizeFaintOpacity(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, 0d, 1d) : DefaultFaintOpacity;

    /// <summary>
    /// Faint colour: <paramref name="foreground"/> mixed toward <paramref name="background"/>, keeping
    /// <paramref name="opacity"/> of the foreground. Channels round half away from zero; the foreground alpha is kept.
    /// </summary>
    public static SgrColorArgb MixFaint(SgrColorArgb foreground, SgrColorArgb background, float opacity)
    {
        opacity = float.IsFinite(opacity) ? Math.Clamp(opacity, 0f, 1f) : DefaultFaintOpacity;
        uint r = Mix(foreground.R, background.R, opacity);
        uint g = Mix(foreground.G, background.G, opacity);
        uint b = Mix(foreground.B, background.B, opacity);
        return new SgrColorArgb(((uint)foreground.A << 24) | (r << 16) | (g << 8) | b);
    }

    private static uint Mix(byte foreground, byte background, float opacity) =>
        (uint)MathF.Round(background + (foreground - background) * opacity, MidpointRounding.AwayFromZero);
}
