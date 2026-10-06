using System;
using System.Runtime.InteropServices;

namespace Dotty.Silk.Config;

/// <summary>
/// Detects the OS light/dark color scheme. Each platform probe is best-effort
/// and fails closed to null (unknown) so callers keep the configured theme.
/// </summary>
public static class SystemThemeDetector
{
    public static bool? DetectIsDark()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DetectWindows();
            if (OperatingSystem.IsMacOS())
                return DetectMacOS();
            if (OperatingSystem.IsLinux())
                return DetectLinux();
        }
        catch
        {
        }
        return null;
    }

    private static bool? DetectWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
                return i == 0;
        }
        catch
        {
        }
        return null;
    }

    private static bool? DetectMacOS()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("defaults", "read -g AppleInterfaceStyle")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode == 0 && output.Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase))
                return true;
            // Exit code nonzero with no value means Light (key absent); exit 0
            // without "Dark" is unexpected, treat as unknown.
            return process.ExitCode != 0 ? false : (bool?)null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? DetectLinux()
    {
        // Freedesktop org.freedesktop.appearance color-scheme: 0=no-preference,
        // 1=prefer-dark, 2=prefer-light.
        string? sessionBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        string? xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        bool hasBus = !string.IsNullOrWhiteSpace(sessionBus) ||
            (!string.IsNullOrWhiteSpace(xdgRuntime) &&
                System.IO.File.Exists(System.IO.Path.Combine(xdgRuntime, "bus")));
        if (!hasBus)
            return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gsettings", "get org.gnome.desktop.interface gtk-theme")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            if (process.ExitCode != 0)
                return null;
            string theme = output.Trim().Trim('\'', '"').ToLowerInvariant();
            if (theme.Contains("dark"))
                return true;
            if (theme.Length > 0)
                return false;
        }
        catch
        {
        }
        return null;
    }
}
