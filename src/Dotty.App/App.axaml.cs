using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dotty.Abstractions.Config;
using Dotty.App.Services;
using Dotty.App.Views;
using Dotty.App.Configuration;
using Dotty.Terminal.Adapter;

namespace Dotty.App;

public partial class App : Application
{
    private static ThemeManager? _themeManager;
    
    /// <summary>
    /// Gets the global ThemeManager instance for runtime theme management.
    /// </summary>
    public static ThemeManager ThemeManager => _themeManager ??= new ThemeManager();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // Initialize theme manager (loads built-in + user themes)
            _themeManager = new ThemeManager();
            _themeManager.ThemeChanged += OnThemeChanged;
            Console.WriteLine($"[App] ThemeManager initialized with {_themeManager.AvailableThemes.Count} themes");
            
            ApplyDefaultsToResources();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply defaults: {ex}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
    
    /// <summary>
    /// Handles theme changes - updates application resources.
    /// </summary>
    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (Current == null)
            return;
            
        var theme = e.NewTheme;
        var resources = Current.Resources;
        
        // Update background and foreground brushes
        resources["TerminalBackground"] = new SolidColorBrush(ConfigBridge.ToColor(theme.Background));
        resources["TerminalForeground"] = new SolidColorBrush(ConfigBridge.ToColor(theme.Foreground));
        resources["TerminalBackgroundTransparent"] = new SolidColorBrush(ConfigBridge.ToColor(theme.Background));
        resources["TabBarForeground"] = new SolidColorBrush(ConfigBridge.ToColor(theme.Foreground));
        
        // Re-apply ANSI palette with new theme colors
        ApplyAnsiColorPalette(theme);
        
        Console.WriteLine($"[App] Theme changed to background 0x{theme.Background:X8}");
    }

    private static readonly bool ShouldLogFontResolution =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTTY_LOG_FONT_RESOLUTION"));

    private static void ApplyDefaultsToResources()
    {
        if (Current == null)
        {
            return;
        }

        if (ShouldLogFontResolution)
        {
            FontResolver.FontResolved += OnTerminalFontResolved;
        }

        var resources = Current.Resources;
        resources["TerminalFontFamily"] = FontResolver.ResolveFontFamily(Defaults.DefaultFontStack);
        resources["TerminalFontSize"] = Defaults.GetInitialFontSize();

        // Check if transparency is enabled
        var transparency = global::Dotty.Generated.Config.Transparency;
        var windowOpacity = global::Dotty.Generated.Config.WindowOpacity;
        var hasOpacity = windowOpacity < 100;
        var isTransparent = transparency != TransparencyLevel.None || hasOpacity;

        // Set terminal background - transparent if transparency or opacity enabled
        if (isTransparent)
        {
            // Use transparent background so window transparency shows through
            resources["TerminalBackground"] = Brushes.Transparent;
            resources["TerminalBackgroundTransparent"] = Brushes.Transparent;
        }
        else
        {
            // Solid background for fully opaque mode
            try { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultBackground)); } catch { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse("#801E1E1E")); }
            try { resources["TerminalBackgroundTransparent"] = new SolidColorBrush(Color.Parse(Defaults.DefaultBackground)); } catch { resources["TerminalBackgroundTransparent"] = new SolidColorBrush(Color.Parse("#801E1E1E")); }
        }
        
        try { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultForeground)); } catch { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse("#D4D4D4")); }
        
        // Add tab bar background and foreground resources from theme
        resources["TabBarBackground"] = new SolidColorBrush(ConfigBridge.ToColor(global::Dotty.Generated.Config.TabBarBackgroundColor));
        resources["TabBarForeground"] = new SolidColorBrush(ConfigBridge.ToColor(global::Dotty.Generated.Config.Foreground));
        
        // Apply the user's color theme to the terminal's ANSI palette
        ApplyAnsiColorPalette();
    }
    
    private static void ApplyAnsiColorPalette(IColorScheme? theme = null)
    {
        try
        {
            uint[] ansiPalette;
            
            if (theme != null)
            {
                // Use provided theme
                ansiPalette = new uint[]
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
            else
            {
                // Fall back to generated config
                var colors = Generated.Config.Colors;
                ansiPalette = new uint[]
                {
                    colors.AnsiBlack,
                    colors.AnsiRed,
                    colors.AnsiGreen,
                    colors.AnsiYellow,
                    colors.AnsiBlue,
                    colors.AnsiMagenta,
                    colors.AnsiCyan,
                    colors.AnsiWhite,
                    colors.AnsiBrightBlack,
                    colors.AnsiBrightRed,
                    colors.AnsiBrightGreen,
                    colors.AnsiBrightYellow,
                    colors.AnsiBrightBlue,
                    colors.AnsiBrightMagenta,
                    colors.AnsiBrightCyan,
                    colors.AnsiBrightWhite
                };
            }
            
            SgrColorArgb.SetAnsiPalette(ansiPalette);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply ANSI color palette: {ex.Message}");
        }
    }

    private static void OnTerminalFontResolved(FontFamily family)
    {
        try
        {
            Console.WriteLine($"[dotty] Terminal font resolved: {family.Name}");
        }
        finally
        {
            FontResolver.FontResolved -= OnTerminalFontResolved;
        }
    }
}