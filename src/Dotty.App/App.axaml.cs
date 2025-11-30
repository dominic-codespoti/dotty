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