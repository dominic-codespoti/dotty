using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

public sealed class ShapedRunCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lru;

    private struct CacheKey : IEquatable<CacheKey>
    {
        public readonly string Text;
        public readonly int TypefaceHash;
        public readonly float TextSize;
        public readonly bool Bold;

        public CacheKey(string text, SKTypeface typeface, float textSize, bool bold)
        {
            Text = text;
            TypefaceHash = typeface.GetHashCode();
            TextSize = textSize;
            Bold = bold;
        }

        public bool Equals(CacheKey other) =>
            Text == other.Text &&
            TypefaceHash == other.TypefaceHash &&
            TextSize.Equals(other.TextSize) &&
            Bold == other.Bold;

        public override bool Equals(object? obj) =>
            obj is CacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Text, TypefaceHash, TextSize, Bold);
    }

    private sealed class CacheEntry
    {
        public CacheKey Key;
        public ShapedRun Run;
        public SKTextBlob? Blob;
    }

    public ShapedRunCache(int maxEntries = 512)
    {
        _maxEntries = maxEntries;
        _map = new Dictionary<CacheKey, LinkedListNode<CacheEntry>>();
        _lru = new LinkedList<CacheEntry>();
    }

    /// <summary>
    /// Looks up the shaped run and, when a previously built text blob is
    /// cached for the same key, returns it in <paramref name="blob"/>.
    /// </summary>
    public bool TryGet(string text, SKTypeface typeface, float textSize, bool bold, out ShapedRun run, out SKTextBlob? blob)
    {
        var key = new CacheKey(text, typeface, textSize, bold);
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            run = node.Value.Run;
            blob = node.Value.Blob;
            return true;
        }
        run = default;
        blob = null;
        return false;
    }

    public void Add(string text, SKTypeface typeface, float textSize, bool bold, ShapedRun run)
    {
        var key = new CacheKey(text, typeface, textSize, bold);

        if (_map.ContainsKey(key))
            return;

        var entry = new CacheEntry { Key = key, Run = run };
        var node = _lru.AddFirst(entry);
        _map[key] = node;

        if (_lru.Count > _maxEntries)
        {
            var last = _lru.Last!;
            last.Value.Blob?.Dispose();
            _map.Remove(last.Value.Key);
            _lru.RemoveLast();
        }
    }

    /// <summary>
    /// Stores the built text blob for an existing shaped-run entry. The cache
    /// takes ownership: eviction and <see cref="Clear"/> dispose it.
    /// </summary>
    public void AddBlob(string text, SKTypeface typeface, float textSize, bool bold, SKTextBlob blob)
    {
        var key = new CacheKey(text, typeface, textSize, bold);
        if (_map.TryGetValue(key, out var node))
        {
            node.Value.Blob?.Dispose();
            node.Value.Blob = blob;
        }
        else
        {
            // The run entry was evicted between shaping and blob build (only
            // possible across frames); the caller retains ownership.
            blob.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var entry in _lru)
            entry.Blob?.Dispose();
        _map.Clear();
        _lru.Clear();
    }
}
