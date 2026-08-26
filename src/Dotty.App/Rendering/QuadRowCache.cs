using System;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Per-row quad cache for the GPU glyph path (WezTerm's line_quad_cache
/// pattern, adapted to ring-scroll). One entry per visible grid row holding
/// the row's emitted vertices in row-local coordinates (Y relative to the
/// row top), validated by the row's identity generation.
///
/// Scroll reuse: a pure scroll moves content between logical rows and bumps
/// every generation exactly once, so the composer shifts entries down by the
/// scroll delta (see TerminalFrameComposer.TryShiftCachesOnScroll) and only
/// the exposed bottom band rebuilds. Entries also reset on geometry changes
/// (columns / cell size); rows containing slow-blink cells are never cached
/// (wall-clock dependent visibility).
///
/// Render-thread confined: the lease-path draw operation serializes all
/// access; no locking.
/// </summary>
public sealed class QuadRowCache
{
    /// <summary>Per-row emitted vertices, Y relative to the row top.</summary>
    public struct Entry
    {
        public bool Valid;
        public ulong Generation;
        public ulong ContentHash;
        public SKPoint[] GlyphPos = Array.Empty<SKPoint>();
        public SKPoint[] GlyphUv = Array.Empty<SKPoint>();
        public SKColor[] GlyphCol = Array.Empty<SKColor>();
        public int GlyphCount;
        public SKPoint[] SolidPos = Array.Empty<SKPoint>();
        public SKColor[] SolidCol = Array.Empty<SKColor>();
        public int SolidCount;

        public Entry() { }
    }

    private Entry[] _entries = Array.Empty<Entry>();
    private int _rows;
    private int _columns;
    private float _cellW;
    private float _cellH;

    // Diagnostics (test + telemetry surface).
    public long Hits;
    public long Misses;
    public long ShiftedRows;

    public void EnsureGeometry(int rows, int columns, float cellW, float cellH)
    {
        if (_rows == rows && _columns == columns && _cellW.Equals(cellW) && _cellH.Equals(cellH)) return;
        _rows = rows;
        _columns = columns;
        _cellW = cellW;
        _cellH = cellH;
        if (_entries.Length < rows) _entries = new Entry[rows];
        Array.Clear(_entries, 0, rows);
    }

    public void Reset()
    {
        Array.Clear(_entries, 0, _entries.Length);
        Hits = Misses = ShiftedRows = 0;
    }

    /// <summary>Ref accessor for in-place entry mutation (render thread only).</summary>
    public ref Entry GetEntryRef(int row) => ref _entries[row];

    /// <summary>Marks every entry invalid (generations are re-stamped on rebuild).</summary>
    public void InvalidateAll() => Array.Clear(_entries, 0, _entries.Length);

    public void InvalidateRow(int row)
    {
        if ((uint)row < (uint)_entries.Length) _entries[row].Valid = false;
    }

    /// <summary>
    /// Shifts entries to follow a scroll: content moved up, so new row r
    /// shows what row r+delta showed (entry[r] = old entry[r+delta]). The
    /// exposed bottom band is invalidated. Caller must have validated content
    /// identity via generation relationships before calling.
    /// <paramref name="generations"/> supplies the current per-row identity:
    /// shifted entries are re-stamped so the next frame's equality check
    /// (entry.Generation == row generation) hits — the scroll bumped every
    /// generation, and the moved entry would otherwise be permanently one
    /// bump behind.
    /// </summary>
    public void ShiftUp(int delta, ReadOnlySpan<ulong> generations)
    {
        if (delta <= 0 || delta > _rows) return;
        int survive = _rows - delta;
        for (int r = 0; r < survive; r++)
        {
            _entries[r] = _entries[r + delta];
            if (r < generations.Length) _entries[r].Generation = generations[r];
        }
        Array.Clear(_entries, Math.Max(0, survive), delta);
        ShiftedRows += survive;
    }

    public static void Ensure<T>(ref T[] arr, int needed)
    {
        // Entry arrays start null: `new Entry[rows]` yields default structs,
        // struct field initializers do not run for array elements.
        if (arr == null || arr.Length < needed)
            arr = new T[Math.Max(needed, 16)];
    }

    public void AddGlyphQuad(ref Entry e, float x, float yRel, float w, float h, SKRect uv, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref e.GlyphPos, e.GlyphCount + 4);
        Ensure(ref e.GlyphUv, e.GlyphCount + 4);
        Ensure(ref e.GlyphCol, e.GlyphCount + 4);

        float u0 = uv.Left, v0 = uv.Top, u1 = uv.Right, v1 = uv.Bottom;
        int i = e.GlyphCount;
        e.GlyphPos[i] = new SKPoint(x, yRel);
        e.GlyphUv[i] = new SKPoint(u0, v0);
        e.GlyphCol[i] = color;
        e.GlyphPos[i + 1] = new SKPoint(x + w, yRel);
        e.GlyphUv[i + 1] = new SKPoint(u1, v0);
        e.GlyphCol[i + 1] = color;
        e.GlyphPos[i + 2] = new SKPoint(x + w, yRel + h);
        e.GlyphUv[i + 2] = new SKPoint(u1, v1);
        e.GlyphCol[i + 2] = color;
        e.GlyphPos[i + 3] = new SKPoint(x, yRel + h);
        e.GlyphUv[i + 3] = new SKPoint(u0, v1);
        e.GlyphCol[i + 3] = color;
        e.GlyphCount += 4;
    }

    public void AddSolidQuad(ref Entry e, float x, float yRel, float w, float h, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref e.SolidPos, e.SolidCount + 4);
        Ensure(ref e.SolidCol, e.SolidCount + 4);

        int i = e.SolidCount;
        e.SolidPos[i] = new SKPoint(x, yRel);
        e.SolidCol[i] = color;
        e.SolidPos[i + 1] = new SKPoint(x + w, yRel);
        e.SolidCol[i + 1] = color;
        e.SolidPos[i + 2] = new SKPoint(x + w, yRel + h);
        e.SolidCol[i + 2] = color;
        e.SolidPos[i + 3] = new SKPoint(x, yRel + h);
        e.SolidCol[i + 3] = color;
        e.SolidCount += 4;
    }
}
