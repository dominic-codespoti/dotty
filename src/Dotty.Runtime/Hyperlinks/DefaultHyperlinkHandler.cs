using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Default implementation of <see cref="IHyperlinkHandler"/> that opens URLs
/// securely using system utilities (<c>Process.Start</c> / <c>xdg-open</c> / <c>open</c>).
/// </summary>
public class DefaultHyperlinkHandler : IHyperlinkHandler
{
    private static readonly string[] AllowedSchemes = ["http", "https", "file", "mailto", "git", "ssh"];
    public string? LastError { get; private set; }

    /// <inheritdoc/>
    public virtual bool CanOpen(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();

        // Check git@ SCP-style syntax: git@github.com:owner/repo
        if (trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            string scheme = uri.Scheme.ToLowerInvariant();
            return AllowedSchemes.Contains(scheme);
        }

        string lower = trimmed.ToLowerInvariant();
        return AllowedSchemes.Any(s => lower.StartsWith($"{s}://", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public virtual Task<bool> OpenUrlAsync(string url)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(url) || !CanOpen(url))
        {
            LastError = "The URL is empty or uses an unsupported scheme.";
            return Task.FromResult(false);
        }

        string sanitized = SanitizeUrl(url);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            LastError = "The URL contains no printable characters.";
            return Task.FromResult(false);
        }

        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = sanitized,
                    UseShellExecute = true,
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(sanitized);
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(sanitized);
            }
            else
            {
                LastError = $"Opening URLs is not supported on '{RuntimeInformation.OSDescription}'.";
                return Task.FromResult(false);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                LastError = "The platform URL launcher returned no process.";
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Sanitizes URL to strip unsafe control characters.
    /// </summary>
    public static string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[url.Length];
        int count = 0;
        foreach (char c in url)
        {
            if (c >= 32 && c < 127)
            {
                buffer[count++] = c;
            }
        }

        return buffer[..count].ToString().Trim();
    }
}
