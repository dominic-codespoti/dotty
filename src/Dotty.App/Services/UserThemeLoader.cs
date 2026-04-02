using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// Loads user-defined themes from the ~/.config/dotty/themes/ directory.
/// Handles JSON deserialization and graceful error handling for invalid files.
/// </summary>
public sealed class UserThemeLoader
{
    /// <summary>
    /// The default themes directory path (cross-platform).
    /// </summary>
    public static string DefaultThemesDirectory => GetDefaultThemesDirectory();

    private readonly string _themesDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new UserThemeLoader with the default themes directory.
    /// </summary>
    public UserThemeLoader()
        : this(DefaultThemesDirectory)
    {
    }

    /// <summary>
    /// Creates a new UserThemeLoader with a custom themes directory.
    /// </summary>
    /// <param name="themesDirectory">Path to the themes directory</param>
    public UserThemeLoader(string themesDirectory)
    {
        _themesDirectory = themesDirectory ?? throw new ArgumentNullException(nameof(themesDirectory));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    /// <summary>
    /// Gets the themes directory path being used by this loader.
    /// </summary>
    public string ThemesDirectory => _themesDirectory;

    /// <summary>
    /// Loads all valid theme definitions from the themes directory.
    /// Scans for *.json files and attempts to deserialize each to ThemeDefinition.
    /// </summary>
    /// <returns>List of successfully loaded theme definitions</returns>
    public IReadOnlyList<ThemeDefinition> LoadFromDirectory()
    {
        var themes = new List<ThemeDefinition>();

        if (!Directory.Exists(_themesDirectory))
        {
            // Directory doesn't exist - return empty list, this is not an error
            return themes;
        }

        var jsonFiles = Directory.GetFiles(_themesDirectory, "*.json", SearchOption.TopDirectoryOnly);

        foreach (var filePath in jsonFiles)
        {
            try
            {
                var theme = LoadFromFile(filePath);
                if (theme != null)
                {
                    themes.Add(theme);
                }
            }
            catch (Exception ex)
            {
                // Log error but continue loading other themes
                Console.Error.WriteLine($"[UserThemeLoader] Failed to load theme from '{filePath}': {ex.Message}");
            }
        }

        return themes;
    }

    /// <summary>
    /// Loads a single theme from a JSON file.
    /// </summary>
    /// <param name="filePath">Path to the JSON file</param>
    /// <returns>The theme definition, or null if loading failed</returns>
    /// <exception cref="FileNotFoundException">If the file doesn't exist</exception>
    public ThemeDefinition? LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Theme file not found: {filePath}", filePath);
        }

        var json = File.ReadAllText(filePath);
        
        // First, try to deserialize as ThemeDefinition directly (single theme file)
        var theme = TryDeserializeSingleTheme(json);
        if (theme != null)
        {
            return ValidateAndFixTheme(theme, filePath);
        }

        // If that fails, try as ThemeRoot (file with "themes" array)
        var root = TryDeserializeThemeRoot(json);
        if (root?.Themes?.Length > 0)
        {
            // For ThemeRoot format, return the first theme (multi-theme files not fully supported)
            return ValidateAndFixTheme(root.Themes[0], filePath);
        }

        throw new JsonException($"Failed to deserialize theme from '{filePath}'. Expected ThemeDefinition or ThemeRoot format.");
    }

    /// <summary>
    /// Attempts to deserialize a single ThemeDefinition from JSON.
    /// </summary>
    private ThemeDefinition? TryDeserializeSingleTheme(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ThemeDefinition>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to deserialize a ThemeRoot (file with themes array) from JSON.
    /// </summary>
    private ThemeRoot? TryDeserializeThemeRoot(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ThemeRoot>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates and fixes common issues in loaded themes.
    /// </summary>
    private ThemeDefinition? ValidateAndFixTheme(ThemeDefinition theme, string filePath)
    {
        // Ensure theme has a name
        if (string.IsNullOrWhiteSpace(theme.CanonicalName))
        {
            // Use filename without extension as fallback
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            Console.WriteLine($"[UserThemeLoader] Theme in '{filePath}' missing canonicalName, using filename: '{fileName}'");
            
            // Create a new theme with the fixed name (records are immutable)
            theme = theme with { CanonicalName = fileName };
        }

        // Check for colors
        if (theme.Colors == null)
        {
            Console.Error.WriteLine($"[UserThemeLoader] Theme '{theme.CanonicalName}' has no colors defined");
            return null;
        }

        return theme;
    }

    /// <summary>
    /// Ensures the themes directory exists, creating it if necessary.
    /// </summary>
    /// <returns>True if the directory exists or was created successfully</returns>
    public bool EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_themesDirectory))
            {
                Directory.CreateDirectory(_themesDirectory);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UserThemeLoader] Failed to create themes directory '{_themesDirectory}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the default themes directory path based on the platform.
    /// Uses XDG_CONFIG_HOME on Linux/macOS, %APPDATA% on Windows.
    /// </summary>
    private static string GetDefaultThemesDirectory()
    {
        // Check for XDG_CONFIG_HOME first (Linux/macOS standard)
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, "dotty", "themes");
        }

        // Fall back to platform-specific locations
        var userConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        
        // On Linux/macOS, ApplicationData points to ~/.config (or ~/Library/Application Support on macOS)
        // On Windows, it points to %APPDATA%
        return Path.Combine(userConfigDir, "dotty", "themes");
    }
}
