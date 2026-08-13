using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Shared glyph atlas service that manages font caches across all terminal instances.
/// This reduces memory usage by sharing glyph atlases between tabs instead of duplicating them.
/// </summary>
public static class GlyphAtlasService
{
    // Key: (TypefaceName, TextSize, rasterizationOptions hash)
    private static readonly Dictionary<string, GlyphAtlas> _atlases = new();
    private static readonly object _lock = new();
    private static long _stamp;

    /// <summary>
    /// Retained-memory budget for the atlas cache. At or above this, releasing
    /// an unreferenced atlas evicts least-recently-used entries. A single
    /// 2048x2048 RGBA atlas is 16 MB; the budget holds a few font/scale
    /// configurations before eviction starts.
    /// </summary>
    public const long MaxTotalBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Gets or creates a shared glyph atlas for the given font configuration.
    /// Multiple terminals with the same font settings will share the same atlas.
    /// The caller must pair this with <see cref="AcquireAtlas"/> when it starts
    /// using the atlas and <see cref="ReleaseAtlas"/> when it stops.
    /// </summary>
    public static GlyphAtlas GetOrCreateAtlas(SKTypeface typeface, float textSize, GlyphRasterizationOptions options)
    {
        if (typeface == null) typeface = SKTypeface.Default;
        if (textSize <= 0) textSize = 12f;

        var key = GenerateKey(typeface, textSize, options);

        lock (_lock)
        {
            if (!_atlases.TryGetValue(key, out var atlas))
            {
                atlas = new GlyphAtlas(typeface, textSize, options);
                atlas.PreloadCommonGlyphs();
                _atlases[key] = atlas;
            }

            Touch(atlas);
            return atlas;
        }
    }

    /// <summary>
    /// Registers a mounted view's reference to an atlas. Call when a canvas
    /// switches to this atlas instance.
    /// </summary>
    public static void AcquireAtlas(GlyphAtlas atlas)
    {
        if (atlas == null) return;
        lock (_lock)
        {
            atlas.ReferenceCount++;
            Touch(atlas);
        }
    }

    /// <summary>
    /// Releases a mounted view's reference. Unreferenced atlases become LRU
    /// eviction candidates; when the cache exceeds the byte budget the least
    /// recently used unreferenced atlases are disposed and removed.
    /// </summary>
    public static void ReleaseAtlas(GlyphAtlas atlas)
    {
        if (atlas == null) return;
        lock (_lock)
        {
            if (atlas.ReferenceCount > 0)
            {
                atlas.ReferenceCount--;
            }

            EvictIfOverBudget();
        }
    }

    /// <summary>
    /// Clears all shared atlases and releases their memory.
    /// Call this when changing global font settings or on application shutdown.
    /// </summary>
    public static void ClearAllAtlases()
    {
        lock (_lock)
        {
            foreach (var atlas in _atlases.Values)
            {
                try { atlas.Dispose(); } catch { }
            }
            _atlases.Clear();
        }
    }

    /// <summary>
    /// Returns the number of currently cached atlases.
    /// </summary>
    public static int AtlasCount
    {
        get
        {
            lock (_lock) return _atlases.Count;
        }
    }

    /// <summary>
    /// Total retained bytes of the cached atlases.
    /// </summary>
    public static long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                long total = 0;
                foreach (var atlas in _atlases.Values)
                {
                    total += atlas.SizeBytes;
                }
                return total;
            }
        }
    }

    private static void Touch(GlyphAtlas atlas)
    {
        atlas.LastUsedStamp = ++_stamp;
    }

    private static void EvictIfOverBudget()
    {
        long total = 0;
        foreach (var atlas in _atlases.Values)
        {
            total += atlas.SizeBytes;
        }

        if (total < MaxTotalBytes)
        {
            return;
        }

        // Collect unreferenced atlases and evict least-recently-used until
        // under budget. Referenced atlases (mounted views) are never evicted.
        var candidates = new List<KeyValuePair<string, GlyphAtlas>>(_atlases.Count);
        foreach (var kvp in _atlases)
        {
            if (kvp.Value.ReferenceCount == 0)
            {
                candidates.Add(kvp);
            }
        }

        candidates.Sort(static (a, b) => a.Value.LastUsedStamp.CompareTo(b.Value.LastUsedStamp));

        foreach (var candidate in candidates)
        {
            if (total < MaxTotalBytes)
            {
                break;
            }

            long bytes = candidate.Value.SizeBytes;
            _atlases.Remove(candidate.Key);
            try { candidate.Value.Dispose(); } catch { }
            total -= bytes;
        }
    }

    private static string GenerateKey(SKTypeface typeface, float textSize, GlyphRasterizationOptions options)
    {
        // Round text size to avoid creating separate atlases for nearly identical sizes
        var roundedSize = Math.Round(textSize, 1);
        // Use FamilyName as Name property doesn't exist
        var familyName = typeface?.FamilyName ?? "Default";
        return $"{familyName}:{roundedSize:F1}:{options.GetHashCode()}";
    }
}
