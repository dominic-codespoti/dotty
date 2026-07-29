using System;
using System.IO;
using System.Linq;
using Avalonia;
using Dotty.App.Services;

namespace Dotty.App;

static class Program
{
    internal static StartupTimer? BenchTimer;

    public static void Main(string[] args)
    {
        // Start optional benchmark timer.
        var logPath = Environment.GetEnvironmentVariable("DOTTY_BENCH_STARTUP_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
            BenchTimer = new StartupTimer(logPath);

        BenchTimer?.Stage("main_entry");

        // Handle --version flag
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine(Dotty.VersionInfo.GetDetailedVersionString());
            return;
        }


        // Generate the default config on first run (fast file existence check).
        HandleFirstRunConfig();
        BenchTimer?.Stage("config_check_done");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        BenchTimer?.Stage("avalon_exit");
        BenchTimer?.Dispose();
    }

    /// <summary>
    /// Handles automatic config file generation on first startup.
    /// </summary>
    private static void HandleFirstRunConfig()
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        bool forceRegenerate = cmdArgs.Contains("--generate-config");

        if (ConfigGeneratorService.EnsureConfigExists(forceRegenerate))
        {
            Console.WriteLine("✓ Created Dotty configuration file:");
            Console.WriteLine($"  Location: {ConfigGeneratorService.ConfigPath}");
            Console.WriteLine();
            Console.WriteLine("  Files created:");
            Console.WriteLine("    • Config.cs - Your configuration (edit this!)");
            Console.WriteLine($"    • {Path.GetFileName(ConfigGeneratorService.EditorProjectPath)} - Editor/LSP project");
            Console.WriteLine();
            Console.WriteLine("  Edit the file and restart Dotty to apply changes.");
            PrintEditorRestoreHint();
        }
        else if (forceRegenerate)
        {
            Console.WriteLine($"✓ Regenerated config at: {ConfigGeneratorService.ConfigPath}");
            if (ConfigGeneratorService.EditorProjectWasCreated)
                PrintEditorRestoreHint();
        }
        else if (ConfigGeneratorService.EditorProjectWasCreated)
        {
            Console.WriteLine("✓ Created editor project for IntelliSense support.");
            PrintEditorRestoreHint();
        }
    }

    private static void PrintEditorRestoreHint()
    {
        Console.WriteLine("  Tip: Run the following to enable IntelliSense:");
        Console.WriteLine($"    dotnet restore {ConfigGeneratorService.EditorProjectPath}");
        Console.WriteLine();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace()
            .UsePlatformDetect()
            .UseSkia();

        // GPU rendering is handled automatically by the platform backend:
        // - Wayland: compositor uses GPU composition by default
        // - X11: falls back to software if GLX/EGL unavailable
        // - macOS: uses Metal via Skia
        // The terminal content itself is rasterized by Skia (CPU, AVX2-accelerated)
        // and composited as a texture by Avalonia.  This path is already fast
        // enough (>50 MiB/s throughput) that a dedicated GPU renderer would not
        // move the needle for typical terminal workloads.
        return builder;
    }
}
