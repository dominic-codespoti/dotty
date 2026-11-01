using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dotty.Abstractions.Config;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// The font model of the <c>font.*</c> configuration: ordered family list (also the fallback order, as in
/// Ghostty), optional per-style families for the primary font, regular weight and OpenType features.
/// Equal options compare equal, so a host can rebuild its fallback chain only when the model changes.
/// </summary>
public sealed class FontOptions : IEquatable<FontOptions>
{
    public static FontOptions Default { get; } = Create(null, null, null, null, null, null);

    private FontOptions(
        IReadOnlyList<string> families, IReadOnlyList<string> boldFamilies, IReadOnlyList<string> italicFamilies,
        IReadOnlyList<string> boldItalicFamilies, int weight, IReadOnlyList<FontFeature> features)
    {
        Families = families;
        BoldFamilies = boldFamilies;
        ItalicFamilies = italicFamilies;
        BoldItalicFamilies = boldItalicFamilies;
        Weight = weight;
        Features = features;
    }

    /// <summary>Configured families in fallback order; never empty (the OS default stack when unset).</summary>
    public IReadOnlyList<string> Families { get; }
    public IReadOnlyList<string> BoldFamilies { get; }
    public IReadOnlyList<string> ItalicFamilies { get; }
    public IReadOnlyList<string> BoldItalicFamilies { get; }

    /// <summary>Weight of regular text, 1-1000.</summary>
    public int Weight { get; }

    /// <summary>Weight requested for bold text: 700, or 900 when the regular weight is already semibold or heavier.</summary>
    public int BoldWeight => Weight >= 600 ? 900 : FontFaceStyle.BoldWeight;

    /// <summary>Parsed <c>font.features</c>, for shaping.</summary>
    public IReadOnlyList<FontFeature> Features { get; }

    public FontFaceStyle RegularStyle => new(Weight, false);

    public FontFaceStyle StyleFor(GlyphStyle style) =>
        new((style & GlyphStyle.Bold) != 0 ? BoldWeight : Weight, (style & GlyphStyle.Italic) != 0);

    /// <summary>The explicit families configured for <paramref name="style"/> (empty for regular or when unset).</summary>
    public IReadOnlyList<string> FamiliesFor(GlyphStyle style) => (style & (GlyphStyle.Bold | GlyphStyle.Italic)) switch
    {
        GlyphStyle.Bold => BoldFamilies,
        GlyphStyle.Italic => ItalicFamilies,
        GlyphStyle.Bold | GlyphStyle.Italic => BoldItalicFamilies,
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Builds the model from config values. Invalid weight and feature entries are reported to
    /// <paramref name="errors"/> and replaced by the default (regular) or skipped.
    /// </summary>
    public static FontOptions Create(
        string? family, string? boldFamily, string? italicFamily, string? boldItalicFamily,
        string? weight, IEnumerable<string>? features, ICollection<string>? errors = null)
    {
        var families = ParseFamilyList(family);
        if (families.Count == 0)
            families = ParseFamilyList(DottyDefaults.FontFamily);
        int parsedWeight = FontFaceStyle.RegularWeight;
        if (!string.IsNullOrWhiteSpace(weight) && !FontWeights.TryParse(weight, out parsedWeight))
        {
            errors?.Add($"font.weight: '{weight}' is not a weight name or a number 1-1000; using regular");
            parsedWeight = FontFaceStyle.RegularWeight;
        }
        return new FontOptions(
            families, ParseFamilyList(boldFamily), ParseFamilyList(italicFamily), ParseFamilyList(boldItalicFamily),
            parsedWeight, FontFeatureParser.Parse(features, errors));
    }

    /// <summary>Splits a comma-separated family list, trimming names and dropping empty and repeated (case-insensitive) names.</summary>
    public static IReadOnlyList<string> ParseFamilyList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
            return Array.Empty<string>();
        var result = new List<string>();
        foreach (string name in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        return result;
    }

    public bool Equals(FontOptions? other) =>
        other != null &&
        Weight == other.Weight &&
        Families.SequenceEqual(other.Families, StringComparer.OrdinalIgnoreCase) &&
        BoldFamilies.SequenceEqual(other.BoldFamilies, StringComparer.OrdinalIgnoreCase) &&
        ItalicFamilies.SequenceEqual(other.ItalicFamilies, StringComparer.OrdinalIgnoreCase) &&
        BoldItalicFamilies.SequenceEqual(other.BoldItalicFamilies, StringComparer.OrdinalIgnoreCase) &&
        Features.SequenceEqual(other.Features);

    public override bool Equals(object? obj) => obj is FontOptions other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Weight);
        foreach (string family in Families)
            hash.Add(family, StringComparer.OrdinalIgnoreCase);
        foreach (var feature in Features)
            hash.Add(feature);
        return hash.ToHashCode();
    }
}

/// <summary>Parses <c>font.weight</c>: a CSS weight name or a number 1-1000.</summary>
public static class FontWeights
{
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thin"] = 100, ["hairline"] = 100,
        ["extralight"] = 200, ["ultralight"] = 200,
        ["light"] = 300,
        ["regular"] = 400, ["normal"] = 400, ["book"] = 400,
        ["medium"] = 500,
        ["semibold"] = 600, ["demibold"] = 600,
        ["bold"] = 700,
        ["extrabold"] = 800, ["ultrabold"] = 800,
        ["black"] = 900, ["heavy"] = 900,
    };

    public static bool TryParse(string? text, out int weight)
    {
        weight = FontFaceStyle.RegularWeight;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string trimmed = text.Trim();
        // Names may be written "extra-light" or "extra light"; numbers must be plain digits.
        if (Names.TryGetValue(trimmed.Replace("-", string.Empty).Replace(" ", string.Empty), out weight))
            return true;
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out weight) && weight is >= 1 and <= 1000)
            return true;
        weight = FontFaceStyle.RegularWeight;
        return false;
    }
}
