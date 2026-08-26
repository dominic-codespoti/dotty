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
        // Surface Avalonia's internal error logs (compositor/GL init failures
        // log via Trace at Error level; the default listener drops them).
        System.Diagnostics.Trace.AutoFlush = true;
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));

        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace()
            .UseSkia();

        // X11/XWayland by default. The native Wayland backend
        // (Avalonia.Wayland 12.1, experimental) has two verified blockers for
        // a terminal workload (2026-08-26, Hyprland/radeonsi):
        //   1. Animation-frame callbacks stall for seconds under paced
        //      output (bursts of ~10 callbacks, then multi-second gaps);
        //   2. DispatcherTimers starve in the same conditions (a 50 ms
        //      Background-priority timer fired once in ~10 s), so watchdogs
        //      built on timers fail with it.
        // A dedicated watchdog thread (TerminalView) mitigates (1) partially,
        // but smooth output requires upstream fixes. Opt in via
        // DOTTY_WAYLAND=1.
        var isWindows = OperatingSystem.IsWindows();
        var isMacOS = OperatingSystem.IsMacOS();

        if (!isWindows && !isMacOS
            && Environment.GetEnvironmentVariable("DOTTY_WAYLAND") == "1"
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            builder.UseWayland().UseHarfBuzz();
        }
        else
        {
            builder.UsePlatformDetect();
        }

        // X11 present stall workaround: the Glx present path stalls under sparse
        // updates (buffer/UI thread correct, screen frozen — reproduced on main
        // with ED(2) clears). Software rendering (XShmPutImage) does not stall
        // in the same conditions. Force it for the bitmap default; the GL path
        // (DOTTY_GL=1) keeps Glx for hardware acceleration. Use reflection to
        // avoid a direct Avalonia.X11 package reference (transitive only).
        if (!isWindows && !isMacOS && Environment.GetEnvironmentVariable("DOTTY_GL") != "1")
        {
            try
            {
                var optsType = Type.GetType("Avalonia.X11.X11PlatformOptions, Avalonia.X11");
                var modeType = Type.GetType("Avalonia.X11.X11RenderingMode, Avalonia.X11");
                if (optsType != null && modeType != null)
                {
                    var opts = Activator.CreateInstance(optsType)!;
                    var software = Enum.Parse(modeType, "Software");
                    var arr = Array.CreateInstance(modeType, 1);
                    arr.SetValue(software, 0);
                    optsType.GetProperty("RenderingMode")!.SetValue(opts, arr);
                    var withMethod = typeof(Avalonia.AppBuilder).GetMethods()
                        .First(m => m.Name == "With" && m.IsGenericMethodDefinition)
                        .MakeGenericMethod(optsType);
                    withMethod.Invoke(builder, new[] { opts });
                }
            }
            catch { }
        }

        return builder;
    }
}
