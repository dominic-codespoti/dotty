using System;
using System.IO;
using System.Linq;
using Avalonia;
using Dotty.App.Services;

namespace Dotty.App;

static class Program
{
    public static void Main(string[] args)
    {
        // Optional global silencing of Console output to avoid flooding the
        // terminal when debugging/logging is still present in code. Set
        // DOTTY_DISABLE_LOGGING=1 in your environment to enable this.
        // Environment-driven global silencing removed; keep Console as-is.

        // Generate default config on first run
        HandleFirstRunConfig();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Handles automatic config file generation on first startup.
    /// Creates a default Config.cs if none exists.
    /// </summary>
    private static void HandleFirstRunConfig()
    {
        // Handle --generate-config flag to force regeneration
        var args = Environment.GetCommandLineArgs();
        bool forceRegenerate = args.Contains("--generate-config");

        if (ConfigGeneratorService.EnsureConfigExists(forceRegenerate))
        {
            var path = ConfigGeneratorService.GetExistingConfigPath();
            if (!string.IsNullOrEmpty(path))
            {
                Console.WriteLine($"✓ Created default config: {path}");
                Console.WriteLine("  Edit this file to customize your terminal, then rebuild to apply changes.");
                Console.WriteLine();
            }
        }
        else if (forceRegenerate)
        {
            var path = ConfigGeneratorService.GetExistingConfigPath();
            Console.WriteLine($"✓ Regenerated config: {path}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace()
            .UsePlatformDetect()
            .UseSkia();

        return builder;
    }
}
