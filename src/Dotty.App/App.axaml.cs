using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dotty.App.Services;

namespace Dotty.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
            try
            {
                ApplyDefaultsToResources();
                // Run glyph diagnostics only when explicitly requested via env var
                if (Environment.GetEnvironmentVariable("DOTTY_GLYPH_DIAG") == "1")
                {
                    GlyphDiagnostics.Run();
                }
            // Support a probe mode to scan installed fonts for glyph coverage when
            // DOTTY_PROBE_FONTS=1 is set. This is useful for local diagnostics and
            // does not start the main window.
            if (Environment.GetEnvironmentVariable("DOTTY_PROBE_FONTS") == "1")
            {
                Tools.FontProbe.Run();
                Environment.Exit(0);
            }
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

    private static void ApplyDefaultsToResources()
    {
        if (Current == null)
        {
            return;
        }

        var resources = Current.Resources;
        resources["TerminalFontFamily"] = FontResolver.ResolveFontFamily(Defaults.DefaultFontStack);
        resources["TerminalFontSize"] = Defaults.DefaultFontSize;

        try { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultBackground)); } catch { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse("#1E1E1E")); }
        try { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultForeground)); } catch { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse("#D4D4D4")); }
    }
}