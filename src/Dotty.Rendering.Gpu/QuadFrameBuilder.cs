using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
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
    /// Builds cell instances from the given render source into a newly allocated array or destination buffer.
    /// </summary>
    public static QuadFrameBuildResult Build(
        IRenderSource source,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float textSize,
        in FrameGeometry geometry,
        SgrColorArgb? defaultFg = null,
        SgrColorArgb? defaultBg = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);

        int rows = Math.Min(source.Rows, geometry.Rows);
        int cols = Math.Min(source.Columns, geometry.Columns);
        if (rows <= 0 || cols <= 0)
        {
            return new QuadFrameBuildResult(Array.Empty<CellInstance>(), 0, new HashSet<int>());
        }

        // Allocate maximum possible cells for the visible grid
        var instances = new CellInstance[rows * cols];
        var dirtyRows = new HashSet<int>();

        int count = Build(
            source,
            atlas,
            typeface,
            textSize,
            instances.AsSpan(),
            dirtyRows,
            rows,
            cols,
            defaultFg ?? DefaultForeground,
            defaultBg ?? DefaultBackground);

        return new QuadFrameBuildResult(instances, count, dirtyRows);
    }

    /// <summary>
    /// Writes cell instances into the provided destination span.
    /// Returns the number of instances written.
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
        SgrColorArgb? defaultBg = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);

        int rows = maxRows >= 0 ? Math.Min(source.Rows, maxRows) : source.Rows;
        int cols = maxCols >= 0 ? Math.Min(source.Columns, maxCols) : source.Columns;
        if (rows <= 0 || cols <= 0) return 0;

        var defFg = defaultFg ?? DefaultForeground;
        var defBg = defaultBg ?? DefaultBackground;
        int written = 0;

        for (int r = 0; r < rows; r++)
        {
            var cellHotSpan = source.GetRowCells(r);
            var coldSpan = source.GetRowColdCells(r);
            int rowLength = Math.Min(cols, cellHotSpan.Length);
            int initialAtlasCount = atlas.EntryCount;

            int c = 0;
            while (c < rowLength)
            {
                ref readonly var hot = ref cellHotSpan[c];

                // Skip continuation cells. Empty cells emit only when they
                // carry a custom background (pill/segment padding) — a
                // zero-size glyph instance draws just the background quad.
                if (hot.IsContinuation)
                {
                    c++;
                    continue;
                }
                if (hot.Rune == 0)
                {
                    ref readonly var emptyStyle = ref source.GetStyle(hot.StyleId);
                    bool emptyHasBg = !emptyStyle.Background.IsEmpty || emptyStyle.Inverse;
                    if (emptyHasBg && written < destination.Length)
                    {
                        var bg2 = emptyStyle.Inverse ? (emptyStyle.Background.IsEmpty ? defFg : emptyStyle.Background) : emptyStyle.Background;
                        destination[written] = new CellInstance
                        {
                            Col = (ushort)c,
                            Row = (ushort)r,
                            BgR = bg2.R,
                            BgG = bg2.G,
                            BgB = bg2.B,
                            BgA = 255,
                        };
                        written++;
                    }
                    c++;
                    continue;
                }

                // Resolve cold cell data if available
                short graphemeIndex = -1;
                if (c < coldSpan.Length)
                {
                    graphemeIndex = coldSpan[c].GraphemeIndex;
                }

                // Resolve grapheme string
                string? grapheme = GraphemeHelper.Resolve(hot.Rune, graphemeIndex);
                if (string.IsNullOrEmpty(grapheme))
                {
                    c++;
                    continue;
                }

                // Resolve style attributes and colors before glyph lookup so
                // background-only cells can still paint when the grapheme has
                // no visible coverage (for example a styled space).
                ref readonly var style = ref source.GetStyle(hot.StyleId);
                var effectiveFg = !style.Foreground.IsEmpty ? style.Foreground : defFg;
                var effectiveBg = !style.Background.IsEmpty ? style.Background : defBg;
                if (style.Inverse)
                {
                    (effectiveFg, effectiveBg) = (effectiveBg, effectiveFg);
                }

                byte bgA = !style.Background.IsEmpty || style.Inverse ? (byte)255 : (byte)0;

                byte flags = 0;
                if (style.Bold) flags |= CellFlags.Bold;
                if (hot.Width == 2) flags |= CellFlags.WideCell;
                if (style.Inverse) flags |= CellFlags.InverseVideo;
                if (style.Underline) flags |= CellFlags.Underline;
                if (style.Strikethrough) flags |= CellFlags.Strikethrough;
                if (style.Overline) flags |= CellFlags.Overline;

                GlyphInfo glyphInfo = default;
                bool glyphOk = false;
                if (!(grapheme.Length == 1 && char.IsWhiteSpace(grapheme[0])))
                {
                    var key = new GlyphKey(grapheme, typeface, textSize, style.Bold);
                    int countBefore = atlas.EntryCount;
                    glyphOk = atlas.EnsureGlyph(key, out glyphInfo);
                    if (atlas.EntryCount > countBefore)
                    {
                        dirtyAtlasRows?.Add(r);
                    }
                }

                if (!glyphOk)
                {
                    if (bgA != 0 && written < destination.Length)
                    {
                        destination[written] = new CellInstance
                        {
                            Col = (ushort)c,
                            Row = (ushort)r,
                            FgR = effectiveFg.R,
                            FgG = effectiveFg.G,
                            FgB = effectiveFg.B,
                            Flags = flags,
                            BgR = effectiveBg.R,
                            BgG = effectiveBg.G,
                            BgB = effectiveBg.B,
                            BgA = bgA
                        };
                        written++;
                    }

                    c += hot.Width > 1 ? hot.Width : 1;
                    continue;
                }

                if (written < destination.Length)
                {
                    destination[written] = new CellInstance
                    {
                        Col = (ushort)c,
                        Row = (ushort)r,
                        OffX = (short)glyphInfo.LeftBearing,
                        OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing),
                        GlyphX = (short)glyphInfo.X,
                        GlyphY = (short)glyphInfo.Y,
                        GlyphW = (short)glyphInfo.Width,
                        GlyphH = (short)glyphInfo.Height,
                        FgR = effectiveFg.R,
                        FgG = effectiveFg.G,
                        FgB = effectiveFg.B,
                        Flags = flags,
                        BgR = effectiveBg.R,
                        BgG = effectiveBg.G,
                        BgB = effectiveBg.B,
                        BgA = bgA
                    };
                    written++;
                }

                // If wide cell (Width == 2), skip next column
                c += hot.Width > 1 ? hot.Width : 1;
            }
        }

        return written;
    }
}
