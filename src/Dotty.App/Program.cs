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
    /// Creates a full .csproj with NuGet reference for LSP support.
    /// </summary>
    private static void HandleFirstRunConfig()
    {
        // Handle --generate-config flag to force regeneration
        var args = Environment.GetCommandLineArgs();
        bool forceRegenerate = args.Contains("--generate-config");

        if (ConfigGeneratorService.EnsureConfigExists(forceRegenerate))
        {
            Console.WriteLine($"✓ Created Dotty configuration project:");
            Console.WriteLine($"  Location: {ConfigGeneratorService.ProjectDir}");
            Console.WriteLine();
            Console.WriteLine("  Files created:");
            Console.WriteLine($"    • Config.cs - Your configuration (edit this!)");
            Console.WriteLine($"    • Dotty.UserConfig.csproj - Project file with NuGet reference");
            Console.WriteLine();
            Console.WriteLine("  To customize your terminal:");
            Console.WriteLine($"    1. Open {ConfigGeneratorService.ProjectDir}/ in your IDE");
            Console.WriteLine("       (VS Code, Rider, or any C# editor)");
            Console.WriteLine("    2. Edit Config.cs with full IntelliSense support");
            Console.WriteLine("    3. The Dotty.Abstractions package (v0.1.0) from NuGet.org");
            Console.WriteLine("       provides all themes, types, and documentation");
            Console.WriteLine("    4. Restart Dotty to apply changes");
            Console.WriteLine();
            Console.WriteLine("  Package: https://www.nuget.org/packages/Dotty.Abstractions/");
            Console.WriteLine();
        }
        else if (forceRegenerate)
        {
            Console.WriteLine($"✓ Regenerated config at: {ConfigGeneratorService.ConfigPath}");
            Console.WriteLine($"  Project: {ConfigGeneratorService.ProjectPath}");
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
