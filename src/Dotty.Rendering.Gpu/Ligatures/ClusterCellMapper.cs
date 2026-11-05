using System;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// One shaped glyph. <see cref="Cluster"/> is the index of the first run character (= cell, runs are ASCII)
/// the glyph belongs to; advances and offsets are in pixels.
/// </summary>
public readonly record struct ShapedGlyph(ushort GlyphId, int Cluster, float XAdvance, float XOffset, float YOffset);

/// <summary>Shapes one run of a single face; the seam that lets tests replace HarfBuzz.</summary>
public interface IRunShaper
{
    /// <summary>
    /// Shapes <paramref name="text"/> with <paramref name="face"/> at <paramref name="textSize"/> pixels applying
    /// <paramref name="features"/> (on top of calt/liga/clig). Writes at most <c>output.Length</c> glyphs and returns
    /// the number of glyphs produced, which may exceed <c>output.Length</c> (the caller then skips the run).
    /// </summary>
    int Shape(ReadOnlySpan<char> text, SKTypeface face, float textSize, ReadOnlySpan<FontFeature> features, Span<ShapedGlyph> output);

    /// <summary>The glyph <paramref name="face"/> maps <paramref name="c"/> to without shaping (0 when missing).</summary>
    ushort GetNominalGlyph(SKTypeface face, char c);
}

/// <summary>How a cell of a shaped run is drawn.</summary>
public enum LigatureCellKind : byte
{
    /// <summary>Not shaped or shaped to its own nominal glyph: the ordinary per-cell path draws it.</summary>
    None,
    /// <summary>First cell of a shaped cluster: draws all of the cluster's glyphs, spanning <see cref="LigatureCell.Span"/> cells.</summary>
    Start,
    /// <summary>Covered by a preceding <see cref="Start"/>: background and decorations only.</summary>
    Continuation,
}

/// <summary>Per-cell result of <see cref="ClusterCellMapper"/>; glyph indices are relative to the run's glyph array.</summary>
public readonly record struct LigatureCell(LigatureCellKind Kind, byte Span, ushort GlyphStart, ushort GlyphCount);

/// <summary>
/// Pure mapping of shaped clusters onto cells. A cluster covers the cells from its cluster value up to the next
/// cluster value (or the run end); its glyphs are drawn together from its first cell and the other cells only
/// draw their background. A one-cell cluster of one glyph equal to the cell's nominal glyph stays
/// <see cref="LigatureCellKind.None"/>; one with a different glyph (a contextual alternate) keeps the 1:1
/// layout but draws the alternate. Clusters that are not strictly increasing, or a cluster outside the run,
/// make the whole run fall back to <see cref="LigatureCellKind.None"/>.
/// </summary>
public static class ClusterCellMapper
{
    /// <summary>Fills <paramref name="cells"/> (one per run cell); returns false (all None) when the run is unusable.</summary>
    public static bool Map(ReadOnlySpan<ShapedGlyph> glyphs, ReadOnlySpan<ushort> nominalGlyphs, Span<LigatureCell> cells)
    {
        int length = cells.Length;
        cells.Clear();
        if (glyphs.IsEmpty || nominalGlyphs.Length < length || length > byte.MaxValue || glyphs.Length > ushort.MaxValue)
            return false;

        int previous = -1;
        for (int g = 0; g < glyphs.Length; g++)
        {
            int cluster = glyphs[g].Cluster;
            if (cluster < previous || cluster < 0 || cluster >= length)
                return false;
            previous = cluster;
        }

        int index = 0;
        while (index < glyphs.Length)
        {
            int cluster = glyphs[index].Cluster;
            int end = index + 1;
            while (end < glyphs.Length && glyphs[end].Cluster == cluster)
                end++;
            int nextCell = end < glyphs.Length ? glyphs[end].Cluster : length;
            int span = nextCell - cluster;
            int count = end - index;
            if (span == 1 && count == 1 && glyphs[index].GlyphId == nominalGlyphs[cluster])
            {
                cells[cluster] = default;
            }
            else
            {
                cells[cluster] = new LigatureCell(LigatureCellKind.Start, (byte)span, (ushort)index, (ushort)count);
                for (int c = cluster + 1; c < nextCell; c++)
                    cells[c] = new LigatureCell(LigatureCellKind.Continuation, 0, 0, 0);
            }
            index = end;
        }
        return true;
    }
}
