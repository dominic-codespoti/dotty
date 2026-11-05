using System;
using System.Collections.Concurrent;

namespace Dotty.Terminal.Adapter;

public static class GraphemeHelper
{
    /// <summary>
    /// Most combining code points a single cell keeps after its base (matches Ghostty 1.4). Excess marks are
    /// dropped, never split across surrogate pairs.
    /// </summary>
    internal const int MaxCombiningCodepointsPerCell = 64;

    /// <summary>Most scalar values (base + combining marks) one cell can hold.</summary>
    internal const int MaxScalarsPerCell = MaxCombiningCodepointsPerCell + 1;

    /// <summary>Most UTF-16 code units a capped cell can hold (every scalar a surrogate pair).</summary>
    internal const int MaxCellChars = MaxScalarsPerCell * 2;

    private static readonly string[] s_asciiCache = BuildAsciiCache();
    private static readonly ConcurrentDictionary<uint, string> s_graphemePool = new();
    private static readonly GraphemeStore s_store = new();

    private static string[] BuildAsciiCache()
    {
        var cache = new string[128];
        for (int i = 0; i < cache.Length; i++) cache[i] = ((char)i).ToString();
        return cache;
    }

    /// <summary>Number of distinct clusters held by the process-wide store (test/diagnostic hook).</summary>
    internal static int StoredGraphemeCount => s_store.Count;

    /// <summary>
    /// Stores a multi-codepoint cluster and returns its index, de-duplicated: identical clusters share one
    /// entry. Returns <c>-1</c> (never a wrapped or aliased index) when the bounded store is full, which
    /// callers treat as "no grapheme" and render the base code point only.
    /// </summary>
    public static short StoreGrapheme(string value) => s_store.Store(value);

    internal static short StoreGrapheme(ReadOnlySpan<char> value) => s_store.Store(value);

    public static string? Resolve(uint rune, short graphemeIndex)
    {
        if (graphemeIndex > 0)
        {
            string? stored = s_store.Get(graphemeIndex);
            if (stored is not null) return stored;
        }
        if (rune == 0) return null;
        if (rune < 128) return s_asciiCache[rune];
        if (s_graphemePool.TryGetValue(rune, out var g)) return g;
        var str = char.ConvertFromUtf32((int)rune);
        s_graphemePool[rune] = str;
        return str;
    }

    /// <summary>
    /// Number of scalar values in <paramref name="text"/>; a valid surrogate pair counts once and a lone
    /// surrogate counts once (as <see cref="System.Text.Rune"/> enumeration does).
    /// </summary>
    internal static int CountScalars(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Length in UTF-16 code units of the longest prefix of <paramref name="text"/> holding at most
    /// <paramref name="maxScalars"/> scalar values; never ends between the halves of a surrogate pair.
    /// </summary>
    internal static int TruncateToScalars(ReadOnlySpan<char> text, int maxScalars)
    {
        int scalars = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (scalars == maxScalars) return i;
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            scalars++;
        }
        return text.Length;
    }
}
