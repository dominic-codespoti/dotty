using System;
using System.IO;

namespace Dotty.Runtime.Sessions;

/// <summary>Finds the shipped <c>shell-integration</c> directory.</summary>
public static class ShellIntegrationLocator
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _packagedDirectory;
    private static string? _lastDiagnostic;

    public static string? LastDiagnostic
    {
        get { lock (Gate) return _lastDiagnostic; }
    }

    /// <summary>
    /// Resolution order: <c>DOTTY_SHELL_INTEGRATION_DIR</c> override (re-read on every call), then
    /// <c>BaseDirectory/shell-integration</c>, then <c>BaseDirectory/../share/dotty/shell-integration</c>.
    /// A directory is valid iff it contains <c>dotty.bash</c>. The packaged probe is cached.
    /// </summary>
    public static string? Find()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("DOTTY_SHELL_INTEGRATION_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            string? fromOverride = Resolve(Normalize(overrideDir), null, File.Exists, out string? overrideDiagnostic);
            lock (Gate) _lastDiagnostic = overrideDiagnostic;
            return fromOverride;
        }

        lock (Gate)
        {
            if (!_probed)
            {
                _packagedDirectory = Resolve(null, AppContext.BaseDirectory, File.Exists, out _lastDiagnostic);
                _probed = true;
            }
            return _packagedDirectory;
        }
    }

    /// <summary>
    /// Makes the override absolute (the shell runs in another working directory) and drops a trailing separator so
    /// the paths derived from it compare equal to what the injection scripts rebuild from the exported variable.
    /// </summary>
    private static string Normalize(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception)
        {
            return directory;
        }
    }

    internal static string? Resolve(string? overrideDir, string? baseDirectory, Func<string, bool> fileExists, out string? diagnostic)
    {
        diagnostic = null;
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            if (IsValid(overrideDir, fileExists)) return overrideDir;
            diagnostic = $"DOTTY_SHELL_INTEGRATION_DIR '{overrideDir}' does not contain dotty.bash.";
            return null;
        }

        if (!string.IsNullOrEmpty(baseDirectory))
        {
            string beside = Path.Combine(baseDirectory, "shell-integration");
            if (IsValid(beside, fileExists)) return beside;
            string fhs = Path.GetFullPath(Path.Combine(baseDirectory, "..", "share", "dotty", "shell-integration"));
            if (IsValid(fhs, fileExists)) return fhs;
        }
        diagnostic = "shell-integration directory not found beside the executable.";
        return null;
    }

    private static bool IsValid(string directory, Func<string, bool> fileExists) =>
        fileExists(Path.Combine(directory, "dotty.bash"));
}
