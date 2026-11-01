using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dotty.Rendering.Gpu;

/// <summary>One OpenType feature setting from <c>font.features</c>: a four-character tag and its value (0 = off, 1 = on, n = alternate).</summary>
public readonly record struct FontFeature(string Tag, uint Value)
{
    public override string ToString() => Value switch
    {
        0 => "-" + Tag,
        1 => "+" + Tag,
        _ => Tag + "=" + Value.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Parses <c>font.features</c> entries. Accepted forms: <c>+tag</c> or <c>tag</c> (on), <c>-tag</c> (off) and
/// <c>tag=n</c> (value n, 0-65535; <c>on</c>/<c>off</c> also accepted). A tag is exactly four ASCII letters or digits.
/// Ranged settings such as <c>[3:5]calt</c> are rejected because a terminal cell has no stable text offsets.
/// A later setting of the same tag replaces the earlier one; invalid entries are skipped and reported.
/// </summary>
public static class FontFeatureParser
{
    public const uint MaxValue = 65535;

    public static IReadOnlyList<FontFeature> Parse(IEnumerable<string>? entries, ICollection<string>? errors = null)
    {
        var result = new List<FontFeature>();
        if (entries == null)
            return result;
        foreach (string? entry in entries)
        {
            if (!TryParse(entry, out var feature, out string? error))
            {
                errors?.Add(error!);
                continue;
            }
            int existing = result.FindIndex(f => f.Tag == feature.Tag);
            if (existing >= 0)
                result.RemoveAt(existing);
            result.Add(feature);
        }
        return result;
    }

    public static bool TryParse(string? entry, out FontFeature feature, out string? error)
    {
        feature = default;
        string text = entry?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "font.features: empty entry";
            return false;
        }
        if (text[0] == '[' || text.Contains('['))
        {
            error = $"font.features: '{text}': ranged feature settings are not supported";
            return false;
        }

        uint value = 1;
        string tag;
        if (text[0] is '+' or '-')
        {
            value = text[0] == '+' ? 1u : 0u;
            tag = text.Substring(1).Trim();
        }
        else
        {
            int eq = text.IndexOf('=');
            if (eq < 0)
            {
                tag = text;
            }
            else
            {
                tag = text.Substring(0, eq).Trim();
                string valueText = text.Substring(eq + 1).Trim();
                if (valueText.Equals("on", StringComparison.OrdinalIgnoreCase)) value = 1;
                else if (valueText.Equals("off", StringComparison.OrdinalIgnoreCase)) value = 0;
                else if (!uint.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value > MaxValue)
                {
                    error = $"font.features: '{text}': value must be on, off or an integer 0-{MaxValue}";
                    return false;
                }
            }
        }

        if (!IsValidTag(tag))
        {
            error = $"font.features: '{text}': a feature tag is four ASCII letters or digits";
            return false;
        }
        feature = new FontFeature(tag, value);
        error = null;
        return true;
    }

    private static bool IsValidTag(string tag)
    {
        if (tag.Length != 4)
            return false;
        foreach (char c in tag)
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        return true;
    }
}
