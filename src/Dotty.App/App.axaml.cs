using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dotty.App.Services;
using Dotty.App.Views;

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

        try { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultBackground)); } catch { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse("#801E1E1E")); }
        try { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultForeground)); } catch { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse("#D4D4D4")); }
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