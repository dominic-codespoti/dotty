using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Dotty.Silk.Config;

/// <summary>
/// Detects the OS light/dark color scheme. Each platform probe is best-effort
/// and fails closed to null (unknown) so callers keep the configured theme.
/// </summary>
public static class SystemThemeDetector
{
    private static readonly IThemeProcessRunner ProcessRunner = new ThemeProcessRunner();

    public static bool? DetectIsDark()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return DetectWindows();
            if (OperatingSystem.IsMacOS()) return DetectMacOS(ProcessRunner);
            if (OperatingSystem.IsLinux()) return DetectLinux(ProcessRunner);
        }
        catch { }
        return null;
    }

    private static bool? DetectWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch { }
        return null;
    }

    internal static bool? DetectMacOS(IThemeProcessRunner runner)
    {
        ProcessResult result = RunWithinDeadline(runner, "defaults", new[] { "read", "-g", "AppleInterfaceStyle" },
            Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.9));
        if (!result.Completed) return null;
        if (result.ExitCode != 0) return false;
        return string.Equals(result.StandardOutput.Trim(), "Dark", StringComparison.OrdinalIgnoreCase) ? true : null;
    }

    internal static bool? DetectLinux(IThemeProcessRunner runner)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.9);
        string[] tools = { "busctl", "gdbus", "dbus-send" };
        string[][] args =
        {
            new[] { "--user", "call", "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Settings", "Read", "ss", "org.freedesktop.appearance", "color-scheme" },
            new[] { "call", "--session", "--dest", "org.freedesktop.portal.Desktop", "--object-path", "/org/freedesktop/portal/desktop", "--method", "org.freedesktop.portal.Settings.Read", "org.freedesktop.appearance", "color-scheme" },
            new[] { "--session", "--print-reply", "--dest=org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Settings.Read", "string:org.freedesktop.appearance", "string:color-scheme" },
        };
        for (int i = 0; i < tools.Length; i++)
        {
            ProcessResult result = RunWithinDeadline(runner, tools[i], args[i], deadline);
            if (TryPortalScheme(result, out int scheme) && scheme != 0) return scheme == 1;
        }

        ProcessResult colorScheme = RunWithinDeadline(runner, "gsettings",
            new[] { "get", "org.gnome.desktop.interface", "color-scheme" }, deadline);
        if (colorScheme.Completed && colorScheme.ExitCode == 0)
        {
            string value = Unquote(colorScheme.StandardOutput);
            if (string.Equals(value, "prefer-dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "prefer-light", StringComparison.OrdinalIgnoreCase)) return false;
        }
        ProcessResult gtkTheme = RunWithinDeadline(runner, "gsettings",
            new[] { "get", "org.gnome.desktop.interface", "gtk-theme" }, deadline);
        if (gtkTheme.Completed && gtkTheme.ExitCode == 0)
        {
            string value = Unquote(gtkTheme.StandardOutput);
            if (value.Contains("dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.EndsWith("light", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    private static ProcessResult RunWithinDeadline(IThemeProcessRunner runner, string executable, string[] args, long deadline)
    {
        long remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) return ProcessResult.Failure;
        TimeSpan remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
        TimeSpan timeout = remaining < TimeSpan.FromMilliseconds(170) ? remaining : TimeSpan.FromMilliseconds(170);
        try { return runner.Run(executable, args, timeout); }
        catch { return ProcessResult.Failure; }
    }

    private static bool TryPortalScheme(ProcessResult result, out int scheme)
    {
        scheme = -1;
        if (!result.Completed || result.ExitCode != 0) return false;
        string text = result.StandardOutput;
        int end = text.Length - 1;
        while (end >= 0 && !char.IsDigit(text[end])) end--;
        if (end < 0) return false;
        int start = end;
        while (start >= 0 && char.IsDigit(text[start])) start--;
        if (!int.TryParse(text.AsSpan(start + 1, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return false;
        scheme = value;
        return value is 0 or 1 or 2;
    }

    private static string Unquote(string value) => value.Trim().Trim('\'', '"');
}

internal readonly record struct ProcessResult(bool Completed, int ExitCode, string StandardOutput)
{
    internal static ProcessResult Failure => new(false, -1, string.Empty);
}

internal interface IThemeProcessRunner
{
    ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout);
}

internal sealed class ThemeProcessRunner : IThemeProcessRunner
{
    private const int MaxOutputCharacters = 4096;

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        for (int i = 0; i < arguments.Count; i++) startInfo.ArgumentList.Add(arguments[i]);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return ProcessResult.Failure;
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput);
        Task<string> stderr = ReadBoundedAsync(process.StandardError);
        int milliseconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
        if (!process.WaitForExit(milliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            try { Task.WaitAll(stdout, stderr); } catch (AggregateException) { }
            return ProcessResult.Failure;
        }
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(true, process.ExitCode, stdout.GetAwaiter().GetResult());
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[512];
        var output = new StringBuilder(512);
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            int append = Math.Min(read, MaxOutputCharacters - output.Length);
            if (append > 0) output.Append(buffer, 0, append);
        }
        return output.ToString();
    }
}
