using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dotty.NativePty.Unix;

/// <summary>Where a resolved <c>pty-helper</c> came from.</summary>
public enum PtyHelperSource
{
    NotFound,
    /// <summary>The <c>DOTTY_PTY_HELPER</c> environment variable.</summary>
    Override,
    /// <summary><c>&lt;base&gt;/pty-helper</c> (the flat published layout).</summary>
    BesideExecutable,
    /// <summary><c>&lt;base&gt;/../libexec/dotty/pty-helper</c>.</summary>
    Libexec,
    /// <summary><c>&lt;base&gt;/../lib/dotty/pty-helper</c> (launcher in <c>bin</c>, app in <c>lib/dotty</c>).</summary>
    LibDotty,
    /// <summary>A source-tree build output found by walking up from the base directory (development builds only).</summary>
    DevelopmentTree,
    /// <summary>A directory on <c>PATH</c> (development builds only).</summary>
    SearchPath,
}

/// <summary>Result of <see cref="PtyHelperLocator.Resolve"/>: the helper path (null when not found) and every path probed.</summary>
public sealed record PtyHelperResolution(string? Path, PtyHelperSource Source, IReadOnlyList<string> ProbedPaths)
{
    public bool Found => Path is not null;

    /// <summary>One line for diagnostics and error messages.</summary>
    public string Describe()
    {
        if (Path is not null) return $"pty-helper: {Path} ({Source})";
        var text = new StringBuilder(Source == PtyHelperSource.Override
            ? $"pty-helper: {PtyHelperLocator.OverrideVariable} does not name an existing file; probed: "
            : "pty-helper: not found; probed: ");
        for (int i = 0; i < ProbedPaths.Count; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(ProbedPaths[i]);
        }
        return text.ToString();
    }
}

/// <summary>Finds the Unix <c>pty-helper</c> executable.</summary>
public static class PtyHelperLocator
{
    public const string OverrideVariable = "DOTTY_PTY_HELPER";
    public const string HelperFileName = "pty-helper";
    private const int DevelopmentTreeDepth = 16;

    /// <summary>
    /// True for <c>dotty</c> binaries produced by <c>dotnet publish -c Release</c> (the shipped artifacts): those resolve
    /// the helper only from the install layout. Plain builds (development, tests and CI test runs, in either
    /// configuration) also walk the source tree and <c>PATH</c>.
    /// </summary>
    public static bool IsPublishedLayout =>
#if DOTTY_PUBLISHED_LAYOUT
        true;
#else
        false;
#endif

    /// <summary>Resolves the helper for this process: real environment, <see cref="AppContext.BaseDirectory"/> and file system.</summary>
    public static PtyHelperResolution ResolveForCurrentProcess() =>
        Resolve(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, IsPublishedLayout, File.Exists);

    /// <summary>
    /// Resolution order: <c>DOTTY_PTY_HELPER</c> (exclusive: a set but missing path resolves to nothing rather than
    /// falling back), then <c>&lt;base&gt;/pty-helper</c>, <c>&lt;base&gt;/../libexec/dotty/pty-helper</c> and
    /// <c>&lt;base&gt;/../lib/dotty/pty-helper</c>. Unless <paramref name="isRelease"/>, the source tree
    /// (<c>src/Dotty.NativePty/bin/pty-helper</c> or <c>Dotty.NativePty/bin/pty-helper</c> in the base directory
    /// and up to 15 parents) and then each <c>PATH</c> entry follow. Returned and probed paths are absolute.
    /// </summary>
    public static PtyHelperResolution Resolve(
        Func<string, string?> getEnvironmentVariable,
        string baseDirectory,
        bool isRelease,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        var probed = new List<string>();

        string? overridePath = getEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            PtyHelperResolution? fromOverride = Probe(overridePath.Trim(), PtyHelperSource.Override, probed, fileExists);
            return fromOverride ?? new PtyHelperResolution(null, PtyHelperSource.Override, probed);
        }

        if (baseDirectory.Length > 0)
        {
            PtyHelperResolution? installed =
                Probe(System.IO.Path.Combine(baseDirectory, HelperFileName), PtyHelperSource.BesideExecutable, probed, fileExists)
                ?? Probe(System.IO.Path.Combine(baseDirectory, "..", "libexec", "dotty", HelperFileName), PtyHelperSource.Libexec, probed, fileExists)
                ?? Probe(System.IO.Path.Combine(baseDirectory, "..", "lib", "dotty", HelperFileName), PtyHelperSource.LibDotty, probed, fileExists);
            if (installed is not null) return installed;
        }

        if (!isRelease)
        {
            if (baseDirectory.Length > 0)
            {
                string? current = FullPath(baseDirectory);
                for (int i = 0; i < DevelopmentTreeDepth && current is not null; i++)
                {
                    PtyHelperResolution? fromTree =
                        Probe(System.IO.Path.Combine(current, "src", "Dotty.NativePty", "bin", HelperFileName), PtyHelperSource.DevelopmentTree, probed, fileExists)
                        ?? Probe(System.IO.Path.Combine(current, "Dotty.NativePty", "bin", HelperFileName), PtyHelperSource.DevelopmentTree, probed, fileExists);
                    if (fromTree is not null) return fromTree;
                    current = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(current));
                }
            }

            string? searchPath = getEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(searchPath))
            {
                foreach (string directory in searchPath.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    PtyHelperResolution? fromPath = Probe(System.IO.Path.Combine(directory, HelperFileName), PtyHelperSource.SearchPath, probed, fileExists);
                    if (fromPath is not null) return fromPath;
                }
            }
        }

        return new PtyHelperResolution(null, PtyHelperSource.NotFound, probed);
    }

    private static PtyHelperResolution? Probe(string candidate, PtyHelperSource source, List<string> probed, Func<string, bool> fileExists)
    {
        string? full = FullPath(candidate);
        if (full is null) return null;
        probed.Add(full);
        bool exists;
        try { exists = fileExists(full); }
        catch (Exception) { exists = false; }
        return exists ? new PtyHelperResolution(full, source, probed) : null;
    }

    // Absolute, normalized path; null for a path the platform rejects.
    private static string? FullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }
}
