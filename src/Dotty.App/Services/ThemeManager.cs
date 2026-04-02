using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// Manages runtime theme selection and application.
/// Provides access to both built-in and user-defined themes.
/// </summary>
public sealed class ThemeManager
{
    private readonly ThemeRegistry _registry;
    private IColorScheme _currentTheme;

    /// <summary>
    /// Event raised when the current theme changes.
    /// </summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <summary>
    /// Creates a new ThemeManager with the specified registry.
    /// </summary>
    /// <param name="registry">The theme registry to use</param>
    public ThemeManager(ThemeRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _currentTheme = GetInitialTheme();
    }

    /// <summary>
    /// Creates a new ThemeManager with a default registry.
    /// </summary>
    public ThemeManager()
        : this(new ThemeRegistry())
    {
    }

    /// <summary>
    /// Gets the currently applied theme.
    /// </summary>
    public IColorScheme CurrentTheme => _currentTheme;

    /// <summary>
    /// Gets all available themes (built-in + user-defined).
    /// </summary>
    public IReadOnlyList<IColorScheme> AvailableThemes => _registry.GetAllThemes();

    /// <summary>
    /// Gets all available dark themes.
    /// </summary>
    public IEnumerable<IColorScheme> DarkThemes => AvailableThemes.Where(t => IsDarkTheme(t));

    /// <summary>
    /// Gets all available light themes.
    /// </summary>
    public IEnumerable<IColorScheme> LightThemes => AvailableThemes.Where(t => !IsDarkTheme(t));

    /// <summary>
    /// Gets all built-in themes.
    /// </summary>
    public IReadOnlyList<IColorScheme> BuiltInThemes => _registry.GetBuiltInThemes();

    /// <summary>
    /// Gets all user-defined themes.
    /// </summary>
    public IReadOnlyList<IColorScheme> UserThemes => _registry.GetUserThemes();

    /// <summary>
    /// Applies a theme by name.
    /// User themes override built-in themes with the same name.
    /// Falls back to DarkPlus if the theme is not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>True if the theme was found and applied</returns>
    public bool ApplyTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var theme = _registry.GetByName(name);
        if (theme == null)
        {
            Console.Error.WriteLine($"[ThemeManager] Theme '{name}' not found, falling back to DarkPlus");
            theme = Abstractions.Themes.BuiltInThemes.DarkPlus;
        }

