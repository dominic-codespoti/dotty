using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // Handle --check-updates flag
        if (args.Contains("--check-updates"))
        {
            if (ConfigGeneratorService.UpdatePackageVersionIfNeeded())
            {
                Console.WriteLine("\nUpdate complete! Run 'dotnet restore' to apply changes.");
            }
            else
            {
                Console.WriteLine("✓ Your config is up to date.");
            }
            return;
        }

        // Generate default config on first run (fast file existence check).
        // The version-update check is deferred to a background task below so it
        // does not block window creation during benchmarks.
        HandleFirstRunConfig(quickOnly: true);
        BenchTimer?.Stage("config_check_done");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        BenchTimer?.Stage("avalon_exit");
        BenchTimer?.Dispose();
    }

    /// <summary>
    /// Handles automatic config file generation on first startup.
    /// When quickOnly is true, skips the version-update check (deferred to
    /// a background task so it does not block window creation).
    /// </summary>
    private static void HandleFirstRunConfig(bool quickOnly = false)
    {
        // Handle --generate-config flag to force regeneration
        var cmdArgs = Environment.GetCommandLineArgs();
        bool forceRegenerate = cmdArgs.Contains("--generate-config");

        if (ConfigGeneratorService.EnsureConfigExists(forceRegenerate))
        {
            Console.WriteLine($"✓ Created Dotty configuration project:");
            Console.WriteLine($"  Location: {ConfigGeneratorService.ProjectDir}");
            Console.WriteLine();
            Console.WriteLine("  Files created:");
            Console.WriteLine($"    • Config.cs - Your configuration (edit this!)");
            Console.WriteLine($"    • Dotty.UserConfig.csproj - Project file with NuGet reference");
            Console.WriteLine();
            
            // Run dotnet restore to download the NuGet package for immediate IntelliSense
            Console.WriteLine("  Restoring NuGet packages for IntelliSense support...");
            RestoreConfigProject();
            
            Console.WriteLine();
            Console.WriteLine("  To customize your terminal:");
            Console.WriteLine($"    1. Open {ConfigGeneratorService.ProjectDir}/ in your IDE");
            Console.WriteLine("       (VS Code, Rider, or any C# editor)");
            Console.WriteLine("    2. Edit Config.cs with full IntelliSense support");
            Console.WriteLine($"    3. The Dotty.Abstractions package (v{ConfigGeneratorService.LatestPackageVersion}) from NuGet.org");
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
        else if (!quickOnly)
        {
            // Check for package updates on existing configs
            ConfigGeneratorService.UpdatePackageVersionIfNeeded();
        }
    }

    /// <summary>
    /// Runs the deferred config version check on a background thread so it
    /// does not delay window creation.
    /// </summary>
    internal static void RunDeferredConfigCheck()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTTY_SKIP_CONFIG_CHECK")))
            return;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { ConfigGeneratorService.UpdatePackageVersionIfNeeded(); }
            catch { }
        });
    }

    /// <summary>
    /// Runs dotnet restore on the config project to download NuGet packages.
    /// </summary>
    private static void RestoreConfigProject()
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"restore \"{ConfigGeneratorService.ProjectPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            process.WaitForExit(30000); // 30 second timeout
            
            if (process.ExitCode == 0)
            {
                Console.WriteLine("  ✓ NuGet packages restored successfully");
            }
            else
            {
                Console.WriteLine("  ⚠ NuGet restore had issues, but you can run it manually:");
                Console.WriteLine($"    cd {ConfigGeneratorService.ProjectDir}");
                Console.WriteLine("    dotnet restore");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ Could not auto-restore packages: {ex.Message}");
            Console.WriteLine("  You can restore manually by running:");
            Console.WriteLine($"    cd {ConfigGeneratorService.ProjectDir}");
            Console.WriteLine("    dotnet restore");
        }
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
