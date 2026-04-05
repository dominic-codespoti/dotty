using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Dotty.App.Services;

/// <summary>
/// Service for handling OSC 8 hyperlinks in the terminal.
/// Provides secure URL opening with Ctrl+Click requirement and scheme validation.
/// </summary>
public static class HyperlinkService
{
    private const string DisableExternalOpenEnvVar = "DOTTY_DISABLE_EXTERNAL_URL_OPEN";

    // Allowed URL schemes for security
    private static readonly string[] AllowedSchemes = { "http", "https", "file" };
    
    // Schemes that require extra caution
    private static readonly string[] CautionSchemes = { "file" };

    /// <summary>
    /// Validates if a URL scheme is allowed to be opened.
    /// </summary>
    public static bool IsSchemeAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url);
            var scheme = uri.Scheme.ToLowerInvariant();
            return AllowedSchemes.Contains(scheme);
        }
        catch
        {
            // If it's not a valid URI, check if it starts with allowed schemes
            var lowerUrl = url.ToLowerInvariant();
            return AllowedSchemes.Any(s => lowerUrl.StartsWith($"{s}://"));
        }
    }

    /// <summary>
    /// Checks if a URL scheme requires extra caution (e.g., file://).
    /// </summary>
    public static bool IsCautionScheme(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url);
            var scheme = uri.Scheme.ToLowerInvariant();
            return CautionSchemes.Contains(scheme);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitizes a URL to prevent command injection attacks.
    /// </summary>
    public static string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // Remove any null bytes or control characters
        var sanitized = new string(url.Where(c => c >= 32 && c < 127).ToArray());
        
        // Trim whitespace
        sanitized = sanitized.Trim();
        
        return sanitized;
    }

    /// <summary>
    /// Opens a URL securely using the system's default application.
    /// Requires Ctrl+Click for security.
    /// </summary>
    /// <param name="url">The URL to open</param>
    /// <param name="ctrlPressed">Whether Ctrl key is pressed (required for security)</param>
    /// <returns>True if the URL was opened successfully</returns>
    public static async Task<bool> OpenUrlAsync(string url, bool ctrlPressed)
    {
        // Security: Require Ctrl+Click to open URLs
        if (!ctrlPressed)
        {
            return false;
        }

        var sanitized = SanitizeUrl(url);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return false;
        }

        // Validate URL scheme
        if (!IsSchemeAllowed(sanitized))
        {
            return false;
        }

        // Extra warning for file:// URLs
        if (IsCautionScheme(sanitized))
        {
            // TODO: Could show a confirmation dialog here
        }

        // Test hook: allow validation logic to be exercised without launching
        // external applications that can hang or crash headless runners.
        if (string.Equals(Environment.GetEnvironmentVariable(DisableExternalOpenEnvVar), "1", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use ProcessStartInfo with UseShellExecute on Windows
                var psi = new ProcessStartInfo
                {
                    FileName = sanitized,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try xdg-open on Linux
                var psi = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = EscapeShellArg(sanitized),
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use open command on macOS
                var psi = new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = EscapeShellArg(sanitized),
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            else
            {
                // Fallback: try generic approach
                var psi = new ProcessStartInfo
                {
                    FileName = sanitized,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Escapes a shell argument to prevent injection attacks.
    /// </summary>
    private static string EscapeShellArg(string arg)
    {
        // Simple escaping: wrap in single quotes and escape any single quotes
        if (arg.Contains('\'') || arg.Contains('"') || arg.Contains('$') || arg.Contains('`'))
        {
            // Use double quotes and escape
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
        }
        return "'" + arg + "'";
    }
}
