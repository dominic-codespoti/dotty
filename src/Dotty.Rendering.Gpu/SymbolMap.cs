using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Parses symbol-map entries ("U+2500-U+257F: Family, U+E000: Other") into
/// codepoint ranges with preferred families. Malformed entries are dropped.
/// Resolution order: explicit range match first, then the generic chain.
/// </summary>
public sealed class SymbolMap
{
    public readonly struct Entry
    {
        public readonly int Start;
        public readonly int End;
        public readonly string Family;

        public Entry(int start, int end, string family)
        {
            Start = start;
            End = end;
            Family = family;
        }
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, SKTypeface> _familyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public static SymbolMap Parse(IEnumerable<string>? lines)
    {
        var map = new SymbolMap();
        if (lines == null)
            return map;
        foreach (string line in lines)
            map.AddLine(line);
        return map;
    }

    public void AddLine(string? line)
    {
        if (!TryParseLine(line, out int start, out int end, out string? family) || family == null)
            return;
        lock (_lock)
            _entries.Add(new Entry(start, end, family));
    }

    public bool TryResolveFamily(int codepoint, out string? family)
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (codepoint >= entry.Start && codepoint <= entry.End)
                {
                    family = entry.Family;
                    return true;
                }
            }
        }
        family = null;
        return false;
    }

    public SKTypeface? ResolveTypeface(int codepoint)
    {
        if (!TryResolveFamily(codepoint, out string? family) || string.IsNullOrEmpty(family))
            return null;
        lock (_lock)
        {
            if (_familyCache.TryGetValue(family, out var cached))
                return cached;
        }
        SKTypeface? matched = null;
        try
        {
            matched = SKFontManager.Default.MatchFamily(family);
        }
        catch
        {
            return null;
        }
        if (matched == null)
            return null;
        lock (_lock)
        {
            if (_familyCache.TryGetValue(family, out var existing))
            {
                if (!ReferenceEquals(existing, matched))
                    matched.Dispose();
                return existing;
            }
            _familyCache[family] = matched;
            return matched;
        }
    }

    public static bool TryParseLine(string? line, out int start, out int end, out string? family)
    {
        start = end = 0;
        family = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        int colon = line.IndexOf(':');
        if (colon <= 0)
            return false;
        string range = line.Substring(0, colon).Trim();
        string name = line.Substring(colon + 1).Trim().Trim(',', ';');
        if (name.Length == 0)
            return false;
        // Allow comma-separated families; the first matchable wins at resolve time.
        int comma = name.IndexOf(',');
        if (comma >= 0)
            name = name.Substring(0, comma).Trim();
        if (name.Length == 0)
            return false;
        int dash = range.IndexOf('-');
        string first = dash < 0 ? range : range.Substring(0, dash).Trim();
        string last = dash < 0 ? range : range.Substring(dash + 1).Trim();
        if (!TryParseCodepoint(first, out start))
            return false;
        end = start;
        if (dash >= 0 && !TryParseCodepoint(last, out end))
            return false;
        if (end < start || start < 0 || end > 0x10FFFF)
            return false;
        family = name;
        return true;
    }

    private static bool TryParseCodepoint(string text, out int codepoint)
    {
        codepoint = 0;
        text = text.Trim();
        if (text.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(2);
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(2);
        if (text.Length == 0 || text.Length > 6)
            return false;
        try
        {
            codepoint = Convert.ToInt32(text, 16);
            return codepoint is >= 0 and <= 0x10FFFF;
        }
        catch
        {
            return false;
        }
    }
}
