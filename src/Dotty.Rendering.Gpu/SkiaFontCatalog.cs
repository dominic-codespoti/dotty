using System;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>An installed <see cref="SKTypeface"/> seen through <see cref="IFontFace"/>.</summary>
public sealed class SkiaFontFace : IFontFace
{
    private static readonly ConditionalWeakTable<SKTypeface, SkiaFontFace> Wrappers = new();
    private readonly SKFont _font;
    private readonly Lazy<bool> _isColor;

    private SkiaFontFace(SKTypeface typeface)
    {
        Typeface = typeface;
        FamilyName = typeface.FamilyName ?? string.Empty;
        Weight = typeface.FontWeight;
        IsItalic = typeface.FontSlant != SKFontStyleSlant.Upright;
        // Keep one managed SKFont with this weakly cached face for glyph probes. When this wrapper
        // becomes unreachable, SKFont's finalizer releases its native font; do not dispose it here,
        // because the wrapper has no deterministic lifetime and may still be in use by a caller.
        _font = new SKFont(typeface);
        _isColor = new Lazy<bool>(() => ColorFontTables.IsColorFace(typeface));
    }

    /// <summary>The wrapper of <paramref name="typeface"/>; one wrapper per typeface object.</summary>
    public static SkiaFontFace Wrap(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        return Wrappers.GetValue(typeface, static t => new SkiaFontFace(t));
    }

    public SKTypeface Typeface { get; }
    public string FamilyName { get; }
    public int Weight { get; }
    public bool IsItalic { get; }
    public bool IsColor => _isColor.Value;

    public bool HasGlyph(int codepoint)
    {
        Span<int> codepoints = stackalloc int[1];
        Span<ushort> glyphs = stackalloc ushort[1];
        codepoints[0] = codepoint;
        _font.GetGlyphs(codepoints, glyphs);
        return glyphs[0] != 0;
    }

    public override string ToString() => $"{FamilyName} {Weight}{(IsItalic ? " italic" : string.Empty)}";
}

/// <summary>
/// Colour-face detection by OpenType table tags: a face with <c>CBDT</c>/<c>CBLC</c>, <c>sbix</c>,
/// <c>COLR</c> or <c>SVG </c> can draw intrinsic-colour glyphs; any other face is monochrome and the atlas
/// rasterizes it straight to coverage without the colour probe. The answer is cached per typeface object.
/// </summary>
public static class ColorFontTables
{
    public const uint Cbdt = 0x43424454; // 'CBDT'
    public const uint Cblc = 0x43424C43; // 'CBLC'
    public const uint Sbix = 0x73626978; // 'sbix'
    public const uint Colr = 0x434F4C52; // 'COLR'
    public const uint Svg = 0x53564720;  // 'SVG '

    private static readonly ConditionalWeakTable<SKTypeface, object> Cache = new();
    private static readonly object True = new(), False = new();

    /// <summary>Whether any of <paramref name="tags"/> is a colour glyph table.</summary>
    public static bool ContainsColorTable(ReadOnlySpan<uint> tags)
    {
        foreach (uint tag in tags)
            if (tag is Cbdt or Cblc or Sbix or Colr or Svg)
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="typeface"/> has a colour glyph table. A face whose tables cannot be read counts as colour, so it keeps the probe.</summary>
    public static bool IsColorFace(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        return ReferenceEquals(Cache.GetValue(typeface, static t =>
        {
            try
            {
                return t.TryGetTableTags(out uint[] tags) && !ContainsColorTable(tags) ? False : True;
            }
            catch (Exception)
            {
                return True;
            }
        }), True);
    }

    /// <summary>Four-character tag of <paramref name="text"/> (for tests and diagnostics).</summary>
    public static uint Tag(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != 4)
            throw new ArgumentException("An OpenType tag has four characters.", nameof(text));
        return (uint)text[0] << 24 | (uint)text[1] << 16 | (uint)text[2] << 8 | text[3];
    }
}

/// <summary>Production <see cref="IFontCatalog"/> over <see cref="SKFontManager.Default"/> (fontconfig on Linux).</summary>
public sealed class SkiaFontCatalog : IFontCatalog
{
    public static SkiaFontCatalog Shared { get; } = new();

    private SkiaFontCatalog() { }

    public IFontFace DefaultFace => SkiaFontFace.Wrap(SKTypeface.Default);

    public IFontFace? MatchFamily(string family, FontFaceStyle style)
    {
        if (string.IsNullOrWhiteSpace(family))
            return null;
        try
        {
            using var skStyle = ToSkia(style);
            var typeface = SKFontManager.Default.MatchFamily(family, skStyle);
            return typeface == null ? null : SkiaFontFace.Wrap(typeface);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IFontFace? MatchCharacter(int codepoint, FontFaceStyle style)
    {
        if (codepoint <= 0)
            return null;
        try
        {
            using var skStyle = ToSkia(style);
            var typeface = SKFontManager.Default.MatchCharacter(null, skStyle, null, codepoint);
            return typeface == null ? null : SkiaFontFace.Wrap(typeface);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKFontStyle ToSkia(FontFaceStyle style) =>
        new(style.Weight, (int)SKFontStyleWidth.Normal, style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
}
