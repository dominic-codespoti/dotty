using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Opens approved links with <c>xdg-open</c> / <c>open</c> / ShellExecute. The launch runs on the
/// thread pool (ShellExecute can block on handler resolution) and never throws.
/// </summary>
public sealed class SystemHyperlinkOpener : IHyperlinkOpener
{
    /// <inheritdoc/>
    public Task<HyperlinkOpenResult> OpenAsync(HyperlinkTarget target, CancellationToken ct = default)
    {
        if (target is null)
            return Task.FromResult(HyperlinkOpenResult.Failed("No link target."));
        if (ct.IsCancellationRequested)
            return Task.FromResult(HyperlinkOpenResult.Failed("Cancelled."));
        return Task.Run(() => Open(target), ct);
    }

    private static HyperlinkOpenResult Open(HyperlinkTarget target)
    {
        HyperlinkPlatform? platform = HyperlinkLauncher.CurrentPlatform();
        if (platform is null)
            return HyperlinkOpenResult.Failed("Opening links is not supported on this operating system.");

        try
        {
            if (!OperatingSystem.IsWindows() && target.Scheme == Uri.UriSchemeFile)
            {
                string? executable = CheckUnixExecutable(target.LocalPath);
                if (executable is not null)
                    return HyperlinkOpenResult.Failed(executable);
            }

            ProcessStartInfo psi = HyperlinkLauncher.CreateStartInfo(target, platform.Value);
            if (platform == HyperlinkPlatform.Windows)
            {
                using Process? process = Process.Start(psi);
                if (process is null)
                    return HyperlinkOpenResult.Failed("The platform link launcher returned no process.");
            }
            else
            {
                Process.StartAndForget(psi);
            }

            return HyperlinkOpenResult.Opened;
        }
        catch (Win32Exception ex) when (platform == HyperlinkPlatform.Linux)
        {
            return HyperlinkOpenResult.Failed($"xdg-open could not be started: {ex.Message}");
        }
        catch (Exception ex)
        {
            return HyperlinkOpenResult.Failed(ex.Message);
        }
    }

    /// <summary>Returns an error when <paramref name="path"/> is (a symlink to) a file with any execute bit.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string? CheckUnixExecutable(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "The file link has no local path.";

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            FileSystemInfo resolved = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            if (!File.Exists(resolved.FullName))
                return null;

            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(resolved.FullName) & executeBits) != 0
                ? "The target is an executable file."
                : null;
        }
        catch (Exception ex)
        {
            return $"The file could not be inspected: {ex.Message}";
        }
    }
}
