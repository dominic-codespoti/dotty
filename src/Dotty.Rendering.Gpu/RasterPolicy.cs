using System;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>Outline hinting strength (<c>font.hinting</c>). <see cref="Full"/> is zero so a default policy is today's raster.</summary>
public enum FontHinting : byte
{
    Full = 0,
    Normal,
    Slight,
    None,
}

/// <summary>Coverage mode of the A8 atlas (<c>font.antialias</c>). LCD subpixel coverage is out of scope (one channel).</summary>
public enum FontAntialias : byte
{
    Gray = 0,
    None,
}

/// <summary>
/// The one raster policy shared by cell metrics (<c>FontMetricsService</c>) and glyph rasterisation
/// (<see cref="GlyphAtlas"/>), so a measured cell and the glyphs drawn into it come from identically configured
/// fonts. Part of the atlas identity in <see cref="GlyphAtlasService.GetOrCreateAtlas"/>. <c>default</c> is the
/// historical policy: full hinting, gray antialiasing, no subpixel positioning.
/// </summary>
public readonly record struct RasterPolicy(FontHinting Hinting, FontAntialias Antialias)
{
    /// <summary>Full hinting and gray coverage: the raster before <c>font.hinting</c>/<c>font.antialias</c> existed.</summary>
    public static RasterPolicy Default => default;

    /// <summary>Paint-level antialiasing for outline strokes (synthetic bold).</summary>
    public bool IsAntialias => Antialias != FontAntialias.None;

    public SKFontHinting SkiaHinting => Hinting switch
    {
        FontHinting.None => SKFontHinting.None,
        FontHinting.Slight => SKFontHinting.Slight,
        FontHinting.Normal => SKFontHinting.Normal,
        _ => SKFontHinting.Full,
    };

    public SKFontEdging SkiaEdging => Antialias == FontAntialias.None ? SKFontEdging.Alias : SKFontEdging.Antialias;

    /// <summary>
    /// Applies the policy to <paramref name="font"/>. Subpixel stays off: subpixel coverage needs RGB channels the
    /// single-channel atlas cannot hold.
    /// </summary>
    public void ApplyTo(SKFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        font.Subpixel = false;
        font.Hinting = SkiaHinting;
        font.Edging = SkiaEdging;
    }

    /// <summary>Builds the policy from the config strings; unknown or empty values keep the default of that field.</summary>
    public static RasterPolicy FromConfig(string? hinting, string? antialias) => new(ParseHinting(hinting), ParseAntialias(antialias));

    /// <summary><c>none|slight|normal|full</c>, case-insensitive; anything else is <see cref="FontHinting.Full"/>.</summary>
    public static FontHinting ParseHinting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => FontHinting.None,
        "slight" => FontHinting.Slight,
        "normal" => FontHinting.Normal,
        _ => FontHinting.Full,
    };

    /// <summary><c>gray|none</c>, case-insensitive; anything else is <see cref="FontAntialias.Gray"/>.</summary>
    public static FontAntialias ParseAntialias(string? value) =>
        string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? FontAntialias.None : FontAntialias.Gray;
}