        return ApplyTheme(theme);
    }

    /// <summary>
    /// Applies a specific color scheme as the current theme.
    /// </summary>
    /// <param name="theme">The color scheme to apply</param>
    /// <returns>True if the theme was applied (false only if theme is null)</returns>
    public bool ApplyTheme(IColorScheme theme)
    {
        if (theme == null)
            return false;

        var previousTheme = _currentTheme;
        _currentTheme = theme;

        // Notify subscribers
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(previousTheme, theme));

        Console.WriteLine($"[ThemeManager] Applied theme with background 0x{theme.Background:X8}");
        
        return true;
    }

    /// <summary>
    /// Reloads user themes from disk.
    /// Call this after adding, removing, or modifying theme files.
    /// </summary>
    public void LoadUserThemes()
    {
        var previousUserThemes = UserThemes.ToList();
        
        _registry.Refresh();
        
        // If the current theme was a user theme that no longer exists, 
        // or if it was overridden, we may need to reapply
        var currentName = GetThemeName(_currentTheme);
        var newTheme = _registry.GetByName(currentName);
        
        if (newTheme != null && !ReferenceEquals(newTheme, _currentTheme))
        {
            // The theme was reloaded - reapply it
            ApplyTheme(newTheme);
        }
    }

    /// <summary>
    /// Checks if a theme is registered (either built-in or user-defined).
    /// </summary>
    /// <param name="name">Theme name to check</param>
    /// <returns>True if the theme exists</returns>
    public bool HasTheme(string name)
    {
        return _registry.GetByName(name) != null;
    }

    /// <summary>
    /// Gets a theme by name, or null if not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>The theme, or null if not found</returns>
    public IColorScheme? GetTheme(string name)
    {
        return _registry.GetByName(name);
    }

    /// <summary>
    /// Gets a theme by name, falling back to DarkPlus if not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>The requested theme, or DarkPlus if not found</returns>
    public IColorScheme GetThemeOrDefault(string name)
    {
        return _registry.GetByName(name) ?? Abstractions.Themes.BuiltInThemes.DarkPlus;
    }

    /// <summary>
    /// Determines if a theme is a dark theme based on background luminance.
    /// </summary>
    private static bool IsDarkTheme(IColorScheme theme)
    {
        // Calculate relative luminance of background
        var bg = theme.Background;
        var r = ((bg >> 16) & 0xFF) / 255.0;
        var g = ((bg >> 8) & 0xFF) / 255.0;
        var b = (bg & 0xFF) / 255.0;

        // Convert to linear RGB
        r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        
        // Dark if luminance < 0.5
        return luminance < 0.5;
    }

    /// <summary>
    /// Gets the name of a theme (if it's from the registry).
    /// </summary>
    private static string GetThemeName(IColorScheme theme)
    {
        // Try to find the theme in the registry to get its name
        // This is a heuristic - we can't directly get the name from IColorScheme
        // So we compare with known themes
        
        foreach (var builtin in Abstractions.Themes.BuiltInThemes.AllThemes)
        {
            if (ThemesEqual(theme, builtin))
            {
                // Find the name by checking which built-in theme this is
                return GetBuiltInThemeName(builtin);
            }
        }
        
        return "Unknown";
    }

    /// <summary>
    /// Compares two color schemes for equality.
    /// </summary>
    private static bool ThemesEqual(IColorScheme a, IColorScheme b)
    {
        return a.Background == b.Background &&
               a.Foreground == b.Foreground &&
               a.AnsiBlack == b.AnsiBlack &&
               a.AnsiRed == b.AnsiRed &&
               a.AnsiGreen == b.AnsiGreen &&
               a.AnsiYellow == b.AnsiYellow &&
               a.AnsiBlue == b.AnsiBlue &&
               a.AnsiMagenta == b.AnsiMagenta &&
               a.AnsiCyan == b.AnsiCyan &&
               a.AnsiWhite == b.AnsiWhite;
    }

    /// <summary>
    /// Gets the canonical name of a built-in theme.
    /// </summary>
    private static string GetBuiltInThemeName(IColorScheme theme)
    {
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.DarkPlus)) return "DarkPlus";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.Dracula)) return "Dracula";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.OneDark)) return "OneDark";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.GruvboxDark)) return "GruvboxDark";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.CatppuccinMocha)) return "CatppuccinMocha";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.TokyoNight)) return "TokyoNight";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.LightPlus)) return "LightPlus";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.OneLight)) return "OneLight";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.GruvboxLight)) return "GruvboxLight";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.CatppuccinLatte)) return "CatppuccinLatte";
        if (ReferenceEquals(theme, Abstractions.Themes.BuiltInThemes.SolarizedLight)) return "SolarizedLight";
        return "Unknown";
    }

    /// <summary>
    /// Gets the initial theme based on configuration.
    /// </summary>
    private static IColorScheme GetInitialTheme()
    {
        // For now, return the default DarkPlus theme
        // TODO: Integrate with generated config when ColorScheme implements IColorScheme
        return Abstractions.Themes.BuiltInThemes.DarkPlus;
    }
}

/// <summary>
/// Event arguments for theme change notifications.
/// </summary>
public sealed class ThemeChangedEventArgs : EventArgs
{
    /// <summary>
    /// The previous theme (may be null on initial load).
    /// </summary>
    public IColorScheme? PreviousTheme { get; }

    /// <summary>
    /// The new current theme.
    /// </summary>
    public IColorScheme NewTheme { get; }

    /// <summary>
    /// Creates new ThemeChangedEventArgs.
    /// </summary>
    public ThemeChangedEventArgs(IColorScheme? previousTheme, IColorScheme newTheme)
    {
        PreviousTheme = previousTheme;
        NewTheme = newTheme ?? throw new ArgumentNullException(nameof(newTheme));
    }
}
