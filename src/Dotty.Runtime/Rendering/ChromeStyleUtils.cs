using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dotty.Abstractions.Config;
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
    private readonly struct FontCacheKey : IEquatable<FontCacheKey>
    {
        private readonly SKTypeface _typeface;
        private readonly float _size;

        public FontCacheKey(SKTypeface typeface, float size) { _typeface = typeface; _size = size; }
        public bool Equals(FontCacheKey other) => ReferenceEquals(_typeface, other._typeface) && _size.Equals(other._size);
        public override bool Equals(object? obj) => obj is FontCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_typeface), _size);
    }

    private sealed class CachedFont
    {
        public readonly SKFont Font;
        public readonly float Ascent;
        public readonly float Descent;

        public CachedFont(SKTypeface typeface, float size)
        {
            Font = new SKFont(typeface, size)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = false,
                Hinting = SKFontHinting.Full
            };
            var metrics = Font.Metrics;
            Ascent = MathF.Abs(metrics.Ascent);
            Descent = MathF.Abs(metrics.Descent);
        }
    }

    private static readonly object FontCacheLock = new();
    private static readonly Dictionary<FontCacheKey, CachedFont> FontCache = new();

    private static CachedFont GetCachedFont(SKTypeface typeface, float fontSize)
    {
        var key = new FontCacheKey(typeface, fontSize);
        lock (FontCacheLock)
        {
            if (!FontCache.TryGetValue(key, out CachedFont? cached))
            {
                cached = new CachedFont(typeface, fontSize);
                FontCache.Add(key, cached);
            }
            return cached;
        }
    }

    internal static (float Ascent, float Descent) GetFontMetrics(SKTypeface typeface, float fontSize)
    {
        var cached = GetCachedFont(typeface, fontSize);
        return (cached.Ascent, cached.Descent);
    }

    internal static SKFont GetCachedSKFont(SKTypeface typeface, float fontSize) =>
        GetCachedFont(typeface, fontSize).Font;
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
    /// Computes the extra Y offset needed to center a text row vertically in
    /// a box, using cached font ascent and descent so every glyph in the row
    /// shifts together.
    /// </summary>
    public static float ComputeCenteredOffsetY(SKTypeface typeface, float fontSize, int row, float cellHeight, float boxTop, float boxHeight)
    {
        var font = GetCachedFont(typeface, fontSize);
        float boxCenter = boxTop + boxHeight * 0.5f;
        float naturalBaselineY = row * cellHeight + font.Ascent;
        float targetBaselineY = boxCenter + (font.Ascent - font.Descent) * 0.5f;
        return targetBaselineY - naturalBaselineY;
    }

    public static float MeasureUiText(string? text, SKTypeface typeface, float fontSize) =>
        string.IsNullOrEmpty(text) ? 0f : GetCachedSKFont(typeface, fontSize).MeasureText(text);

    /// <summary>Draws proportional UI text, ellipsizing columns and cropping ink to the popup viewport.</summary>
    public static void EmitUiText(Span<CellInstance> destination, ref int written, string? text,
        SKRect box, SKRect clip, uint color, GlyphAtlas atlas, SKTypeface typeface, float fontSize,
        float cellWidth, float cellHeight)
    {
        if (string.IsNullOrEmpty(text) || box.Width <= 0f || box.Height <= 0f) return;
        var font = GetCachedSKFont(typeface, fontSize);
        var metrics = GetFontMetrics(typeface, fontSize);
        float baseline = box.MidY + (metrics.Ascent - metrics.Descent) * 0.5f;
        float usedWidth = 0f;
        const float widthTolerance = 0.01f;
        float ellipsisWidth = font.MeasureText("…");
        bool truncated = font.MeasureText(text) > box.Width + widthTolerance;
        float textWidth = box.Width - (truncated ? ellipsisWidth : 0f);
        var span = text.AsSpan();
        for (int i = 0; i < span.Length;)
        {
            int length = i + 1 < span.Length && char.IsSurrogatePair(span[i], span[i + 1]) ? 2 : 1;
            string glyph = global::Dotty.Runtime.Tabs.TabBarQuadBuilder.GlyphTextCache.Get(span, i, length);
            float advance = font.MeasureText(glyph);
            if (usedWidth + advance > textWidth + widthTolerance) break;
            float x = box.Left + usedWidth;
            if (!char.IsWhiteSpace(span[i]) &&
                (atlas.EnsureGlyph(new GlyphKey(glyph, typeface, fontSize, false), out var info) ||
                 atlas.TryGetFallbackGlyph(out info)))
                EmitUiGlyph(destination, ref written, info, x + info.LeftBearing,
                    baseline + info.TopBearing, IntersectUiRect(box, clip), color,
                    cellWidth, cellHeight);
            usedWidth += advance;
            i += length;
        }
        if (truncated && ellipsisWidth <= box.Width &&
            atlas.EnsureGlyph(new GlyphKey("…", typeface, fontSize, false), out var ellipsis))
            EmitUiGlyph(destination, ref written, ellipsis, box.Left + usedWidth + ellipsis.LeftBearing,
                baseline + ellipsis.TopBearing, IntersectUiRect(box, clip), color,
                cellWidth, cellHeight);
    }

    public static void EmitUiIcon(Span<CellInstance> destination, ref int written, UiIcon icon,
        SKRect box, SKRect clip, uint color, GlyphAtlas atlas, float cellWidth, float cellHeight,
        float maximumSize = 16f)
    {
        float size = MathF.Floor(Math.Min(maximumSize, Math.Min(box.Width, box.Height)));
        if (size <= 0f || icon == UiIcon.None || !atlas.EnsureIcon(icon, size, out var info)) return;
        EmitUiGlyph(destination, ref written, info, box.MidX - info.Width * 0.5f,
            box.MidY - info.Height * 0.5f, IntersectUiRect(box, clip), color,
            cellWidth, cellHeight);
    }

    public static SKRect IntersectUiRect(SKRect a, SKRect b) => new(
        Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    private static void EmitUiGlyph(Span<CellInstance> destination, ref int written, GlyphInfo info,
        float x, float y, SKRect clip, uint color, float cellWidth, float cellHeight)
    {
        if (written >= destination.Length) return;
        int left = (int)MathF.Round(x), top = (int)MathF.Round(y);
        int clippedLeft = Math.Max(left, (int)MathF.Ceiling(clip.Left));
        int clippedTop = Math.Max(top, (int)MathF.Ceiling(clip.Top));
        int right = Math.Min(left + info.Width, (int)MathF.Floor(clip.Right));
        int bottom = Math.Min(top + info.Height, (int)MathF.Floor(clip.Bottom));
        if (right <= clippedLeft || bottom <= clippedTop) return;
        float gridX = clippedLeft, gridY = clippedTop;
        int col = Math.Max(0, (int)MathF.Floor(gridX / cellWidth));
        int row = Math.Max(0, (int)MathF.Floor(gridY / cellHeight));
        ExtractRgb(color, out byte r, out byte g, out byte b);
        destination[written++] = new CellInstance
        {
            Col = (ushort)col,
            Row = (ushort)row,
            OffX = (short)MathF.Round(gridX - col * cellWidth),
            OffY = (short)MathF.Round(gridY - row * cellHeight),
            GlyphX = (short)(info.X + clippedLeft - left),
            GlyphY = (short)(info.Y + clippedTop - top),
            GlyphW = (short)(right - clippedLeft),
            GlyphH = (short)(bottom - clippedTop),
            FgR = r,
            FgG = g,
            FgB = b,
            FgA = 255,
            Flags = info.IsColor ? CellFlags.ColorGlyph : (byte)0
        };
    }

    /// <summary>Appends a chrome quad if <paramref name="destination"/> has room.</summary>
    public static void EmitChrome(Span<ChromeQuadInstance> destination, ref int written, ChromeQuadInstance quad)
    {
        if (written >= destination.Length) return;
        destination[written++] = quad;
    }
}
