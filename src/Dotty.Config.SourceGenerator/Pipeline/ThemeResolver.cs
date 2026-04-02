using System.Reflection;
using System.Text.Json;
using Dotty.Config.SourceGenerator.Models;

namespace Dotty.Config.SourceGenerator.Pipeline;

/// <summary>
/// Loads and resolves themes from the embedded themes.json resource.
/// </summary>
public static class ThemeResolver
{
    private static readonly Lazy<Dictionary<string, ThemeModel>> _themes = new(LoadThemes, true);

    /// <summary>
    /// Static constructor initializes the theme cache.
    /// </summary>
    static ThemeResolver()
    {
        // Access _themes to ensure it's initialized
        _ = _themes.Value;
    }

    /// <summary>
    /// Loads themes from the embedded themes.json resource.
    /// </summary>
    private static Dictionary<string, ThemeModel> LoadThemes()
    {
        var themes = new Dictionary<string, ThemeModel>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Dotty.Config.SourceGenerator.themes.json";

            // Try to find the embedded resource
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    LoadFromStream(stream, themes);
                    return themes;
                }
            }

            // Fallback: try to find any resource containing "themes"
            var resourceNames = assembly.GetManifestResourceNames();
            var fallbackResource = resourceNames.FirstOrDefault(r => r.IndexOf("themes", StringComparison.OrdinalIgnoreCase) >= 0);

            if (fallbackResource != null)
            {
                using (var fallbackStream = assembly.GetManifestResourceStream(fallbackResource))
                {
                    if (fallbackStream != null)
                    {
                        LoadFromStream(fallbackStream, themes);
                        return themes;
                    }
                }
            }
        }
        catch
        {
            // Ignore errors and use hardcoded fallback themes
        }

        // Add hardcoded fallback themes if JSON loading failed
        AddFallbackThemes(themes);

        return themes;
    }

    /// <summary>
    /// Loads themes from a stream into the dictionary.
    /// </summary>
    private static void LoadFromStream(Stream stream, Dictionary<string, ThemeModel> themes)
    {
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var root = JsonSerializer.Deserialize<ThemeRoot>(json, options);
        if (root?.Themes == null) return;

        foreach (var theme in root.Themes)
        {
            var model = ConvertToThemeModel(theme);

            // Add by canonical name
            if (!string.IsNullOrEmpty(theme.CanonicalName))
            {
                themes[theme.CanonicalName] = model;
            }

            // Add by aliases
            foreach (var alias in theme.Aliases ?? Array.Empty<string>())
            {
                themes[alias] = model;
            }

            // Add normalized variations
            var normalized = NormalizeName(theme.CanonicalName ?? "");
            if (!string.IsNullOrEmpty(normalized))
            {
                themes[normalized] = model;
            }
        }
    }

    /// <summary>
    /// Converts a ThemeDefinition to a ThemeModel.
    /// </summary>
    private static ThemeModel ConvertToThemeModel(ThemeDefinition theme)
    {
        var ansiColors = new uint[16];
        if (theme.Colors?.Ansi != null)
        {
            for (int i = 0; i < Math.Min(16, theme.Colors.Ansi.Length); i++)
            {
                ansiColors[i] = ParseHexColor(theme.Colors.Ansi[i]);
            }
        }

        return new ThemeModel
        {
            CanonicalName = theme.CanonicalName ?? "DarkPlus",
            Background = ParseHexColor(theme.Colors?.Background ?? "#1E1E1E"),
            Foreground = ParseHexColor(theme.Colors?.Foreground ?? "#D4D4D4"),
            Opacity = (byte)Clamp((int)((theme.Colors?.Opacity ?? 1.0) * 100), 0, 100),
            AnsiColors = ansiColors
        };
    }

    /// <summary>
    /// Parses a hex color string to uint (ARGB format).
    /// </summary>
    private static uint ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0xFFFFFFFF;

        hex = hex.Trim().TrimStart('#');

        if (hex.Length == 6)
        {
            // RGB format, add full opacity
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            {
                return 0xFF000000 | rgb;
            }
        }
        else if (hex.Length == 8)
        {
            // ARGB format
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            {
                return argb;
            }
        }

        return 0xFFFFFFFF;
    }

    /// <summary>
    /// Clamps a value to the specified range.
    /// </summary>
    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>
    /// Normalizes a theme name for comparison.
    /// </summary>
    private static string NormalizeName(string name)
    {
        return name.ToLowerInvariant()
                   .Replace(" ", "")
                   .Replace("-", "")
                   .Replace("+", "plus");
    }

    /// <summary>
    /// Adds hardcoded fallback themes if JSON loading fails.
    /// </summary>
    private static void AddFallbackThemes(Dictionary<string, ThemeModel> themes)
    {
        // DarkPlus (default)
        themes["DarkPlus"] = ThemeModel.DarkPlus;
        themes["darkplus"] = ThemeModel.DarkPlus;
        themes["dark-plus"] = ThemeModel.DarkPlus;
        themes["vscode-dark"] = ThemeModel.DarkPlus;

        // Add other common themes as fallbacks
        var catppuccinMocha = new ThemeModel
        {
            CanonicalName = "CatppuccinMocha",
            Background = 0xFF1E1E2E,
            Foreground = 0xFFCDD6F4,
            Opacity = 100,
            AnsiColors = new uint[]
            {
                0xFF45475A, 0xFFF38BA8, 0xFFA6E3A1, 0xFFF9E2AF,
                0xFF89B4FA, 0xFFF5C2E7, 0xFF94E2D5, 0xFFBAC2DE,
                0xFF585B70, 0xFFF38BA8, 0xFFA6E3A1, 0xFFF9E2AF,
                0xFF89B4FA, 0xFFF5C2E7, 0xFF94E2D5, 0xFFA6ADC8
            }
        };
        themes["CatppuccinMocha"] = catppuccinMocha;
        themes["catppuccin-mocha"] = catppuccinMocha;

        var dracula = new ThemeModel
        {
            CanonicalName = "Dracula",
            Background = 0xFF282A36,
            Foreground = 0xFFF8F8F2,
            Opacity = 100,
            AnsiColors = new uint[]
            {
                0xFF21222C, 0xFFFF5555, 0xFF50FA7B, 0xFFF1FA8C,
                0xFFBD93F9, 0xFFFF79C6, 0xFF8BE9FD, 0xFFF8F8F2,
                0xFF6272A4, 0xFFFF6E6E, 0xFF69FF94, 0xFFFFFFA5,
                0xFFD6ACFF, 0xFFFF92DF, 0xFFA4FFFF, 0xFFFFFFFF
            }
        };
        themes["Dracula"] = dracula;
    }

    /// <summary>
    /// Resolves a theme by name, supporting aliases and normalized names.
    /// Falls back to DarkPlus if not found.
    /// </summary>
    /// <param name="name">Theme name or alias</param>
    /// <returns>ThemeModel for the requested theme, or DarkPlus if not found</returns>
    public static ThemeModel Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ThemeModel.DarkPlus;

        // Try direct lookup
        if (_themes.Value.TryGetValue(name, out var theme))
            return theme;

        // Try normalized name
        var normalized = NormalizeName(name);
        if (_themes.Value.TryGetValue(normalized, out theme))
            return theme;

        // Try removing "Theme" suffix
        if (name.EndsWith("Theme", StringComparison.OrdinalIgnoreCase))
        {
            var withoutSuffix = name.Substring(0, name.Length - 5);
            if (_themes.Value.TryGetValue(withoutSuffix, out theme))
                return theme;

            var normalizedWithoutSuffix = NormalizeName(withoutSuffix);
            if (_themes.Value.TryGetValue(normalizedWithoutSuffix, out theme))
                return theme;
        }

        // Fallback to DarkPlus
        return ThemeModel.DarkPlus;
    }
}

/// <summary>
/// Theme definition for JSON deserialization.
/// </summary>
internal class ThemeRoot
{
    public int Version { get; set; }
    public ThemeDefinition[]? Themes { get; set; }
}

/// <summary>
/// Individual theme definition.
/// </summary>
internal class ThemeDefinition
{
    public string? CanonicalName { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsDark { get; set; }
    public string[]? Aliases { get; set; }
    public ThemeColors? Colors { get; set; }
}

/// <summary>
/// Theme colors.
/// </summary>
internal class ThemeColors
{
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    public double Opacity { get; set; } = 1.0;
    public string[]? Ansi { get; set; }
}
