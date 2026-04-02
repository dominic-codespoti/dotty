using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// Registry that merges built-in and user-defined themes.
/// User themes take priority and can override built-in themes with the same name.
/// Provides thread-safe access to the theme collection.
/// </summary>
public sealed class ThemeRegistry
{
    private readonly UserThemeLoader _userThemeLoader;
    private readonly Dictionary<string, IColorScheme> _allThemes;
    private readonly Dictionary<string, IColorScheme> _builtInThemes;
    private readonly Dictionary<string, IColorScheme> _userThemes;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new ThemeRegistry with the default user themes directory.
    /// </summary>
    public ThemeRegistry()
        : this(new UserThemeLoader())
    {
    }

    /// <summary>
    /// Creates a new ThemeRegistry with a custom user theme loader.
    /// </summary>
    /// <param name="userThemeLoader">The user theme loader to use</param>
    public ThemeRegistry(UserThemeLoader userThemeLoader)
    {
        _userThemeLoader = userThemeLoader ?? throw new ArgumentNullException(nameof(userThemeLoader));
        _allThemes = new Dictionary<string, IColorScheme>(StringComparer.OrdinalIgnoreCase);
        _builtInThemes = new Dictionary<string, IColorScheme>(StringComparer.OrdinalIgnoreCase);
        _userThemes = new Dictionary<string, IColorScheme>(StringComparer.OrdinalIgnoreCase);

        // Initialize with built-in themes
        RegisterBuiltInThemes();
        
        // Load user themes (may override built-ins)
        RegisterUserThemes();
    }

    /// <summary>
    /// Gets the user themes directory path.
    /// </summary>
    public string UserThemesDirectory => _userThemeLoader.ThemesDirectory;

    /// <summary>
    /// Gets all available themes (built-in + user), with user themes taking priority.
    /// </summary>
    /// <returns>Read-only list of all themes</returns>
    public IReadOnlyList<IColorScheme> GetAllThemes()
    {
        lock (_lock)
        {
            return _allThemes.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets all built-in themes.
    /// </summary>
    /// <returns>Read-only list of built-in themes</returns>
    public IReadOnlyList<IColorScheme> GetBuiltInThemes()
    {
        lock (_lock)
        {
            return _builtInThemes.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets all user-defined themes.
    /// </summary>
    /// <returns>Read-only list of user themes</returns>
    public IReadOnlyList<IColorScheme> GetUserThemes()
    {
        lock (_lock)
        {
            return _userThemes.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets a theme by name.
    /// User themes take priority over built-in themes.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive, supports aliases)</param>
    /// <returns>The theme, or null if not found</returns>
    public IColorScheme? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        lock (_lock)
        {
            // Check all themes dictionary (user themes override built-ins)
            if (_allThemes.TryGetValue(name, out var theme))
            {
                return theme;
            }
        }

        // Try built-in theme registry for aliases
        var definition = BuiltInThemeRegistry.GetByName(name);
        if (definition != null)
        {
            // Return the built-in theme from our cache
            lock (_lock)
            {
                if (_allThemes.TryGetValue(definition.CanonicalName ?? "", out var cachedTheme))
                {
                    return cachedTheme;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a theme by name, falling back to the specified default if not found.
    /// </summary>
    /// <param name="name">Theme name to look up</param>
    /// <param name="defaultTheme">Default theme to return if not found</param>
    /// <returns>The requested theme, or the default</returns>
    public IColorScheme GetByNameOrDefault(string name, IColorScheme? defaultTheme = null)
    {
        return GetByName(name) ?? defaultTheme ?? BuiltInThemes.DarkPlus;
    }

    /// <summary>
    /// Refreshes the registry by reloading user themes from disk.
    /// Built-in themes are not reloaded.
    /// </summary>
    public void Refresh()
    {
        lock (_lock)
        {
            // Clear user themes and all themes
            _userThemes.Clear();
            _allThemes.Clear();

            // Re-add built-in themes
            foreach (var kvp in _builtInThemes)
            {
                _allThemes[kvp.Key] = kvp.Value;
            }
        }

        // Reload user themes (outside lock to prevent deadlocks during file I/O)
        RegisterUserThemes();
    }

    /// <summary>
    /// Registers all built-in themes from BuiltInThemes.
    /// </summary>
    private void RegisterBuiltInThemes()
    {
        var builtInThemes = new (string name, IColorScheme theme)[]
        {
            ("DarkPlus", BuiltInThemes.DarkPlus),
            ("Dracula", BuiltInThemes.Dracula),
            ("OneDark", BuiltInThemes.OneDark),
            ("GruvboxDark", BuiltInThemes.GruvboxDark),
            ("CatppuccinMocha", BuiltInThemes.CatppuccinMocha),
            ("TokyoNight", BuiltInThemes.TokyoNight),
            ("LightPlus", BuiltInThemes.LightPlus),
            ("OneLight", BuiltInThemes.OneLight),
            ("GruvboxLight", BuiltInThemes.GruvboxLight),
            ("CatppuccinLatte", BuiltInThemes.CatppuccinLatte),
            ("SolarizedLight", BuiltInThemes.SolarizedLight)
        };

        lock (_lock)
        {
            foreach (var (name, theme) in builtInThemes)
            {
                _builtInThemes[name] = theme;
                _allThemes[name] = theme;
            }
        }
    }

    /// <summary>
    /// Registers user themes from the themes directory.
    /// User themes can override built-in themes.
    /// </summary>
    private void RegisterUserThemes()
    {
        try
        {
            var userThemeDefinitions = _userThemeLoader.LoadFromDirectory();

            lock (_lock)
            {
                foreach (var definition in userThemeDefinitions)
                {
                    var canonicalName = definition.CanonicalName ?? "Unknown";
                    var theme = new JsonBackedColorScheme(definition);

                    _userThemes[canonicalName] = theme;
                    
                    // User themes override built-ins
                    _allThemes[canonicalName] = theme;

                    // Also register by aliases
                    foreach (var alias in definition.Aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias))
                        {
                            _allThemes[alias] = theme;
                        }
                    }

                    Console.WriteLine($"[ThemeRegistry] Registered user theme: '{canonicalName}'");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ThemeRegistry] Failed to load user themes: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a theme with the given name exists.
    /// </summary>
    /// <param name="name">Theme name to check</param>
    /// <returns>True if the theme exists</returns>
    public bool Contains(string name)
    {
        return GetByName(name) != null;
    }

    /// <summary>
    /// Checks if a theme is a user-defined theme (not built-in).
    /// </summary>
    /// <param name="name">Theme name to check</param>
    /// <returns>True if it's a user theme</returns>
    public bool IsUserTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_lock)
        {
            return _userThemes.ContainsKey(name);
        }
    }

    /// <summary>
    /// Checks if a theme is a built-in theme.
    /// </summary>
    /// <param name="name">Theme name to check</param>
    /// <returns>True if it's a built-in theme</returns>
    public bool IsBuiltInTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_lock)
        {
            return _builtInThemes.ContainsKey(name);
        }
    }
}
