using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>Per-range font-family preferences. The first installed family wins.</summary>
public sealed class SymbolMap
{
    public readonly struct Entry : IEquatable<Entry>
    {
        public readonly int Start;
        public readonly int End;
        public readonly IReadOnlyList<string> Families;
        public string Family => Families.Count == 0 ? string.Empty : Families[0];

        public Entry(int start, int end, IEnumerable<string> families)
        {
            Start = start;
            End = end;
            Families = Array.AsReadOnly(new List<string>(families).ToArray());
        }

        public bool Equals(Entry other) => Start == other.Start && End == other.End && Families.SequenceEqual(other.Families, StringComparer.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => obj is Entry other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Start);
            hash.Add(End);
            foreach (string family in Families)
                hash.Add(family, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, SKTypeface?> _familyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _entries.Count; } }

    public static SymbolMap Parse(IEnumerable<string>? lines)
    {
        var map = new SymbolMap();
        if (lines != null)
            foreach (string line in lines)
                map.AddLine(line);
        return map;
    }

    public void AddLine(string? line)
    {
        if (!TryParseLine(line, out int start, out int end, out string[] families))
            return;
        lock (_lock)
            _entries.Add(new Entry(start, end, families));
    }

    public bool HasSameEntries(SymbolMap? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other == null) return false;
        Entry[] left, right;
        lock (_lock) left = _entries.ToArray();
        lock (other._lock) right = other._entries.ToArray();
        return left.SequenceEqual(right);
    }

    public bool TryResolveFamily(int codepoint, out string? family)
    {
        lock (_lock)
            foreach (var entry in _entries)
                if (codepoint >= entry.Start && codepoint <= entry.End)
                {
                    family = entry.Family;
                    return true;
                }
        family = null;
        return false;
    }

    public SKTypeface? ResolveTypeface(int codepoint)
    {
        Entry? match = null;
        lock (_lock)
            foreach (var entry in _entries)
                if (codepoint >= entry.Start && codepoint <= entry.End)
                {
                    match = entry;
                    break;
                }
        if (match is not Entry matched) return null;

        foreach (string family in matched.Families)
        {
            lock (_lock)
            {
                if (_familyCache.TryGetValue(family, out var cached))
                {
                    if (cached != null) return cached;
                    continue;
                }
                SKTypeface? typeface;
                try { typeface = SKFontManager.Default.MatchFamily(family); }
                catch { typeface = null; }
                _familyCache.Add(family, typeface);
                if (typeface != null) return typeface;
            }
        }
        return null;
    }

    public static bool TryParseLine(string? line, out int start, out int end, out string? family)
    {
        bool ok = TryParseLine(line, out start, out end, out string[] families);
        family = ok ? families[0] : null;
        return ok;
    }

    public static bool TryParseLine(string? line, out int start, out int end, out string[] families)
    {
        start = end = 0;
        families = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(line)) return false;
        int colon = line.IndexOf(':');
        if (colon <= 0) return false;
        string range = line.Substring(0, colon).Trim();
        string familyList = line.Substring(colon + 1).Trim().Trim(',', ';');
        string[] names = familyList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0) return false;
        int dash = range.IndexOf('-');
        string first = dash < 0 ? range : range.Substring(0, dash).Trim();
        string last = dash < 0 ? range : range.Substring(dash + 1).Trim();
        if (!TryParseCodepoint(first, out start) || !TryParseCodepoint(last, out end) || start > end)
            return false;
        families = names;
        return true;
    }

    private static bool TryParseCodepoint(string text, out int codepoint)
    {
        codepoint = 0;
        text = text.Trim();
        if (text.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
        return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out codepoint)
            && codepoint >= 0 && codepoint <= 0x10FFFF;
    }
}
