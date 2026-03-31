using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public static class GraphemePool
{
    private static readonly List<char[]> _items = new();
    private static readonly Dictionary<ulong, List<int>> _hashIndex = new();
    private static readonly object _lock = new();

    static GraphemePool()
    {
        // 0 is dummy
        _items.Add(Array.Empty<char>());
    }

    public static TerminalGrapheme GetOrAdd(ReadOnlySpan<char> chars)
    {
        if (chars.Length == 0) return new TerminalGrapheme();
        if (chars.Length == 1) return new TerminalGrapheme(chars[0]);
        if (chars.Length == 2) return new TerminalGrapheme(chars[0], chars[1]);

        ulong hash = ComputeHash(chars);
        
        lock (_lock)
        {
            // O(1) lookup using hash index
            if (_hashIndex.TryGetValue(hash, out var candidates))
            {
                foreach (int idx in candidates)
                {
                    if (chars.SequenceEqual(_items[idx].AsSpan()))
                    {
                        return new TerminalGrapheme(idx, (short)chars.Length);
                    }
                }
            }

            // Not found - add new entry
            int index = _items.Count;
            _items.Add(chars.ToArray());
            
            // Add to hash index
            if (!_hashIndex.TryGetValue(hash, out candidates))
            {
                candidates = new List<int>();
                _hashIndex[hash] = candidates;
            }
            candidates.Add(index);
            
            return new TerminalGrapheme(index, (short)chars.Length);
        }
    }

    private static ulong ComputeHash(ReadOnlySpan<char> chars)
    {
        // FNV-1a 64-bit hash for fast computation
        const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
        const ulong FNV_PRIME = 1099511628211UL;
        
        ulong hash = FNV_OFFSET_BASIS;
        for (int i = 0; i < chars.Length; i++)
        {
            hash ^= chars[i];
            hash *= FNV_PRIME;
        }
        return hash;
    }

    public static void CopyTo(TerminalGrapheme grapheme, Span<char> destination, out int written)
    {
        if (grapheme.Length == 0 || grapheme.PoolIndex == 0)
        {
            written = 0;
            return;
        }

        if (grapheme.PoolIndex < 0)
        {
            if (grapheme.Length >= 1)
            {
                destination[0] = grapheme.C0;
                written = 1;
            }
            else written = 0;

            if (grapheme.Length == 2)
            {
                destination[1] = grapheme.C1;
                written = 2;
            }
        }
        else
        {
            lock (_lock)
            {
                var arr = _items[grapheme.PoolIndex];
                arr.AsSpan().CopyTo(destination);
                written = arr.Length;
            }
        }
    }

    public static int GetSpan(TerminalGrapheme grapheme, Span<char> buffer)
    {
        CopyTo(grapheme, buffer, out int written);
        return written;
    }
}
