using System;
using System.Collections.Generic;
using System.Linq;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Resolves theme names to theme data for testing.
/// </summary>
public class ThemeResolver
{
    private readonly Dictionary<string, ThemeData> _themes;
    private readonly Dictionary<string, string> _aliases;

    public ThemeResolver()
    {
        _themes = new Dictionary<string, ThemeData>(StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        InitializeThemes();
    }

    private void InitializeThemes()
    {
        // Dark+
        AddTheme("Dark+", "#1e1e1e", 1.0, new[]
        {
            "#000000", "#cd3131", "#0dbc79", "#e5e510", "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
            "#666666", "#f14c4c", "#23d18b", "#f5f543", "#3b8eea", "#d670d6", "#29b8db", "#ffffff"
        });

        // Light+
        AddTheme("Light+", "#ffffff", 1.0, new[]
        {
            "#000000", "#cd3131", "#00bc00", "#e5e510", "#0000ee", "#cd00cd", "#00cdcd", "#e5e5e5",
            "#666666", "#ff0000", "#00ff00", "#ffff00", "#5c5cff", "#ff00ff", "#00ffff", "#ffffff"
        });

        // Monokai
        AddTheme("Monokai", "#272822", 1.0, new[]
        {
            "#272822", "#f92672", "#a6e22e", "#f4bf75", "#66d9ef", "#ae81ff", "#a1efe4", "#f8f8f2",
            "#75715e", "#f92672", "#a6e22e", "#f4bf75", "#66d9ef", "#ae81ff", "#a1efe4", "#f9f8f5"
        });
        _aliases["monokai"] = "Monokai";

        // Dracula
        AddTheme("Dracula", "#282a36", 1.0, new[]
        {
            "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
            "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff"
        });
        _aliases["dracula"] = "Dracula";

        // Nord
        AddTheme("Nord", "#2e3440", 0.95, new[]
        {
            "#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0",
            "#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4"
        });
        _aliases["nord"] = "Nord";

        // OneDark
        AddTheme("OneDark", "#282c34", 1.0, new[]
        {
            "#282c34", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
            "#5c6370", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff"
        });
        _aliases["onedark"] = "OneDark";
        _aliases["one dark"] = "OneDark";

        // Solarized Dark
        AddTheme("Solarized Dark", "#002b36", 1.0, new[]
        {
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3"
        });
        _aliases["solarized dark"] = "Solarized Dark";
        _aliases["solarized_dark"] = "Solarized Dark";

        // Solarized Light
        AddTheme("Solarized Light", "#fdf6e3", 1.0, new[]
        {
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3"
        });
        _aliases["solarized light"] = "Solarized Light";
        _aliases["solarized_light"] = "Solarized Light";

        // Tokyo Night
        AddTheme("Tokyo Night", "#1a1b26", 0.95, new[]
        {
            "#15161e", "#f7768e", "#9ece6a", "#e0af68", "#7aa2f7", "#bb9af7", "#7dcfff", "#787c99",
            "#414868", "#f7768e", "#9ece6a", "#e0af68", "#7aa2f7", "#bb9af7", "#7dcfff", "#c0caf5"
        });
        _aliases["tokyo night"] = "Tokyo Night";
        _aliases["tokyonight"] = "Tokyo Night";
        _aliases["tokyo_night"] = "Tokyo Night";

        // GitHub Dark
        AddTheme("GitHub Dark", "#0d1117", 1.0, new[]
        {
            "#484f58", "#ff7b72", "#3fb950", "#d29922", "#58a6ff", "#f778ba", "#39c5cf", "#b1bac4",
            "#6e7681", "#ffa198", "#56d364", "#e3b341", "#79c0ff", "#f088b8", "#56d4dd", "#ffffff"
        });
        _aliases["github dark"] = "GitHub Dark";
        _aliases["github_dark"] = "GitHub Dark";

        // GitHub Light
        AddTheme("GitHub Light", "#ffffff", 1.0, new[]
        {
            "#24292f", "#cf222e", "#116329", "#4d2d00", "#0969da", "#8250df", "#1b7c83", "#6e7781",
            "#57606a", "#a40e26", "#1a7f37", "#633c01", "#218bff", "#a475f9", "#319aad", "#ffffff"
        });
        _aliases["github light"] = "GitHub Light";
        _aliases["github_light"] = "GitHub Light";
    }

    private void AddTheme(string name, string background, double opacity, string[] ansiColors)
    {
        _themes[name] = new ThemeData
        {
            Name = name,
            Background = background,
            Opacity = opacity,
            AnsiColors = ansiColors
        };
    }

    /// <summary>
    /// Resolves a theme name or alias to theme data.
    /// Falls back to Dark+ if not found.
    /// </summary>
    public ThemeData Resolve(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName))
        {
            return _themes["Dark+"];
        }

        // Try direct lookup
        if (_themes.TryGetValue(themeName, out var theme))
        {
            return theme;
        }

        // Try alias lookup
        if (_aliases.TryGetValue(themeName, out var canonicalName))
        {
            return _themes[canonicalName];
        }

        // Fall back to Dark+ for unknown themes
        return _themes["Dark+"];
    }
}

/// <summary>
/// Theme data returned by ThemeResolver.
/// </summary>
public class ThemeData
{
    public string Name { get; set; } = "";
    public string Background { get; set; } = "";
    public double Opacity { get; set; } = 1.0;
    public string[] AnsiColors { get; set; } = Array.Empty<string>();
}