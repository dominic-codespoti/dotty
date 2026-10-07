using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dotty.Silk.Config;
using Xunit;

namespace Dotty.App.Tests;

public sealed class SystemThemeDetectorTests
{
    [Theory]
    [InlineData("busctl", "v v u 1", true)]
    [InlineData("gdbus", "(<<uint32 1>>,)\n", true)]
    [InlineData("dbus-send", "   variant       variant          uint32 2", false)]
    public void PortalRecognizesRealCommandOutput(string tool, string output, bool expected)
    {
        var runner = new FakeRunner((name, _) => name == tool ? new ProcessResult(true, 0, output) : ProcessResult.Failure);
        Assert.Equal(expected, SystemThemeDetector.DetectLinux(runner));
    }

    [Fact]
    public void PortalNoPreferenceFallsThroughToGSettingsColorScheme()
    {
        var runner = new FakeRunner((name, args) =>
        {
            if (name == "busctl") return new ProcessResult(true, 0, "v v u 0");
            if (name == "gsettings" && args.Count > 2 && args[2] == "color-scheme")
                return new ProcessResult(true, 0, "'prefer-dark'\n");
            return ProcessResult.Failure;
        });
        Assert.True(SystemThemeDetector.DetectLinux(runner));
        Assert.Contains(runner.Calls, call => call.Executable == "gsettings" && call.Arguments.Last() == "color-scheme");
    }

    [Fact]
    public void MissingPortalToolsFallsBackToGSettingsColorScheme()
    {
        var runner = new FakeRunner((name, args) => name == "gsettings" && args.Last() == "color-scheme"
            ? new ProcessResult(true, 0, "'prefer-light'") : ProcessResult.Failure);
        Assert.False(SystemThemeDetector.DetectLinux(runner));
    }

    [Fact]
    public void GtkThemeAdwGtk3DoesNotImplyLightMode()
    {
        var runner = new FakeRunner((name, args) => name == "gsettings" && args.Last() == "gtk-theme"
            ? new ProcessResult(true, 0, "'adw-gtk3'") : ProcessResult.Failure);
        Assert.Null(SystemThemeDetector.DetectLinux(runner));
    }

    [Fact]
    public void GtkThemeWithDarkNameIsDark()
    {
        var runner = new FakeRunner((name, args) => name == "gsettings" && args.Last() == "gtk-theme"
            ? new ProcessResult(true, 0, "'Adwaita-dark'") : ProcessResult.Failure);
        Assert.True(SystemThemeDetector.DetectLinux(runner));
    }

    [Fact]
    public void AllLinuxProbesFailAsUnknown()
    {
        Assert.Null(SystemThemeDetector.DetectLinux(new FakeRunner((_, _) => ProcessResult.Failure)));
    }

    [Theory]
    [InlineData(0, "Dark\n", true)]
    [InlineData(0, "Light\n", null)]
    [InlineData(1, "", false)]
    public void MacOsUsesProcessExitSemantics(int exitCode, string output, bool? expected)
    {
        Assert.Equal(expected, SystemThemeDetector.DetectMacOS(
            new FakeRunner((_, _) => new ProcessResult(true, exitCode, output))));
    }

    [Fact]
    public void MacOsStartFailureIsUnknown()
    {
        Assert.Null(SystemThemeDetector.DetectMacOS(new FakeRunner((_, _) => ProcessResult.Failure)));
    }

    [Fact]
    public void LinuxProbesShareOneBoundedDeadline()
    {
        var runner = new FakeRunner((_, _) =>
        {
            Thread.Sleep(120);
            return ProcessResult.Failure;
        });
        var timer = Stopwatch.StartNew();
        Assert.Null(SystemThemeDetector.DetectLinux(runner));
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(1050), $"Detection took {timer.Elapsed}.");
        Assert.True(runner.Calls.Count < 6);
    }

    [Fact]
    public void RealRunnerKillsHangingChildWithinTimeoutOnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var runner = new ThemeProcessRunner();
        var timer = Stopwatch.StartNew();
        ProcessResult result = runner.Run("sleep", new[] { "30" }, TimeSpan.FromMilliseconds(150));
        Assert.False(result.Completed);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Hanging process took {timer.Elapsed} to stop.");
    }

    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> responder) : IThemeProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Calls.Add((executable, arguments));
            return responder(executable, arguments);
        }
    }
}
