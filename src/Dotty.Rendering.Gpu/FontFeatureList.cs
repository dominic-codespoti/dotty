using System;
using System.Collections.Generic;
using HarfBuzzSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Parses OpenType feature strings ("calt", "liga=0", "ss01[1]") into
/// HarfBuzz features. Unparseable entries are dropped, never throw.
/// </summary>
public static class FontFeatureList
{
    public static Feature[] Parse(IEnumerable<string>? entries)
    {
        if (entries == null)
            return Array.Empty<Feature>();
        var features = new List<Feature>();
        foreach (string entry in entries)
        {
            if (TryParse(entry, out Feature feature))
                features.Add(feature);
        }
        return features.ToArray();
    }

    public static bool TryParse(string? entry, out Feature feature)
    {
        feature = default;
        if (string.IsNullOrWhiteSpace(entry))
            return false;
        string text = entry.Trim();
        if (text.Length is < 4 or > 16)
            return false;
        // Tag is the first 4 ASCII chars; the rest is "=value", "[index]", or empty.
        for (int i = 0; i < 4; i++)
        {
            if (text[i] > 127 || char.IsWhiteSpace(text[i]))
                return false;
        }
        string remainder = text.Length > 4 ? text.Substring(4).Trim() : string.Empty;
        uint value = 1;
        if (remainder.Length > 0)
        {
            if (remainder.StartsWith("[", StringComparison.Ordinal) && remainder.EndsWith("]", StringComparison.Ordinal))
                remainder = remainder.Substring(1, remainder.Length - 2).Trim();
            if (remainder.StartsWith("=", StringComparison.Ordinal))
                remainder = remainder.Substring(1).Trim();
            if (remainder.Length == 0)
                return false;
            if (string.Equals(remainder, "off", StringComparison.OrdinalIgnoreCase))
                value = 0;
            else if (!string.Equals(remainder, "on", StringComparison.OrdinalIgnoreCase) &&
                !uint.TryParse(remainder, out value))
                return false;
        }
        try
        {
            var tag = new Tag(text[0], text[1], text[2], text[3]);
            feature = new Feature(tag, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Normalize(IEnumerable<string>? entries)
    {
        Feature[] parsed = Parse(entries);
        if (parsed.Length == 0)
            return string.Empty;
        var parts = new string[parsed.Length];
        for (int i = 0; i < parsed.Length; i++)
            parts[i] = parsed[i].ToString();
        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join(",", parts);
    }
}
