using System;
using System.Diagnostics;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>Operating-system family used to pick the launch mechanism.</summary>
public enum HyperlinkPlatform
{
    Linux,
    MacOS,
    Windows,
}

/// <summary>Pure builder of the process start info used to open a link; testable on any OS.</summary>
public static class HyperlinkLauncher
{
    /// <summary>The current OS, or null when links cannot be opened on it.</summary>
    public static HyperlinkPlatform? CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return HyperlinkPlatform.Windows;
        if (OperatingSystem.IsMacOS())
            return HyperlinkPlatform.MacOS;
        if (OperatingSystem.IsLinux())
            return HyperlinkPlatform.Linux;
        return null;
    }

    /// <summary>
    /// Linux/macOS run <c>xdg-open</c>/<c>open</c> with the canonical URI as the single argument
    /// (no shell; a canonical URI starts with a scheme letter so it cannot be read as an option).
    /// Windows shell-executes the canonical URI, or the validated local path for <c>file</c> targets.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(HyperlinkTarget target, HyperlinkPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(target);
        string canonical = target.Canonical;
        if (string.IsNullOrEmpty(canonical) || !char.IsAsciiLetter(canonical[0]))
            throw new ArgumentException("Canonical URI must start with a scheme letter.", nameof(target));

        if (platform == HyperlinkPlatform.Windows)
        {
            string fileName = target.Scheme == Uri.UriSchemeFile && !string.IsNullOrEmpty(target.LocalPath)
                ? target.LocalPath
                : canonical;
            return new ProcessStartInfo { FileName = fileName, UseShellExecute = true };
        }

        var psi = new ProcessStartInfo
        {
            FileName = platform == HyperlinkPlatform.MacOS ? "open" : "xdg-open",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(canonical);
        return psi;
    }
}
