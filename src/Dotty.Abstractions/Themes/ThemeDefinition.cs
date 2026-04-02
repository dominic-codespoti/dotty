using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Dotty.Abstractions.Themes;

/// <summary>
/// Represents a theme definition loaded from themes.json.
/// Contains metadata and color information for a terminal theme.
/// </summary>
public record ThemeDefinition
{
    /// <summary>
    /// Canonical theme name (unique identifier, PascalCase).
    /// </summary>
    [JsonPropertyName("canonicalName")]
    public string? CanonicalName { get; init; }

    /// <summary>
    /// Human-readable display name for the theme.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>
    /// Theme description explaining its characteristics.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Whether this is a dark theme.
    /// </summary>
    [JsonPropertyName("isDark")]
    public bool IsDark { get; init; }

    /// <summary>
    /// Alternative names for this theme (kebab-case or other variants).
    /// </summary>
    [JsonPropertyName("aliases")]
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Theme colors including background, foreground, opacity, and ANSI palette.
    /// </summary>
    [JsonPropertyName("colors")]
    public ThemeColors? Colors { get; init; }

    /// <summary>
    /// Checks if the given name matches this theme (case-insensitive).
    /// Supports the canonical name, display name (case-insensitive, space-insensitive),
    /// and all aliases (with normalization).
    /// </summary>
    /// <param name="name">Name to check</param>
    /// <returns>True if the name matches this theme</returns>
    public bool MatchesName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = NormalizeName(name);
        var canonicalNormalized = NormalizeName(CanonicalName ?? string.Empty);
        var displayNormalized = NormalizeName(DisplayName ?? string.Empty);

        if (normalized == canonicalNormalized || normalized == displayNormalized)
            return true;

        foreach (var alias in Aliases)
        {
            if (normalized == NormalizeName(alias))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a theme name for comparison.
    /// Converts to lowercase, removes spaces, hyphens, and plus signs.
    /// </summary>
    /// <param name="name">Name to normalize</param>
    /// <returns>Normalized name</returns>
    private static string NormalizeName(string name)
    {
        return name.ToLowerInvariant()
                   .Replace(" ", "")
                   .Replace("-", "")
                   .Replace("+", "plus");
    }
}

/// <summary>
/// Represents the color palette of a theme.
/// </summary>
public record ThemeColors
{
    /// <summary>
    /// Background color in hex format (e.g., "#1E1E1E").
    /// </summary>
    [JsonPropertyName("background")]
    public string? Background { get; init; }

    /// <summary>
    /// Foreground/text color in hex format (e.g., "#D4D4D4").
    /// </summary>
    [JsonPropertyName("foreground")]
    public string? Foreground { get; init; }

    /// <summary>
    /// Window background opacity as a value from 0.0 (fully transparent) to 1.0 (fully opaque).
    /// </summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; init; } = 1.0;

    /// <summary>
    /// ANSI color palette - array of 16 hex colors.
    /// Index 0-7: Normal colors (black, red, green, yellow, blue, magenta, cyan, white)
    /// Index 8-15: Bright colors (bright black, bright red, etc.)
    /// </summary>
    [JsonPropertyName("ansi")]
    public string[]? Ansi { get; init; }

    /// <summary>
    /// Gets an ANSI color by index with bounds checking.
    /// Returns the foreground color if index is out of bounds.
    /// </summary>
    /// <param name="index">ANSI color index (0-15)</param>
    /// <returns>Hex color string at the specified index, or foreground color if out of bounds</returns>
    public string GetAnsiColor(int index)
    {
        if (Ansi == null || index < 0 || index >= Ansi.Length)
        {
            return Foreground ?? "#FFFFFF";
        }
        return Ansi[index];
    }

    /// <summary>
    /// Gets an ANSI color by index with bounds checking.
    /// </summary>
    /// <param name="index">ANSI color index (0-15)</param>
    /// <param name="fallback">Fallback color to return if index is out of bounds</param>
    /// <returns>Hex color string at the specified index, or fallback if out of bounds</returns>
    public string GetAnsiColor(int index, string fallback)
    {
        if (Ansi == null || index < 0 || index >= Ansi.Length)
        {
            return fallback;
        }
        return Ansi[index];
    }
}

/// <summary>
/// Root object for themes.json deserialization.
/// </summary>
public record ThemeRoot
{
    /// <summary>
    /// Schema version for migrations.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Array of theme definitions.
    /// </summary>
    [JsonPropertyName("themes")]
    public ThemeDefinition[]? Themes { get; init; }
}
