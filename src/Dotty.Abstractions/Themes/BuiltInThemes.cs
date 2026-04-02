using Dotty.Abstractions.Config;

namespace Dotty.Abstractions.Themes;

/// <summary>
/// Built-in theme presets for Dotty terminal emulator.
/// Use these themes directly in your configuration.
/// </summary>
/// <example>
/// public IColorScheme? Colors => BuiltInThemes.DarkPlus;
/// </example>
public static class BuiltInThemes
{
    // Cache for theme instances to avoid recreating
    private static readonly Dictionary<string, IColorScheme> _themeCache = new(StringComparer.OrdinalIgnoreCase);

    // Lazy-initialized theme instances
    private static readonly Lazy<IColorScheme> _darkPlus = new(() => GetOrCreateTheme("DarkPlus"));
    private static readonly Lazy<IColorScheme> _dracula = new(() => GetOrCreateTheme("Dracula"));
    private static readonly Lazy<IColorScheme> _oneDark = new(() => GetOrCreateTheme("OneDark"));
    private static readonly Lazy<IColorScheme> _gruvboxDark = new(() => GetOrCreateTheme("GruvboxDark"));
    private static readonly Lazy<IColorScheme> _catppuccinMocha = new(() => GetOrCreateTheme("CatppuccinMocha"));
    private static readonly Lazy<IColorScheme> _tokyoNight = new(() => GetOrCreateTheme("TokyoNight"));
    private static readonly Lazy<IColorScheme> _lightPlus = new(() => GetOrCreateTheme("LightPlus"));
    private static readonly Lazy<IColorScheme> _oneLight = new(() => GetOrCreateTheme("OneLight"));
    private static readonly Lazy<IColorScheme> _gruvboxLight = new(() => GetOrCreateTheme("GruvboxLight"));
    private static readonly Lazy<IColorScheme> _catppuccinLatte = new(() => GetOrCreateTheme("CatppuccinLatte"));
    private static readonly Lazy<IColorScheme> _solarizedLight = new(() => GetOrCreateTheme("SolarizedLight"));

    /// <summary>
    /// Gets or creates a cached theme instance by name.
    /// </summary>
    private static IColorScheme GetOrCreateTheme(string themeName)
    {
        if (_themeCache.TryGetValue(themeName, out var cached))
        {
            return cached;
        }

        var definition = BuiltInThemeRegistry.GetByNameOrDefault(themeName);
        if (definition == null)
        {
            // Fallback to DarkPlus if somehow registry fails
            definition = BuiltInThemeRegistry.GetByNameOrDefault("DarkPlus")!;
        }

        var theme = new JsonBackedColorScheme(definition);
        _themeCache[themeName] = theme;
        return theme;
    }

    // Dark themes
    
    /// <summary>
    /// VS Code Dark+ theme - the default theme for Dotty.
    /// A balanced dark theme with good contrast and readability.
    /// </summary>
    public static IColorScheme DarkPlus => _darkPlus.Value;

    /// <summary>
    /// Dracula theme - popular dark theme with vibrant colors.
    /// Features a dark purple background with bright, saturated colors.
    /// </summary>
    public static IColorScheme Dracula => _dracula.Value;

    /// <summary>
    /// One Dark theme - inspired by Atom editor.
    /// A subtle dark theme with muted colors.
    /// </summary>
    public static IColorScheme OneDark => _oneDark.Value;

    /// <summary>
    /// Gruvbox Dark theme - warm dark theme with earthy tones.
    /// Easy on the eyes for long coding sessions.
    /// </summary>
    public static IColorScheme GruvboxDark => _gruvboxDark.Value;

    /// <summary>
    /// Catppuccin Mocha theme - soothing dark theme with pastel colors.
    /// </summary>
    public static IColorScheme CatppuccinMocha => _catppuccinMocha.Value;

    /// <summary>
    /// Tokyo Night theme - modern dark theme with deep blues and purples.
    /// </summary>
    public static IColorScheme TokyoNight => _tokyoNight.Value;

    // Light themes
    
    /// <summary>
    /// VS Code Light+ theme - a clean, bright theme.
    /// Good for well-lit environments.
    /// </summary>
    public static IColorScheme LightPlus => _lightPlus.Value;

    /// <summary>
    /// One Light theme - the light counterpart to One Dark.
    /// </summary>
    public static IColorScheme OneLight => _oneLight.Value;

    /// <summary>
    /// Gruvbox Light theme - warm light theme.
    /// </summary>
    public static IColorScheme GruvboxLight => _gruvboxLight.Value;

