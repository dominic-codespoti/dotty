using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Dotty.Terminal.Adapter;

public static class GraphemeHelper
{
    private static readonly string[] s_asciiCache = BuildAsciiCache();
    private static readonly ConcurrentDictionary<uint, string> s_graphemePool = new();
    private static readonly object s_graphemeLock = new();
    private static readonly List<string> s_graphemeList = new() { "" };

    private static string[] BuildAsciiCache()
    {
        var cache = new string[128];
        for (int i = 0; i < cache.Length; i++) cache[i] = ((char)i).ToString();
        return cache;
    }

    public static short StoreGrapheme(string value)
    {
        lock (s_graphemeLock)
        {
            s_graphemeList.Add(value);
            return (short)(s_graphemeList.Count - 1);
        }
    }

    public static string? Resolve(uint rune, short graphemeIndex)
    {
        if (graphemeIndex > 0) return s_graphemeList[graphemeIndex];
        if (rune == 0) return null;
        if (rune < 128) return s_asciiCache[rune];
        if (s_graphemePool.TryGetValue(rune, out var g)) return g;
        var str = char.ConvertFromUtf32((int)rune);
        s_graphemePool[rune] = str;
        return str;
    }
}
