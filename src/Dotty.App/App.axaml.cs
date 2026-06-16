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

    private static CSharpConfigWatcher? s_configWatcher;

    public override void OnFrameworkInitializationCompleted()
    {
        Program.BenchTimer?.Stage("avalon_framework_init");

        try
        {
            // Initialize theme manager (loads built-in + user themes)
            _themeManager = new ThemeManager();
            _themeManager.ThemeChanged += OnThemeChanged;
            Program.BenchTimer?.Stage("theme_manager_done");

            // Watch and compile the user's Config.cs at runtime.
            // Changes to ~/.config/dotty/Dotty.UserConfig/Config.cs are
            // automatically compiled and applied without restart.
            s_configWatcher = new CSharpConfigWatcher();
            s_configWatcher.ConfigCompiled += (_, settings) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RuntimeSettings.Apply(settings);
                });
            };
            // Defer initial compilation to background so the window appears first.
            // The watcher hot-reload will apply any subsequent changes instantly.
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTTY_SKIP_CONFIG_COMPILE")))
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var startupSettings = s_configWatcher.CompileAndLoad();
                    if (startupSettings != null)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            RuntimeSettings.Apply(startupSettings);
                        });
                    }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Program.BenchTimer?.Stage("config_watcher_done");
                    });
                });
            }
            else
            {
                Program.BenchTimer?.Stage("config_watcher_done");
            }
            s_configWatcher.Start();

            ApplyDefaultsToResources();
            Program.BenchTimer?.Stage("defaults_applied");

            // Re-apply resources when runtime settings change (font, colors, etc.)
            RuntimeSettings.Changed += (_, _) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { ApplyDefaultsToResources(); } catch { }
                });
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply defaults: {ex}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        Program.BenchTimer?.Stage("avalon_window_created");

        // Deferred: check for config NuGet version updates on a background thread.
        Program.RunDeferredConfigCheck();

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

    private static Color ParseRuntimeColorOrFallback(string color, uint fallbackArgb)
    {
        try
        {
            return ConfigBridge.ToColor(ConfigBridge.FromHex(color));
        }
        catch
        {
            return ConfigBridge.ToColor(fallbackArgb);
        }
    }

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

        var rs = RuntimeSettings.Current;
        var resources = Current.Resources;
        resources["TerminalFontFamily"] = FontResolver.ResolveFontFamily(Defaults.DefaultFontStack);
        resources["TerminalFontSize"] = Defaults.GetInitialFontSize();

        // Check if transparency is enabled
        var transparency = RuntimeSettings.GetTransparency();
        var windowOpacity = RuntimeSettings.GetWindowOpacity();
        var hasOpacity = windowOpacity < 100;
        var isTransparent = transparency != TransparencyLevel.None || hasOpacity;

        // Determine background and foreground colors
        string bgColorStr, fgColorStr;
        if (rs.Background != null) bgColorStr = rs.Background;
        else bgColorStr = Defaults.DefaultBackground;

        if (rs.Foreground != null) fgColorStr = rs.Foreground;
        else fgColorStr = Defaults.DefaultForeground;

        // Set terminal background - transparent if transparency or opacity enabled
        if (isTransparent)
        {
            resources["TerminalBackground"] = Brushes.Transparent;
            resources["TerminalBackgroundTransparent"] = Brushes.Transparent;
        }
        else
        {
            resources["TerminalBackground"] = new SolidColorBrush(ParseRuntimeColorOrFallback(bgColorStr, 0xFF1E1E1E));
            resources["TerminalBackgroundTransparent"] = new SolidColorBrush(ParseRuntimeColorOrFallback(bgColorStr, 0xFF1E1E1E));
        }

        resources["TerminalForeground"] = new SolidColorBrush(ParseRuntimeColorOrFallback(fgColorStr, 0xFFD4D4D4));

        // Tab bar colors from runtime or generated config
        uint tabBarArgb = global::Dotty.Generated.Config.TabBarBackgroundColor;
        if (rs.TabBarBackgroundColor != null)
        {
            try { tabBarArgb = ConfigBridge.FromHex(rs.TabBarBackgroundColor); } catch { }
        }
        resources["TabBarBackground"] = new SolidColorBrush(ConfigBridge.ToColor(tabBarArgb));
        resources["TabBarForeground"] = new SolidColorBrush(ParseRuntimeColorOrFallback(fgColorStr, 0xFFD4D4D4));

        // Apply the user's color theme to the terminal's ANSI palette
        ApplyAnsiColorPalette();
    }
    
    private static void ApplyAnsiColorPalette(IColorScheme? theme = null)
    {
        try
        {
            uint[] ansiPalette;
            var rs = RuntimeSettings.Current;

            // Check if runtime has ANSI colors
            if (rs.AnsiBlack != null)
            {
                ansiPalette = new uint[]
                {
                    SafeParseHex(rs.AnsiBlack),
                    SafeParseHex(rs.AnsiRed),
                    SafeParseHex(rs.AnsiGreen),
                    SafeParseHex(rs.AnsiYellow),
                    SafeParseHex(rs.AnsiBlue),
                    SafeParseHex(rs.AnsiMagenta),
                    SafeParseHex(rs.AnsiCyan),
                    SafeParseHex(rs.AnsiWhite),
                    SafeParseHex(rs.AnsiBrightBlack),
                    SafeParseHex(rs.AnsiBrightRed),
                    SafeParseHex(rs.AnsiBrightGreen),
                    SafeParseHex(rs.AnsiBrightYellow),
                    SafeParseHex(rs.AnsiBrightBlue),
                    SafeParseHex(rs.AnsiBrightMagenta),
                    SafeParseHex(rs.AnsiBrightCyan),
                    SafeParseHex(rs.AnsiBrightWhite)
                };
            }
            else if (theme != null)
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

    private static uint SafeParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return 0;
        try { return ConfigBridge.FromHex(hex); } catch { return 0; }
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
