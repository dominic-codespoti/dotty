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
    // Dark themes
    
    /// <summary>
    /// VS Code Dark+ theme - the default theme for Dotty.
    /// A balanced dark theme with good contrast and readability.
    /// </summary>
    public static Config.IColorScheme DarkPlus => new DarkPlusTheme();

    /// <summary>
    /// Dracula theme - popular dark theme with vibrant colors.
    /// Features a dark purple background with bright, saturated colors.
    /// </summary>
    public static Config.IColorScheme Dracula => new DraculaTheme();

    /// <summary>
    /// One Dark theme - inspired by Atom editor.
    /// A subtle dark theme with muted colors.
    /// </summary>
    public static Config.IColorScheme OneDark => new OneDarkTheme();

    /// <summary>
    /// Gruvbox Dark theme - warm dark theme with earthy tones.
    /// Easy on the eyes for long coding sessions.
    /// </summary>
    public static Config.IColorScheme GruvboxDark => new GruvboxDarkTheme();

    /// <summary>
    /// Catppuccin Mocha theme - soothing dark theme with pastel colors.
    /// </summary>
    public static Config.IColorScheme CatppuccinMocha => new CatppuccinMochaTheme();

    /// <summary>
    /// Tokyo Night theme - modern dark theme with deep blues and purples.
    /// </summary>
    public static Config.IColorScheme TokyoNight => new TokyoNightTheme();

    // Light themes
    
    /// <summary>
    /// VS Code Light+ theme - a clean, bright theme.
    /// Good for well-lit environments.
    /// </summary>
    public static Config.IColorScheme LightPlus => new LightPlusTheme();

    /// <summary>
    /// One Light theme - the light counterpart to One Dark.
    /// </summary>
    public static Config.IColorScheme OneLight => new OneLightTheme();

    /// <summary>
    /// Gruvbox Light theme - warm light theme.
    /// </summary>
    public static Config.IColorScheme GruvboxLight => new GruvboxLightTheme();

    /// <summary>
    /// Catppuccin Latte theme - light counterpart to Catppuccin Mocha.
    /// </summary>
    public static Config.IColorScheme CatppuccinLatte => new CatppuccinLatteTheme();

    /// <summary>
    /// Solarized Light theme - carefully selected low-contrast colors.
    /// Designed to reduce eye strain.
    /// </summary>
    public static Config.IColorScheme SolarizedLight => new SolarizedLightTheme();

    /// <summary>
    /// Gets all available dark themes.
    /// </summary>
    public static Config.IColorScheme[] DarkThemes => new[]
    {
        DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha, TokyoNight
    };

    /// <summary>
    /// Gets all available light themes.
    /// </summary>
    public static Config.IColorScheme[] LightThemes => new[]
    {
        LightPlus, OneLight, GruvboxLight, CatppuccinLatte, SolarizedLight
    };

    /// <summary>
    /// Gets all available themes (both dark and light).
    /// </summary>
    public static Config.IColorScheme[] AllThemes => new[]
    {
        DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha, TokyoNight,
        LightPlus, OneLight, GruvboxLight, CatppuccinLatte, SolarizedLight
    };

    /// <summary>
    /// Gets a theme by name, or returns the default (DarkPlus) if not found.
    /// </summary>
    /// <param name="name">Theme name (case-insensitive)</param>
    /// <returns>The requested theme, or DarkPlus if not found</returns>
    public static Config.IColorScheme GetByName(string name)
    {
        return name?.ToLowerInvariant() switch
        {
            "darkplus" or "dark-plus" or "vscode-dark" => DarkPlus,
            "dracula" => Dracula,
            "onedark" or "one-dark" or "atom-dark" => OneDark,
            "gruvboxdark" or "gruvbox-dark" => GruvboxDark,
            "catppuccinmocha" or "catppuccin-mocha" => CatppuccinMocha,
            "tokyonight" or "tokyo-night" => TokyoNight,
            "lightplus" or "light-plus" or "vscode-light" => LightPlus,
            "onelight" or "one-light" => OneLight,
            "gruvboxlight" or "gruvbox-light" => GruvboxLight,
            "catppuccinlatte" or "catppuccin-latte" => CatppuccinLatte,
            "solarizedlight" or "solarized-light" => SolarizedLight,
            _ => DarkPlus  // Default fallback
        };
    }
}
