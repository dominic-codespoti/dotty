using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Geometry and visible cell boundaries for terminal rendering.
/// </summary>
public readonly record struct FrameGeometry(
    float CellWidth,
    float CellHeight,
    int Rows,
    int Columns,
    float OffsetX = 0f,
    float OffsetY = 0f);

/// <summary>
/// Result of building a frame: the generated instances and tracking information.
/// </summary>
public sealed class QuadFrameBuildResult
{
    public CellInstance[] Instances { get; }
    public int InstanceCount { get; }
    public HashSet<int> DirtyAtlasRows { get; }

    public QuadFrameBuildResult(CellInstance[] instances, int instanceCount, HashSet<int> dirtyAtlasRows)
    {
        Instances = instances;
        InstanceCount = instanceCount;
        DirtyAtlasRows = dirtyAtlasRows;
    }

    public ReadOnlySpan<CellInstance> AsSpan() => new(Instances, 0, InstanceCount);
}

/// <summary>
/// Pure conversion helper that transforms an <see cref="IRenderSource"/> snapshot
/// into an array of <see cref="CellInstance"/> structs for GPU rendering.
/// </summary>
public static class QuadFrameBuilder
{
    public static readonly SgrColorArgb DefaultForeground = SgrColorArgb.FromRgb(255, 255, 255);
    public static readonly SgrColorArgb DefaultBackground = SgrColorArgb.FromRgb(0, 0, 0);

    /// <summary>
    /// Upper bound of instances one cell emits: its glyph or background instance, an underline sprite and a
    /// strikethrough/overline sprite (separate because SGR 58 colours only the underline).
    /// </summary>
    public const int MaxInstancesPerCell = 3;

    /// <summary>Instance capacity that can never overflow for a <paramref name="rows"/> × <paramref name="cols"/> grid.</summary>
    public static int InstanceBudget(int rows, int cols) =>
        checked(Math.Max(0, rows) * Math.Max(0, cols) * MaxInstancesPerCell);
    /// <summary>
    /// Whole-pixel glyph placement offset, rounded half away from zero. A fallback glyph's baseline offset is
    /// fractional; truncation moved negative offsets down and positive ones up by up to a pixel.
    /// </summary>
    public static short RoundPixelOffset(float offset) =>
        (short)Math.Clamp(MathF.Round(offset, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

    /// <summary>
    /// Builds cell instances from the given render source into a newly allocated array or destination buffer.
    /// </summary>
    public static QuadFrameBuildResult Build(
        IRenderSource source,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float textSize,
        in FrameGeometry geometry,
        SgrColorArgb? defaultFg = null,
        SgrColorArgb? defaultBg = null,
        TextAttributeOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);

        int rows = Math.Min(source.Rows, geometry.Rows);
        int cols = Math.Min(source.Columns, geometry.Columns);
        if (rows <= 0 || cols <= 0)
            return new QuadFrameBuildResult(Array.Empty<CellInstance>(), 0, new HashSet<int>());

        var instances = new CellInstance[InstanceBudget(rows, cols)];
        var dirtyRows = new HashSet<int>();
        int count = Build(source, atlas, typeface, textSize, instances.AsSpan(), dirtyRows,
            rows, cols, defaultFg ?? DefaultForeground, defaultBg ?? DefaultBackground, textOptions);
        return new QuadFrameBuildResult(instances, count, dirtyRows);
    }

    /// <summary>
    /// Writes cell instances into the provided destination span and publishes
    /// glyphs once after the complete batch has been rasterized.
    /// </summary>
    public static int Build(
        IRenderSource source,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float textSize,
        Span<CellInstance> destination,
        HashSet<int>? dirtyAtlasRows = null,
        int maxRows = -1,
        int maxCols = -1,
        SgrColorArgb? defaultFg = null,
        SgrColorArgb? defaultBg = null,
        TextAttributeOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);

        int rows = maxRows >= 0 ? Math.Min(source.Rows, maxRows) : source.Rows;
        int cols = maxCols >= 0 ? Math.Min(source.Columns, maxCols) : source.Columns;
        if (rows <= 0 || cols <= 0)
            return 0;

        var fg = defaultFg ?? DefaultForeground;
        var bg = defaultBg ?? DefaultBackground;
        var options = textOptions ?? TextAttributeOptions.Default;
        int written = 0;
        for (int row = 0; row < rows; row++)
        {
            written += BuildRow(source, row, atlas, typeface, textSize,
                destination[written..], dirtyAtlasRows, cols, fg, bg, in options, out _);
        }

        atlas.PublishPendingGlyphs();
        return written;
    }

