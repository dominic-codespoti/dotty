using System;
using System.Threading;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Runtime.Config;
using Dotty.Runtime.Themes;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk.Config;

/// <summary>Configuration and theme helper for the Dotty.Silk host.</summary>
public static class SilkConfig
{
    private static IColorScheme? _cachedTheme;
    private static string? _cachedThemeName;
    private static int _systemPrefersDark = -1;

    internal static Func<bool?> SystemThemeProbe { get; set; } = SystemThemeDetector.DetectIsDark;

    /// <summary>The last system color-scheme preference; null means unknown.</summary>
    public static bool? SystemPrefersDark
    {
        get => Volatile.Read(ref _systemPrefersDark) switch { 0 => false, 1 => true, _ => null };
        private set => Volatile.Write(ref _systemPrefersDark, value switch { false => 0, true => 1, _ => -1 });
    }

    /// <summary>Refreshes the system preference. Must be called off the UI thread.</summary>
    public static bool RefreshSystemTheme()
    {
        bool? previous = SystemPrefersDark;
        bool? current = SystemThemeProbe();
        SystemPrefersDark = current;
        return previous != current;
    }

    public static string GetActiveThemeName()
    {
        var envTheme = Environment.GetEnvironmentVariable("DOTTY_THEME");
        return !string.IsNullOrWhiteSpace(envTheme)
            ? envTheme.Trim()
            : ResolveConfiguredThemeName(UserConfigService.Current);
    }

    /// <summary>Resolves themeAuto using only the last-known system preference.</summary>
    public static string ResolveConfiguredThemeName(DottyUserConfig config)
    {
        if (config == null)
            return DottyDefaults.DefaultThemeName;
        if (!config.ThemeAuto)
            return string.IsNullOrWhiteSpace(config.Theme) ? DottyDefaults.DefaultThemeName : config.Theme;
        if (SystemPrefersDark == false)
            return string.IsNullOrWhiteSpace(config.ThemeLight) ? "LightPlus" : config.ThemeLight!;
        string dark = string.IsNullOrWhiteSpace(config.ThemeDark) ? config.Theme : config.ThemeDark!;
        return string.IsNullOrWhiteSpace(dark) ? DottyDefaults.DefaultThemeName : dark;
    }

    public static IColorScheme LoadActiveTheme()
    {
        var envTheme = Environment.GetEnvironmentVariable("DOTTY_THEME");
        var themeName = !string.IsNullOrWhiteSpace(envTheme)
            ? envTheme.Trim()
            : ResolveConfiguredThemeName(UserConfigService.Current);
        if (_cachedTheme != null && string.Equals(_cachedThemeName, themeName, StringComparison.OrdinalIgnoreCase))
            return _cachedTheme;

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

    public static void ClearThemeCache()
    {
        _cachedTheme = null;
        _cachedThemeName = null;
    }

    public static SgrColorArgb ResolveForeground(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        return new SgrColorArgb(theme.Foreground);
    }

    public static SgrColorArgb ResolveBackground(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        return new SgrColorArgb(theme.Background);
    }

    public static SgrColorArgb ResolveSelectionColor(IColorScheme? theme = null)
    {
        var config = UserConfigService.Current;
        if (!string.IsNullOrWhiteSpace(config.SelectionColor))
        {
            try { return new SgrColorArgb(ColorSchemeBase.FromHex(config.SelectionColor)); }
            catch { }
        }
        return new SgrColorArgb(DottyDefaults.SelectionColor);
    }

    public static uint[] ResolveAnsiPalette(IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        return new uint[16]
        {
            theme.AnsiBlack, theme.AnsiRed, theme.AnsiGreen, theme.AnsiYellow,
            theme.AnsiBlue, theme.AnsiMagenta, theme.AnsiCyan, theme.AnsiWhite,
            theme.AnsiBrightBlack, theme.AnsiBrightRed, theme.AnsiBrightGreen, theme.AnsiBrightYellow,
            theme.AnsiBrightBlue, theme.AnsiBrightMagenta, theme.AnsiBrightCyan, theme.AnsiBrightWhite
        };
    }

    public static void ApplyThemeToAdapter(TerminalAdapter adapter, IColorScheme? theme = null)
    {
        if (adapter == null) return;
        theme ??= LoadActiveTheme();
        adapter.SetPaletteBaseline(ResolveAnsiPalette(theme));
        adapter.SetDefaultColors($"#{theme.Foreground & 0xFFFFFF:X6}", $"#{theme.Background & 0xFFFFFF:X6}");
    }

    public static (SgrColorArgb Foreground, SgrColorArgb Background) InitializeTheme(
        TerminalAdapter? adapter = null, IColorScheme? theme = null)
    {
        theme ??= LoadActiveTheme();
        if (adapter != null)
            ApplyThemeToAdapter(adapter, theme);
        return (ResolveForeground(theme), ResolveBackground(theme));
    }
}
