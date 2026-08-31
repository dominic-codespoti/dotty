using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Runtime.Config;
using Dotty.Runtime.Themes;
using Dotty.Terminal.Adapter;
namespace Dotty.Silk.Config;

/// <summary>
/// Configuration and theme helper for Dotty.Silk host.
/// Resolves active theme, color values, ANSI palette, and applies them to the terminal runtime.
/// </summary>
public static class SilkConfig
{
    private static IColorScheme? _cachedTheme;
    private static string? _cachedThemeName;

    /// <summary>
    /// Gets the active theme name checking the DOTTY_THEME environment variable,
    /// falling back to DottyDefaults.DefaultThemeName ("DarkPlus").
    /// </summary>
    public static string GetActiveThemeName()
    {
        var envTheme = Environment.GetEnvironmentVariable("DOTTY_THEME");
        if (!string.IsNullOrWhiteSpace(envTheme))
        {
            return envTheme.Trim();
        }

        return DottyDefaults.DefaultThemeName;
    }

    /// <summary>
    /// Loads the active theme based on DOTTY_THEME environment variable or default fallback.
    /// Supports both built-in themes and user-defined themes loaded via ThemeRegistry.
    /// </summary>
    public static IColorScheme LoadActiveTheme()
    {
        var themeName = GetActiveThemeName();

        if (_cachedTheme != null && string.Equals(_cachedThemeName, themeName, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedTheme;
        }

        IColorScheme theme;
        try
        {
            var registry = new ThemeRegistry();
            theme = registry.GetByNameOrDefault(themeName, DottyDefaults.DefaultColorScheme);
        }
        catch
        {
            theme = BuiltInThemes.GetByName(themeName);
        }

        _cachedTheme = theme;
        _cachedThemeName = themeName;
        return theme;
    }

    /// <summary>
    /// Resolves the foreground color of the specified (or active) theme as SgrColorArgb.
    /// </summary>
    public static SgrColorArgb ResolveForeground(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        return new SgrColorArgb(theme.Foreground);
    }

    /// <summary>
    /// Resolves the background color of the specified (or active) theme as SgrColorArgb.
    /// </summary>
    public static SgrColorArgb ResolveBackground(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        return new SgrColorArgb(theme.Background);
    }

    /// <summary>
    /// Resolves the selection color of the specified (or active) theme / defaults as SgrColorArgb.
    /// </summary>
    public static SgrColorArgb ResolveSelectionColor(IColorScheme? theme = null)
    {
        var config = UserConfigService.Current;
        if (!string.IsNullOrWhiteSpace(config.SelectionColor))
        {
            try
            {
                return new SgrColorArgb(ColorSchemeBase.FromHex(config.SelectionColor));
            }
            catch
            {
            }
        }

        return new SgrColorArgb(DottyDefaults.SelectionColor);
    }

    /// <summary>
    /// Resolves the 16-color ANSI palette for the specified (or active) theme.
    /// </summary>
    public static uint[] ResolveAnsiPalette(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();

        return new uint[16]
        {
            theme.AnsiBlack,
            theme.AnsiRed,
            theme.AnsiGreen,
            theme.AnsiYellow,
            theme.AnsiBlue,
            theme.AnsiMagenta,
            theme.AnsiCyan,
            theme.AnsiWhite,
            theme.AnsiBrightBlack,
            theme.AnsiBrightRed,
            theme.AnsiBrightGreen,
            theme.AnsiBrightYellow,
            theme.AnsiBrightBlue,
            theme.AnsiBrightMagenta,
            theme.AnsiBrightCyan,
            theme.AnsiBrightWhite
        };
    }

    /// <summary>
    /// Applies the theme's 16-color ANSI palette to SgrColorArgb globally.
    /// </summary>
    public static void ApplyAnsiPalette(IColorScheme? theme = null)
    {
        var palette = ResolveAnsiPalette(theme);
        SgrColorArgb.SetAnsiPalette(palette);
    }

    /// <summary>
    /// Applies the default foreground and background colors to a TerminalAdapter.
    /// </summary>
    public static void ApplyThemeToAdapter(TerminalAdapter adapter, IColorScheme? theme = null)
    {
        if (adapter == null) return;
        theme ??= LoadActiveTheme();

        string fgHex = $"#{theme.Foreground & 0xFFFFFF:X6}";
        string bgHex = $"#{theme.Background & 0xFFFFFF:X6}";
        adapter.SetDefaultColors(fgHex, bgHex);
    }

    /// <summary>
    /// Initializes theme system by applying ANSI palette and resolving initial colors.
    /// </summary>
    public static (SgrColorArgb Foreground, SgrColorArgb Background) InitializeTheme(TerminalAdapter? adapter = null)
    {
        var theme = LoadActiveTheme();
        ApplyAnsiPalette(theme);

        if (adapter != null)
        {
            ApplyThemeToAdapter(adapter, theme);
        }

        return (ResolveForeground(theme), ResolveBackground(theme));
    }
}