    /// <summary>
    /// Builds one logical row into caller-owned storage. The caller may cache
    /// this run and should publish pending glyphs after its whole frame batch.
    /// <paramref name="hasBlinkingText"/> reports SGR 5/6 glyphs, whose cached run must be rebuilt when
    /// <see cref="TextAttributeOptions.BlinkVisible"/> changes.
    /// </summary>
    public static int BuildRow(
        IRenderSource source,
        int row,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float textSize,
        Span<CellInstance> destination,
        HashSet<int>? dirtyAtlasRows,
        int maxCols,
        SgrColorArgb defaultFg,
        SgrColorArgb defaultBg,
        in TextAttributeOptions textOptions,
        out bool hasBlinkingText)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);
        hasBlinkingText = false;
        if ((uint)row >= (uint)source.Rows)
            return 0;

        int cols = maxCols >= 0 ? Math.Min(source.Columns, maxCols) : source.Columns;
        if (cols <= 0)
            return 0;

        var defFg = defaultFg;
        var defBg = defaultBg;
        var cellHotSpan = source.GetRowCells(row);
        var coldSpan = source.GetRowColdCells(row);
        int rowLength = Math.Min(cols, cellHotSpan.Length);
        int written = 0;
        int column = 0;
        while (column < rowLength)
        {
            ref readonly var hot = ref cellHotSpan[column];

            if (hot.IsContinuation)
            {
                column++;
                continue;
            }
            if (hot.Rune == 0)
            {
                ref readonly var emptyStyle = ref source.GetStyle(hot.StyleId);
                bool emptyHasBg = !emptyStyle.Background.IsEmpty || emptyStyle.Inverse;
                if (emptyHasBg && written < destination.Length)
                {
                    var emptyFg = !emptyStyle.Foreground.IsEmpty ? emptyStyle.Foreground : defFg;
                    var emptyBg = !emptyStyle.Background.IsEmpty ? emptyStyle.Background : defBg;
                    byte emptyFlags = 0;
                    if (emptyStyle.Inverse)
                    {
                        (emptyFg, emptyBg) = (emptyBg, emptyFg);
                        emptyFlags |= CellFlags.InverseVideo;
                    }
                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)column,
                        Row = (ushort)row,
                        FgR = emptyFg.R,
                        FgA = emptyFg.A,
                        FgG = emptyFg.G,
                        FgB = emptyFg.B,
                        Flags = emptyFlags,
                        BgR = emptyBg.R,
                        BgG = emptyBg.G,
                        BgB = emptyBg.B,
                        BgA = 255,
                    };
                    written += EmitDecorations(in emptyStyle, column, row, 1, emptyFg, emptyBg, atlas,
                        in textOptions, destination[written..], dirtyAtlasRows);
                }
                column++;
                continue;
            }

            short graphemeIndex = column < coldSpan.Length ? coldSpan[column].GraphemeIndex : (short)-1;
            // U+10EEEE (Unicode-placeholder) cells never draw a glyph, whatever the image store holds: the resolver
            // paints the image there. A blank grapheme keeps the style's background (visible through transparent
            // image pixels) and leaves the row's identity untouched.
            string? grapheme = hot.Rune == PlaceholderTable.PlaceholderRune ? " " : GraphemeHelper.Resolve(hot.Rune, graphemeIndex);
            if (string.IsNullOrEmpty(grapheme))
            {
                column++;
                continue;
            }

            ref readonly var style = ref source.GetStyle(hot.StyleId);
            var effectiveFg = !style.Foreground.IsEmpty ? style.Foreground : defFg;
            var effectiveBg = !style.Background.IsEmpty ? style.Background : defBg;
            if (style.Inverse)
                (effectiveFg, effectiveBg) = (effectiveBg, effectiveFg);
            // Faint mixes the visible text colour toward the visible background (after inverse); decorations follow it.
            if (style.Faint)
                effectiveFg = TextAttributeOptions.MixFaint(effectiveFg, effectiveBg, textOptions.FaintOpacity);
            // Minimum contrast floors the colour that is finally visible, so it sees inverse and faint already applied.
            if (textOptions.MinimumContrast > MinimumContrast.Off)
                effectiveFg = MinimumContrast.Apply(effectiveFg, effectiveBg, textOptions.MinimumContrast);

            byte bgA = !style.Background.IsEmpty || style.Inverse ? (byte)255 : (byte)0;
            byte flags = 0;
            if (style.Bold) flags |= CellFlags.Bold;
            if (hot.Width == 2) flags |= CellFlags.WideCell;
            if (style.Inverse) flags |= CellFlags.InverseVideo;
            bool hasDecoration = HasDecoration(in style);

            GlyphInfo glyphInfo = default;
            bool glyphOk = false;
            bool builtinGlyph = false;
            // Conceal (SGR 8) and the off phase of blink (SGR 5/6) drop the glyph but keep background and decorations.
            if (!style.Invisible && !(grapheme.Length == 1 && char.IsWhiteSpace(grapheme[0])))
            {
                if (style.SlowBlink)
                    hasBlinkingText = true;
                if (!style.SlowBlink || textOptions.BlinkVisible)
                {
                    // Box drawing, blocks, braille, sextants, octants and Powerline come from the sprite rasteriser
                    // (font.builtinGlyphs), sized to the cell span; a combining cluster keeps the font path.
                    if (textOptions.BuiltinGlyphs && graphemeIndex <= 0 && textOptions.Sprites.IsValid
                        && SpriteRasterizer.IsSupported((int)hot.Rune))
                    {
                        var spriteKey = new SpriteKey((int)hot.Rune,
                            textOptions.Sprites.CellWidth * (hot.Width > 1 ? hot.Width : 1), textOptions.Sprites);
                        builtinGlyph = glyphOk = TryEnsureSprite(atlas, spriteKey, out glyphInfo, out bool spriteAdded);
                        if (spriteAdded)
                            dirtyAtlasRows?.Add(row);
                    }
                    if (!glyphOk)
                    {
                        var key = new GlyphKey(grapheme, typeface, textSize, GlyphStyles.From(style.Bold, style.Italic));
                        glyphOk = atlas.EnsureGlyph(key, out glyphInfo, out bool glyphAdded);
                        if (!glyphOk)
                            glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
                        if (glyphAdded)
                            dirtyAtlasRows?.Add(row);
                    }
                }
            }

            if (!glyphOk)
            {
                // A glyph-less cell still needs an instance for its background, and its decorations still draw,
                // which also covers placeholder and space cells.
                if (bgA != 0 && written < destination.Length)
                {
                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)column,
                        Row = (ushort)row,
                        FgR = effectiveFg.R,
                        FgG = effectiveFg.G,
                        FgB = effectiveFg.B,
                        FgA = effectiveFg.A,
                        Flags = flags,
                        BgR = effectiveBg.R,
                        BgG = effectiveBg.G,
                        BgB = effectiveBg.B,
                        BgA = bgA
                    };
                }
                if (hasDecoration)
                {
                    written += EmitDecorations(in style, column, row, hot.Width > 1 ? hot.Width : 1, effectiveFg, effectiveBg,
                        atlas, in textOptions, destination[written..], dirtyAtlasRows);
                }

                column += hot.Width > 1 ? hot.Width : 1;
                continue;
            }

            if (written < destination.Length)
            {
                destination[written++] = new CellInstance
                {
                    Col = (ushort)column,
                    Row = (ushort)row,
                    OffX = (short)glyphInfo.LeftBearing,
                    // Sprites are placed from the cell top; font glyphs from the baseline.
                    OffY = builtinGlyph
                        ? (short)glyphInfo.TopBearing
                        : (short)(RoundPixelOffset(glyphInfo.BaselineOffset + glyphInfo.TopBearing) + textOptions.BaselineShift),
                    GlyphX = (short)glyphInfo.X,
                    GlyphY = (short)glyphInfo.Y,
                    GlyphW = (short)glyphInfo.Width,
                    GlyphH = (short)glyphInfo.Height,
                    FgR = effectiveFg.R,
                    FgG = effectiveFg.G,
                    FgB = effectiveFg.B,
                    FgA = effectiveFg.A,
                    Flags = (byte)(flags | (glyphInfo.IsColor ? CellFlags.ColorGlyph : 0)),
                    BgR = effectiveBg.R,
                    BgG = effectiveBg.G,
                    BgB = effectiveBg.B,
                    BgA = bgA
                };
            }
            if (hasDecoration)
            {
                written += EmitDecorations(in style, column, row, hot.Width > 1 ? hot.Width : 1, effectiveFg, effectiveBg,
                    atlas, in textOptions, destination[written..], dirtyAtlasRows);
            }

            column += hot.Width > 1 ? hot.Width : 1;
        }

        return written;
    }

    private static bool HasDecoration(in CellAttributes style) =>
        style.Underline || style.Strikethrough || style.Overline;

    /// <summary>
    /// Appends the decoration sprites of one cell (spanning <paramref name="span"/> cells) after its main instance:
    /// the underline in the SGR 58 colour (else the text colour), then strikethrough and overline in the text colour.
    /// Returns the number written (0-2); nothing is emitted while the cell metrics are unmeasured.
    /// </summary>
    private static int EmitDecorations(
        in CellAttributes style,
        int column,
        int row,
        int span,
        SgrColorArgb foreground,
        SgrColorArgb background,
        GlyphAtlas atlas,
        in TextAttributeOptions textOptions,
        Span<CellInstance> destination,
        HashSet<int>? dirtyAtlasRows)
    {
        var metrics = textOptions.Sprites;
        if (!metrics.IsValid || !HasDecoration(in style))
            return 0;

        int width = metrics.CellWidth * span;
        int written = 0;
        if (style.Underline)
        {
            var color = foreground;
            if (!style.UnderlineColor.IsEmpty)
            {
                color = style.UnderlineColor;
                if (style.Faint)
                    color = TextAttributeOptions.MixFaint(color, background, textOptions.FaintOpacity);
            }
            written += EmitSprite(atlas, DecorationGeometry.UnderlineCode(style.UnderlineStyle), width, in metrics,
                column, row, color, destination[written..], dirtyAtlasRows);
        }
        if (style.Strikethrough || style.Overline)
        {
            written += EmitSprite(atlas, DecorationGeometry.LinesCode(style.Strikethrough, style.Overline), width,
                in metrics, column, row, foreground, destination[written..], dirtyAtlasRows);
        }
        return written;
    }

    private static int EmitSprite(
        GlyphAtlas atlas,
        int code,
        int width,
        in CellSpriteMetrics metrics,
        int column,
        int row,
        SgrColorArgb color,
        Span<CellInstance> destination,
        HashSet<int>? dirtyAtlasRows)
    {
        if (destination.IsEmpty || !TryEnsureSprite(atlas, new SpriteKey(code, width, metrics), out var info, out bool added))
            return 0;
        if (added)
            dirtyAtlasRows?.Add(row);
        destination[0] = new CellInstance
        {
            Col = (ushort)column,
            Row = (ushort)row,
            OffX = (short)info.LeftBearing,
            OffY = (short)info.TopBearing,
            GlyphX = (short)info.X,
            GlyphY = (short)info.Y,
            GlyphW = (short)info.Width,
            GlyphH = (short)info.Height,
            FgR = color.R,
            FgG = color.G,
            FgB = color.B,
            FgA = color.A,
            Flags = CellFlags.Decoration,
        };
        return 1;
    }

    /// <summary>Returns the atlas entry of a sprite, rasterising and packing it on the first use.</summary>
    public static bool TryEnsureSprite(GlyphAtlas atlas, in SpriteKey key, out GlyphInfo info, out bool added)
    {
        added = false;
        if (atlas.TryGetSprite(key, out info))
            return true;
        SpriteBitmap? bitmap = DecorationGeometry.IsDecorationCode(key.Code)
            ? DecorationGeometry.Rasterize(key.Code, key.Width, key.Metrics)
            : SpriteRasterizer.Rasterize(key.Code, key.Width, key.Metrics.CellHeight, key.Metrics.CellWidth);
        return bitmap != null && atlas.AddSprite(key, bitmap, out info, out added);
    }
}