    /// <summary>
    /// Catppuccin Latte theme - light counterpart to Catppuccin Mocha.
    /// </summary>
    public static IColorScheme CatppuccinLatte => _catppuccinLatte.Value;

    /// <summary>
    /// Solarized Light theme - carefully selected low-contrast colors.
    /// Designed to reduce eye strain.
    /// </summary>
    public static IColorScheme SolarizedLight => _solarizedLight.Value;

    /// <summary>
    /// Gets all available dark themes.
    /// </summary>
    public static IColorScheme[] DarkThemes => new[]
    {
        DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha, TokyoNight
    };

    /// <summary>
    /// Gets all available light themes.
    /// </summary>
    public static IColorScheme[] LightThemes => new[]
    {
        LightPlus, OneLight, GruvboxLight, CatppuccinLatte, SolarizedLight
    };

    /// <summary>
    /// Gets all available themes (both dark and light).
    /// </summary>
    public static IColorScheme[] AllThemes => new[]
    {
        DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha, TokyoNight,
        LightPlus, OneLight, GruvboxLight, CatppuccinLatte, SolarizedLight
    };

    /// <summary>
    /// Gets a theme by name, or returns the default (DarkPlus) if not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>The requested theme, or DarkPlus if not found</returns>
    public static IColorScheme GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DarkPlus;

        // Check cache first
        if (_themeCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var definition = BuiltInThemeRegistry.GetByNameOrDefault(name);
        if (definition == null)
            return DarkPlus;

        // Use the canonical name for consistent caching
        var theme = GetOrCreateTheme(definition.CanonicalName ?? "DarkPlus");
        
        // Also cache by the requested name for faster future lookups
        if (!string.Equals(name, definition.CanonicalName, StringComparison.OrdinalIgnoreCase))
        {
            _themeCache[name] = theme;
        }

        return theme;
    }
}

/// <summary>
/// Public color scheme implementation that wraps a ThemeDefinition and inherits from ColorSchemeBase.
/// This allows themes to be defined in themes.json while maintaining the IColorScheme interface.
/// </summary>
public sealed class JsonBackedColorScheme : ColorSchemeBase
{
    private readonly ThemeDefinition _definition;

    /// <summary>
    /// Creates a new JsonBackedColorScheme from a ThemeDefinition.
    /// </summary>
    /// <param name="definition">The theme definition containing colors and metadata</param>
    public JsonBackedColorScheme(ThemeDefinition definition)
        : base(
            FromHex(definition.Colors?.Background ?? "#1E1E1E"),
            FromHex(definition.Colors?.Foreground ?? "#D4D4D4"),
            FromHex(definition.Colors?.GetAnsiColor(0) ?? "#000000"),
            FromHex(definition.Colors?.GetAnsiColor(1) ?? "#CD3131"),
            FromHex(definition.Colors?.GetAnsiColor(2) ?? "#0DBC79"),
            FromHex(definition.Colors?.GetAnsiColor(3) ?? "#E5E510"),
            FromHex(definition.Colors?.GetAnsiColor(4) ?? "#2472C8"),
            FromHex(definition.Colors?.GetAnsiColor(5) ?? "#BC3FBC"),
            FromHex(definition.Colors?.GetAnsiColor(6) ?? "#11A8CD"),
            FromHex(definition.Colors?.GetAnsiColor(7) ?? "#E5E5E5"),
            FromHex(definition.Colors?.GetAnsiColor(8) ?? "#666666"),
            FromHex(definition.Colors?.GetAnsiColor(9) ?? "#F14C4C"),
            FromHex(definition.Colors?.GetAnsiColor(10) ?? "#23D18B"),
            FromHex(definition.Colors?.GetAnsiColor(11) ?? "#F5F543"),
            FromHex(definition.Colors?.GetAnsiColor(12) ?? "#3B8EEA"),
            FromHex(definition.Colors?.GetAnsiColor(13) ?? "#D670D6"),
            FromHex(definition.Colors?.GetAnsiColor(14) ?? "#29B8DB"),
            FromHex(definition.Colors?.GetAnsiColor(15) ?? "#E5E5E5"))
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>
    /// Window background opacity as a value from 0 to 100.
    /// Converts the ThemeDefinition's double opacity (0.0-1.0) to byte (0-100).
    /// </summary>
    public override byte Opacity
    {
        get
        {
            var opacityValue = _definition.Colors?.Opacity ?? 1.0;
            // Convert from 0.0-1.0 range to 0-100 range
            var clamped = opacityValue * 100;
            if (clamped < 0) clamped = 0;
            if (clamped > 100) clamped = 100;
            return (byte)clamped;
        }
    }
}
