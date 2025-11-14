using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dotty.App.Services;

namespace Dotty.App;

public partial class App : Application
{
    public static SettingsService? Settings { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Load user settings and apply to application resources so controls can use DynamicResource keys
        try
        {
            Settings = new SettingsService();
            Settings.Load();
            ApplySettingsToResources(Settings.Current);

            // Subscribe to future changes and start file watching for auto-reload
            Settings.SettingsChanged += s => ApplySettingsToResources(s);
            Settings.StartWatching();
        }
        catch (Exception ex)
        {
            // Swallow - we don't want settings failures to block app startup
            Console.Error.WriteLine($"Failed to load settings: {ex}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplySettingsToResources(UserSettings settings)
    {
        if (Application.Current == null)
            return;

        var resources = Application.Current.Resources;

    // Font family and size
    // TerminalFontFamily resource must be a FontFamily instance (not a plain string)
    resources["TerminalFontFamily"] = new FontFamily(settings.FontFamily ?? "Consolas, Menlo, monospace");
        resources["TerminalFontSize"] = settings.FontSize;

        // Colors as brushes
        try
        {
            resources["TerminalBackground"] = new SolidColorBrush(Color.Parse(settings.Background ?? "#1E1E1E"));
        }
        catch { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse("#1E1E1E")); }

        try
        {
            resources["TerminalForeground"] = new SolidColorBrush(Color.Parse(settings.Foreground ?? "#D4D4D4"));
        }
        catch { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse("#D4D4D4")); }
    }
}