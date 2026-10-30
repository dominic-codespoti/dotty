using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Value identity of a typeface for metric caching: two wrapper objects for the same face
/// (same family name and style) measure identically and share cache entries.
/// </summary>
internal sealed record TypefaceIdentity(string FamilyName, int Weight, int Width, SKFontStyleSlant Slant);

/// <summary>Cache key: face identity plus the already-clamped measurement inputs.</summary>
internal readonly record struct CellMetricsKey(TypefaceIdentity Face, float FontSize, double LineHeight, float Scale);

/// <summary>Logical cell size produced by <see cref="FontMetricsService.MeasureCell(SKTypeface, float, double, float)"/>.</summary>
internal readonly record struct CellMetrics(float Width, float Height);

/// <summary>
/// Fixed-capacity least-recently-used map from <see cref="CellMetricsKey"/> to <see cref="CellMetrics"/>.
/// Hits are allocation-free; a miss on a full cache evicts the least recently used entry, so the
/// cache never grows past <see cref="Capacity"/>. Not thread-safe: callers synchronize.
/// </summary>
internal sealed class CellMetricsCache
{
    private struct Entry
    {
        public CellMetrics Metrics;
        public long LastUse;
    }

    private readonly Dictionary<CellMetricsKey, Entry> _entries;
    private readonly int _capacity;
    private long _clock;

    public CellMetricsCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Dictionary<CellMetricsKey, Entry>(capacity);
    }

    public int Capacity => _capacity;

    public int Count => _entries.Count;

    public bool TryGet(in CellMetricsKey key, out CellMetrics metrics)
    {
        ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(_entries, key);
        if (Unsafe.IsNullRef(ref entry))
        {
            metrics = default;
            return false;
        }

        entry.LastUse = ++_clock;
        metrics = entry.Metrics;
        return true;
    }

    public void Set(in CellMetricsKey key, CellMetrics metrics)
    {
        if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
            EvictLeastRecentlyUsed();

        _entries[key] = new Entry { Metrics = metrics, LastUse = ++_clock };
    }

    private void EvictLeastRecentlyUsed()
    {
        CellMetricsKey oldestKey = default;
        long oldestUse = long.MaxValue;
        foreach (var pair in _entries)
        {
            if (pair.Value.LastUse < oldestUse)
            {
                oldestUse = pair.Value.LastUse;
                oldestKey = pair.Key;
            }
        }

        _entries.Remove(oldestKey);
    }
}
