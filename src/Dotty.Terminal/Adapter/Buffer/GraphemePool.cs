using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public static class GraphemePool
{
    private static readonly List<char[]> _items = new();
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

        lock (_lock)
        {
            for (int i = 1; i < _items.Count; i++)
            {
                if (chars.SequenceEqual(_items[i].AsSpan()))
                {
                    return new TerminalGrapheme(i, (short)chars.Length);
                }
            }

            int index = _items.Count;
            _items.Add(chars.ToArray());
            return new TerminalGrapheme(index, (short)chars.Length);
        }
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
