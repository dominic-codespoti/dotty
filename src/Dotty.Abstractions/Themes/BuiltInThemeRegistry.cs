using System.Reflection;
using System.Text.Json;

namespace Dotty.Abstractions.Themes;

/// <summary>
/// Registry that loads and provides access to built-in themes from embedded resources.
/// </summary>
public static class BuiltInThemeRegistry
{
    private static readonly Lazy<ThemeDefinition[]> _themes = new(LoadThemes, true);
    private static readonly Dictionary<string, ThemeDefinition> _themeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all available built-in theme definitions.
    /// </summary>
    public static ThemeDefinition[] AllThemes => _themes.Value;

    /// <summary>
    /// Gets all dark theme definitions.
    /// </summary>
    public static ThemeDefinition[] DarkThemes => _themes.Value.Where(t => t.IsDark).ToArray();

    /// <summary>
    /// Gets all light theme definitions.
    /// </summary>
    public static ThemeDefinition[] LightThemes => _themes.Value.Where(t => !t.IsDark).ToArray();

    /// <summary>
    /// Gets the default theme (DarkPlus).
    /// </summary>
    public static ThemeDefinition DefaultTheme => GetByNameOrDefault("DarkPlus")!;

    /// <summary>
    /// Loads theme definitions from the embedded themes.json resource.
    /// </summary>
    private static ThemeDefinition[] LoadThemes()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Dotty.Abstractions.Themes.themes.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                // Fallback: try to find any resource containing "themes"
                var resourceNames = assembly.GetManifestResourceNames();
                resourceName = resourceNames.FirstOrDefault(r => r.IndexOf("themes", StringComparison.OrdinalIgnoreCase) >= 0)!;
                
                if (resourceName == null)
                {
                    throw new InvalidOperationException("Could not find themes.json embedded resource.");
                }

                using var fallbackStream = assembly.GetManifestResourceStream(resourceName);
                if (fallbackStream == null)
                {
                    throw new InvalidOperationException($"Failed to load embedded resource: {resourceName}");
                }

                return LoadFromStream(fallbackStream);
            }

            return LoadFromStream(stream);
        }
        catch (Exception ex)
        {
            // In case of error, return an empty array
            // This prevents the application from crashing on startup
            // The error will be logged elsewhere
            Console.Error.WriteLine($"Error loading themes: {ex.Message}");
            return Array.Empty<ThemeDefinition>();
        }
    }

    /// <summary>
    /// Loads theme definitions from a stream.
    /// </summary>
    private static ThemeDefinition[] LoadFromStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var root = JsonSerializer.Deserialize<ThemeRoot>(json, options);
        
        if (root?.Themes == null || root.Themes.Length == 0)
        {
            throw new InvalidOperationException("Failed to deserialize themes.json or no themes found.");
        }

        // Populate the cache for fast lookups
        foreach (var theme in root.Themes)
        {
            _themeCache[theme.CanonicalName ?? "Unknown"] = theme;
            foreach (var alias in theme.Aliases)
            {
                _themeCache[alias] = theme;
            }
        }

        return root.Themes;
    }

    /// <summary>
    /// Gets a theme definition by name.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive, supports aliases)</param>
    /// <returns>The theme definition, or null if not found</returns>
    public static ThemeDefinition? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Check cache first (handles aliases directly)
        if (_themeCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        // Fall back to searching with MatchesName
        var normalized = name.ToLowerInvariant();
        return _themes.Value.FirstOrDefault(t => t.MatchesName(normalized));
    }

    /// <summary>
    /// Gets a theme by name, or returns the default theme if not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>The requested theme, or the default theme if not found</returns>
    public static ThemeDefinition? GetByNameOrDefault(string name)
    {
        return GetByName(name) ?? GetByName("DarkPlus");
    }

    /// <summary>
    /// Refreshes the theme cache (useful for testing or hot-reload scenarios).
    /// </summary>
    internal static void Refresh()
    {
        _themeCache.Clear();
        _themes.Value.ToList(); // Force evaluation
    }
}
