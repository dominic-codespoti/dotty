using System;
using Dotty.App.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class QuadRowCacheTests
{
    [Fact]
    public void EnsureGeometry_resets_entries_on_change()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 5, columns: 10, cellW: 10f, cellH: 20f);

        ref var entry = ref cache.GetEntryRef(0);
        entry.Valid = true;
        entry.Generation = 5;
        cache.AddGlyphQuad(ref entry, 0, 0, 10, 20, new SKRect(0, 0, 1, 1), SKColors.White);

        Assert.True(cache.GetEntryRef(0).Valid);

        // Change cell width -> entries reset
        cache.EnsureGeometry(rows: 5, columns: 10, cellW: 12f, cellH: 20f);

        ref var resetEntry = ref cache.GetEntryRef(0);
        Assert.False(resetEntry.Valid);
        Assert.Equal(0UL, resetEntry.Generation);
        Assert.Equal(0, resetEntry.GlyphCount);
    }

    [Fact]
    public void EnsureGeometry_no_reset_when_same()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 5, columns: 10, cellW: 10f, cellH: 20f);

        ref var entry = ref cache.GetEntryRef(0);
        entry.Valid = true;
        entry.Generation = 5;
        cache.AddGlyphQuad(ref entry, 0, 0, 10, 20, new SKRect(0, 0, 1, 1), SKColors.White);

        // Re-call with identical parameters
        cache.EnsureGeometry(rows: 5, columns: 10, cellW: 10f, cellH: 20f);

        ref var keptEntry = ref cache.GetEntryRef(0);
        Assert.True(keptEntry.Valid);
        Assert.Equal(5UL, keptEntry.Generation);
        Assert.Equal(4, keptEntry.GlyphCount);
    }

    [Fact]
    public void AddGlyphQuad_appends_four_vertices_with_row_local_y()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 1, columns: 10, cellW: 10f, cellH: 20f);

        ref var entry = ref cache.GetEntryRef(0);
        var uv = new SKRect(0.1f, 0.2f, 0.8f, 0.9f);
        var color = new SKColor(255, 0, 128, 255);

        cache.AddGlyphQuad(ref entry, x: 5f, yRel: 3f, w: 10f, h: 10f, uv: uv, color: color);

        Assert.Equal(4, entry.GlyphCount);

        // Vertex 0: (x, yRel)
        Assert.Equal(new SKPoint(5f, 3f), entry.GlyphPos[0]);
        Assert.Equal(new SKPoint(0.1f, 0.2f), entry.GlyphUv[0]);
        Assert.Equal(color, entry.GlyphCol[0]);

        // Vertex 1: (x + w, yRel)
        Assert.Equal(new SKPoint(15f, 3f), entry.GlyphPos[1]);
        Assert.Equal(new SKPoint(0.8f, 0.2f), entry.GlyphUv[1]);
        Assert.Equal(color, entry.GlyphCol[1]);

        // Vertex 2: (x + w, yRel + h)
        Assert.Equal(new SKPoint(15f, 13f), entry.GlyphPos[2]);
        Assert.Equal(new SKPoint(0.8f, 0.9f), entry.GlyphUv[2]);
        Assert.Equal(color, entry.GlyphCol[2]);

        // Vertex 3: (x, yRel + h)
        Assert.Equal(new SKPoint(5f, 13f), entry.GlyphPos[3]);
        Assert.Equal(new SKPoint(0.1f, 0.9f), entry.GlyphUv[3]);
        Assert.Equal(color, entry.GlyphCol[3]);
    }

    [Fact]
    public void AddSolidQuad_appends_four_vertices()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 1, columns: 10, cellW: 10f, cellH: 20f);

        ref var entry = ref cache.GetEntryRef(0);
        var color = new SKColor(0, 255, 0, 255);

        cache.AddSolidQuad(ref entry, x: 2f, yRel: 4f, w: 8f, h: 12f, color: color);

        Assert.Equal(4, entry.SolidCount);

        // Vertex 0: (x, yRel)
        Assert.Equal(new SKPoint(2f, 4f), entry.SolidPos[0]);
        Assert.Equal(color, entry.SolidCol[0]);

        // Vertex 1: (x + w, yRel)
        Assert.Equal(new SKPoint(10f, 4f), entry.SolidPos[1]);
        Assert.Equal(color, entry.SolidCol[1]);

        // Vertex 2: (x + w, yRel + h)
        Assert.Equal(new SKPoint(10f, 16f), entry.SolidPos[2]);
        Assert.Equal(color, entry.SolidCol[2]);

        // Vertex 3: (x, yRel + h)
        Assert.Equal(new SKPoint(2f, 16f), entry.SolidPos[3]);
        Assert.Equal(color, entry.SolidCol[3]);
    }

    [Fact]
    public void ShiftUp_moves_entries_down_and_invalidates_band()
    {
        var cache = new QuadRowCache();
        const int rows = 5;
        cache.EnsureGeometry(rows: rows, columns: 10, cellW: 10f, cellH: 20f);

        for (int r = 0; r < rows; r++)
        {
            ref var e = ref cache.GetEntryRef(r);
            e.Valid = true;
            e.Generation = (ulong)(100 + r);
            cache.AddGlyphQuad(ref e, 0, 0, 10, 20, new SKRect(0, 0, 1, 1), SKColors.White);
        }

        // Post-scroll generations (the scroll bumped every row once).
        var gens = new ulong[] { 200, 201, 202, 203, 204 };
        cache.ShiftUp(2, gens);

        // Terminal scroll: content moves up — new row r = old row r+delta.
        // entry[0] == old entry[2], entry[1] == old entry[3], entry[2] == old entry[4];
        // generations re-stamped from the current per-row identity.
        Assert.True(cache.GetEntryRef(0).Valid);
        Assert.Equal(200UL, cache.GetEntryRef(0).Generation);
        Assert.Equal(4, cache.GetEntryRef(0).GlyphCount);

        Assert.True(cache.GetEntryRef(1).Valid);
        Assert.Equal(201UL, cache.GetEntryRef(1).Generation);
        Assert.Equal(4, cache.GetEntryRef(1).GlyphCount);

        Assert.True(cache.GetEntryRef(2).Valid);
        Assert.Equal(202UL, cache.GetEntryRef(2).Generation);
        Assert.Equal(4, cache.GetEntryRef(2).GlyphCount);

        // Invalidated exposed bottom band (entries 3 and 4)
        Assert.False(cache.GetEntryRef(3).Valid);
        Assert.Equal(0, cache.GetEntryRef(3).GlyphCount);

        Assert.False(cache.GetEntryRef(4).Valid);
        Assert.Equal(0, cache.GetEntryRef(4).GlyphCount);

        Assert.Equal(3L, cache.ShiftedRows);
    }

    [Fact]
    public void ShiftUp_invalid_delta_noop()
    {
        var cache = new QuadRowCache();
        const int rows = 5;
        cache.EnsureGeometry(rows: rows, columns: 10, cellW: 10f, cellH: 20f);

        ref var entry = ref cache.GetEntryRef(0);
        entry.Valid = true;
        entry.Generation = 42UL;

        var gens = new ulong[] { 50, 51, 52, 53, 54 };
        cache.ShiftUp(0, gens);
        Assert.True(cache.GetEntryRef(0).Valid);
        Assert.Equal(42UL, cache.GetEntryRef(0).Generation);
        Assert.Equal(0L, cache.ShiftedRows);

        cache.ShiftUp(-1, gens);
        Assert.True(cache.GetEntryRef(0).Valid);
        Assert.Equal(42UL, cache.GetEntryRef(0).Generation);
        Assert.Equal(0L, cache.ShiftedRows);

        cache.ShiftUp(rows + 1, gens);
        Assert.True(cache.GetEntryRef(0).Valid);
        Assert.Equal(42UL, cache.GetEntryRef(0).Generation);
        Assert.Equal(0L, cache.ShiftedRows);
    }

    [Fact]
    public void InvalidateRow_clears_only_that_row()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 3, columns: 10, cellW: 10f, cellH: 20f);

        for (int r = 0; r < 3; r++)
        {
            ref var e = ref cache.GetEntryRef(r);
            e.Valid = true;
            e.Generation = (ulong)(r + 1);
        }

        cache.InvalidateRow(1);

        Assert.True(cache.GetEntryRef(0).Valid);
        Assert.False(cache.GetEntryRef(1).Valid);
        Assert.True(cache.GetEntryRef(2).Valid);

        // Out-of-bounds invalidation does not throw
        cache.InvalidateRow(99);
        cache.InvalidateRow(-1);
    }

    [Fact]
    public void Rebuild_after_shift_reuses_pooled_arrays()
    {
        var cache = new QuadRowCache();
        cache.EnsureGeometry(rows: 3, columns: 10, cellW: 10f, cellH: 20f);

        for (int r = 0; r < 3; r++)
        {
            ref var e = ref cache.GetEntryRef(r);
            e.Valid = true;
            cache.AddGlyphQuad(ref e, 0, 0, 10, 20, new SKRect(0, 0, 1, 1), SKColors.White);
        }

        var gens = new ulong[] { 10, 11, 12 };
        cache.ShiftUp(1, gens);

        // Content moved up: entry[0] = old entry[1], entry[1] = old entry[2];
        // the exposed bottom band (entry 2) was cleared by ShiftUp.
        ref var entry0 = ref cache.GetEntryRef(0);
        Assert.True(entry0.Valid);
        Assert.Equal(10UL, entry0.Generation);
        Assert.Equal(4, entry0.GlyphCount);

        ref var entry2 = ref cache.GetEntryRef(2);
        Assert.False(entry2.Valid);
        Assert.Equal(0, entry2.GlyphCount);

        // Re-adding quads to the exposed band populates without throwing
        cache.AddGlyphQuad(ref entry2, 5, 5, 10, 20, new SKRect(0, 0, 1, 1), SKColors.Yellow);
        entry2.Valid = true;
        entry2.Generation = 999UL;

        Assert.True(entry2.Valid);
        Assert.Equal(4, entry2.GlyphCount);
        Assert.Equal(new SKPoint(5, 5), entry2.GlyphPos[0]);
    }
}
