using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Rendering;

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

                // Skip continuation cells and empty cells (Rune == 0)
                if (hot.IsContinuation || hot.Rune == 0)
                {
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

                // Resolve style attributes
                ref readonly var style = ref source.GetStyle(hot.StyleId);

                // Look up or rasterize glyph in atlas
                var key = new GlyphKey(grapheme, typeface, textSize, style.Bold);
                int countBefore = atlas.EntryCount;
                bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
                if (atlas.EntryCount > countBefore)
                {
                    dirtyAtlasRows?.Add(r);
                }

                if (!glyphOk)
                {
                    // If glyph could not be rasterized or placed, advance and continue
                    c += hot.Width > 1 ? hot.Width : 1;
                    continue;
                }

                // Resolve colors (taking Inverse into account)
                var effectiveFg = !style.Foreground.IsEmpty ? style.Foreground : defFg;
                var effectiveBg = !style.Background.IsEmpty ? style.Background : defBg;

                if (style.Inverse)
                {
                    (effectiveFg, effectiveBg) = (effectiveBg, effectiveFg);
                }

                byte bgA = !style.Background.IsEmpty || style.Inverse ? (byte)255 : (byte)0;

                // Flags
                byte flags = 0;
                if (style.Bold) flags |= CellFlags.Bold;
                if (hot.Width == 2) flags |= CellFlags.WideCell;
                if (style.Inverse) flags |= CellFlags.InverseVideo;

                if (written < destination.Length)
                {
                    destination[written] = new CellInstance
                    {
                        Col = (ushort)c,
                        Row = (ushort)r,
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
