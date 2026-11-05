using System;

namespace Dotty.Rendering.Gpu;

/// <summary><c>font.ligatures</c>: when programming ligatures and contextual alternates are shaped.</summary>
public enum LigatureMode : byte
{
    /// <summary>Never shape; every cell draws its own nominal glyph.</summary>
    Never,
    /// <summary>Shape, but the cell under the cursor is drawn unshaped and breaks its run (default).</summary>
    Cursor,
    /// <summary>Shape every candidate run.</summary>
    Always,
}

public static class LigatureModes
{
    /// <summary>Parses <c>never</c>, <c>cursor</c> or <c>always</c> case-insensitively; anything else is <see cref="LigatureMode.Cursor"/>.</summary>
    public static LigatureMode Parse(string? value) =>
        string.Equals(value, "never", StringComparison.OrdinalIgnoreCase) ? LigatureMode.Never
        : string.Equals(value, "always", StringComparison.OrdinalIgnoreCase) ? LigatureMode.Always
        : LigatureMode.Cursor;
}

/// <summary>
/// One cell as seen by <see cref="RunSegmenter"/>: <see cref="Char"/> is the printable ASCII character of a
/// width-1 cell, or 0 for a cell that can never join a run (blank, wide, continuation, non-ASCII, cluster, sprite).
/// <see cref="Face"/> identifies the face/style the cell is drawn with; a change breaks the run. <see cref="PaintGroup"/>
/// keeps glyphs with different foreground paint separate without changing the actual glyph style.
/// </summary>
public readonly record struct SegmentCell(char Char, int Face, byte PaintGroup = 0)
{
    public static readonly SegmentCell None = new('\0', -1);
}

/// <summary>A shaping run: <see cref="Length"/> cells starting at <see cref="Start"/>, all of one face.</summary>
public readonly record struct CellRun(int Start, int Length, int Face);

/// <summary>
/// Pure row segmentation for ligature shaping. A run is a maximal sequence of candidate cells (printable ASCII,
/// width 1) of one face, broken in <see cref="LigatureMode.Cursor"/> mode at the cursor column (the cursor cell
/// itself never joins a run). Only runs of two or more cells that contain two adjacent ASCII punctuation
/// characters are reported, since only those can form a programming ligature.
/// </summary>
public static class RunSegmenter
{
    /// <summary>Printable ASCII other than space: the characters a run may contain.</summary>
    public static bool IsCandidate(uint c) => c >= 0x21 && c <= 0x7E;

    /// <summary>Printable ASCII punctuation/symbols (not letters or digits).</summary>
    public static bool IsPunctuation(uint c) =>
        IsCandidate(c) && !(c >= '0' && c <= '9') && !((c | 0x20) >= 'a' && (c | 0x20) <= 'z');

    /// <summary>Writes the runs of <paramref name="cells"/> into <paramref name="runs"/>; returns the count written.</summary>
    public static int Segment(ReadOnlySpan<SegmentCell> cells, LigatureMode mode, int cursorColumn, Span<CellRun> runs)
    {
        if (mode == LigatureMode.Never)
            return 0;
        int count = 0;
        int i = 0;
        while (i < cells.Length && count < runs.Length)
        {
            if (!Joins(cells, i, mode, cursorColumn))
            {
                i++;
                continue;
            }
            int start = i, face = cells[i].Face;
            bool pair = false;
            i++;
            while (i < cells.Length && Joins(cells, i, mode, cursorColumn)
                && cells[i].Face == face && cells[i].PaintGroup == cells[start].PaintGroup)
            {
                if (!pair && IsPunctuation(cells[i].Char) && IsPunctuation(cells[i - 1].Char))
                    pair = true;
                i++;
            }
            if (pair)
                runs[count++] = new CellRun(start, i - start, face);
        }
        return count;
    }

    private static bool Joins(ReadOnlySpan<SegmentCell> cells, int i, LigatureMode mode, int cursorColumn) =>
        IsCandidate(cells[i].Char) && !(mode == LigatureMode.Cursor && i == cursorColumn);
}
