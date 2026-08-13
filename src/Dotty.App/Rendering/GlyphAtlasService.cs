using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Shared A8 glyph atlas lifecycle (Phase 1). Atlases are keyed by
/// (typeface instance, text size) and shared across views; views hold a
/// reference via <see cref="AcquireAtlas"/> / <see cref="ReleaseAtlas"/>.
/// Unreferenced atlases become LRU eviction candidates once total retained
/// bytes exceed <see cref="MaxTotalBytes"/>; referenced atlases are never
/// evicted. Contrast with the deleted predecessor: budget enforcement that
/// actually disposes, and a lock that covers dispose.
/// </summary>
public static class GlyphAtlasService
{
    /// <summary>
    /// Retained-memory budget. A single 4096x4096 A8 atlas is 16 MB; the
    /// budget holds two large or several small font configurations before
    /// eviction starts.
    /// </summary>
    public const long MaxTotalBytes = 32L * 1024 * 1024;

    private static readonly object _lock = new();
    private static readonly Dictionary<GlyphAtlas, AtlasEntry> _atlases = new(ReferenceEqualityComparer.Instance);
    private static long _stamp;

    private sealed class AtlasEntry
    {
        public required GlyphAtlas Atlas;
        public int RefCount;
        public long Stamp;
    }

    /// <summary>
    /// Gets or creates the shared atlas for the font configuration. The caller
    /// must pair this with <see cref="AcquireAtlas"/> when it starts using the
    /// atlas and <see cref="ReleaseAtlas"/> when it stops.
    /// </summary>
    public static GlyphAtlas GetOrCreateAtlas(SKTypeface typeface, float textSize, int initialSize = 1024)
    {
        lock (_lock)
        {
            foreach (var entry in _atlases.Values)
            {
                if (ReferenceEquals(entry.Atlas.Typeface, typeface) && Math.Abs(entry.Atlas.TextSize - textSize) < 0.01f)
                {
                    entry.Stamp = ++_stamp;
                    return entry.Atlas;
                }
            }

            var atlas = new GlyphAtlas(typeface, textSize, initialSize);
            _atlases[atlas] = new AtlasEntry { Atlas = atlas, RefCount = 0, Stamp = ++_stamp };
            return atlas;
        }
    }

    /// <summary>
    /// Registers a mounted view's reference. Referenced atlases are never evicted.
    /// </summary>
    public static void AcquireAtlas(GlyphAtlas atlas)
    {
        lock (_lock)
        {
            if (_atlases.TryGetValue(atlas, out var entry))
            {
                entry.RefCount++;
                entry.Stamp = ++_stamp;
            }
        }
    }

    /// <summary>
    /// Releases a mounted view's reference. Unreferenced atlases become LRU
    /// eviction candidates; when the cache exceeds the byte budget the least
    /// recently used unreferenced atlases are disposed and removed.
    /// </summary>
    public static void ReleaseAtlas(GlyphAtlas atlas)
    {
        lock (_lock)
        {
            if (!_atlases.TryGetValue(atlas, out var entry)) return;
            entry.RefCount = Math.Max(0, entry.RefCount - 1);
            if (entry.RefCount == 0)
            {
                entry.Stamp = ++_stamp;
                EvictIfOverBudgetLocked();
            }
        }
    }

    /// <summary>
    /// Clears all atlases and releases their memory. Call on shutdown or when
    /// global font settings change.
    /// </summary>
    public static void ClearAllAtlases()
    {
        lock (_lock)
        {
            foreach (var entry in _atlases.Values)
                entry.Atlas.Dispose();
            _atlases.Clear();
        }
    }

    public static int AtlasCount
    {
        get { lock (_lock) return _atlases.Count; }
    }

    public static long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                long total = 0;
                foreach (var entry in _atlases.Values)
                    total += entry.Atlas.SizeBytes;
                return total;
            }
        }
    }

    private static void EvictIfOverBudgetLocked()
    {
        long total = 0;
        foreach (var entry in _atlases.Values)
            total += entry.Atlas.SizeBytes;

        if (total < MaxTotalBytes) return;

        // Evict least-recently-used unreferenced atlases until under budget.
        var candidates = new List<AtlasEntry>();
        foreach (var entry in _atlases.Values)
        {
            if (entry.RefCount == 0)
                candidates.Add(entry);
        }
        candidates.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));

        foreach (var entry in candidates)
        {
            if (total < MaxTotalBytes) break;
            total -= entry.Atlas.SizeBytes;
            entry.Atlas.Dispose();
            _atlases.Remove(entry.Atlas);
        }
    }
}
