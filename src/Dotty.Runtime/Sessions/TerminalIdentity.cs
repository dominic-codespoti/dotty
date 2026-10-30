using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Sessions;

/// <summary>The TERM chosen for a session and the <c>TERMINFO_DIRS</c> value the child needs to load it (null: leave unchanged).</summary>
public readonly record struct TermSelection(string Term, string? TerminfoDirs);

/// <summary>
/// Chooses the <c>TERM</c> a session's child receives (config key <c>term</c>) and finds terminfo entries the way
/// ncurses does, so an entry that is not installed never reaches the child.
/// </summary>
public static class TerminalIdentity
{
    public const string DefaultTerm = TerminalAdapter.DefaultTermName;

    /// <summary>Name of the compiled terminfo directory shipped beside the executable.</summary>
    public const string BundledDirectoryName = "terminfo";

    /// <summary>Directories ncurses commonly searches when <c>TERMINFO_DIRS</c> is unset.</summary>
    public static IReadOnlyList<string> SystemDirectories { get; } =
    [
        "/etc/terminfo",
        "/lib/terminfo",
        "/usr/share/terminfo",
        "/usr/lib/terminfo",
        "/usr/local/share/terminfo",
        "/opt/homebrew/share/terminfo",
    ];

    /// <summary><c>&lt;AppContext.BaseDirectory&gt;/terminfo</c> when it exists, else null.</summary>
    public static string? BundledTerminfoDirectory { get; } = FindBundledDirectory();

    /// <summary>
    /// The configured TERM when <paramref name="terminfoExists"/> finds its entry, otherwise <see cref="DefaultTerm"/>.
    /// Blank values and names that could escape a terminfo directory (path separators, whitespace, a leading dot)
    /// also give the default; the default itself is never looked up.
    /// </summary>
    public static string ResolveTerm(string? configured, Func<string, bool> terminfoExists)
    {
        ArgumentNullException.ThrowIfNull(terminfoExists);
        string? name = configured?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, DefaultTerm, StringComparison.Ordinal) || !IsValidName(name))
            return DefaultTerm;
        return terminfoExists(name) ? name : DefaultTerm;
    }

    /// <summary>True when <paramref name="name"/> exists in this process's terminfo search path or the bundled directory.</summary>
    public static bool TerminfoEntryExists(string name) =>
        IsValidName(name) && FindEntryDirectory(name, CurrentSearchDirectories(), File.Exists) is not null;

    /// <summary>Resolves <paramref name="configured"/> against this process's environment and file system.</summary>
    public static TermSelection SelectForCurrentProcess(string? configured) =>
        Select(
            configured,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BundledTerminfoDirectory,
            File.Exists,
            Directory.Exists);

    /// <summary>
    /// Resolves the TERM for a session. When the chosen entry exists only in <paramref name="bundledDirectory"/>,
    /// also returns the <c>TERMINFO_DIRS</c> value that adds it (see <see cref="AppendTerminfoDirs"/>).
    /// </summary>
    public static TermSelection Select(
        string? configured,
        Func<string, string?> getEnvironmentVariable,
        string? home,
        string? bundledDirectory,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        List<string> userDirectories = SearchDirectories(getEnvironmentVariable, home, directoryExists);
        string? foundIn = null;
        string term = ResolveTerm(configured, name =>
        {
            foundIn = FindEntryDirectory(name, userDirectories, fileExists);
            if (foundIn is null && !string.IsNullOrEmpty(bundledDirectory) && HasEntry(bundledDirectory, name, fileExists))
                foundIn = bundledDirectory;
            return foundIn is not null;
        });
        if (foundIn is null || !string.Equals(foundIn, bundledDirectory, StringComparison.Ordinal))
            return new TermSelection(term, null);
        return new TermSelection(term, AppendTerminfoDirs(
            getEnvironmentVariable("TERMINFO_DIRS"), bundledDirectory!, SystemDirectories, directoryExists));
    }

    /// <summary>
    /// The terminfo databases ncurses searches, in order: <c>$TERMINFO</c>, <c>~/.terminfo</c>, then each
    /// <c>$TERMINFO_DIRS</c> entry (an empty entry stands for the system directories) or, when that variable is
    /// unset, the system directories that exist.
    /// </summary>
    public static List<string> SearchDirectories(Func<string, string?> getEnvironmentVariable, string? home, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var directories = new List<string>();
        string? terminfo = getEnvironmentVariable("TERMINFO");
        if (!string.IsNullOrWhiteSpace(terminfo)) directories.Add(terminfo);
        if (!string.IsNullOrWhiteSpace(home)) directories.Add(Path.Combine(home, ".terminfo"));

        string? terminfoDirs = getEnvironmentVariable("TERMINFO_DIRS");
        if (string.IsNullOrEmpty(terminfoDirs))
        {
            AddSystemDirectories(directories, directoryExists);
            return directories;
        }
        bool systemAdded = false;
        foreach (string entry in terminfoDirs.Split(Path.PathSeparator))
        {
            if (entry.Length > 0)
            {
                directories.Add(entry);
            }
            else if (!systemAdded)
            {
                AddSystemDirectories(directories, directoryExists);
                systemAdded = true;
            }
        }
        return directories;
    }

    /// <summary>
    /// <c>TERMINFO_DIRS</c> with <paramref name="bundledDirectory"/> appended. An unset or empty value becomes an empty
    /// entry (the compiled-in default), the existing <paramref name="systemDirectories"/>, then the bundled directory,
    /// so dropping the implicit default list cannot hide system entries. A value that already lists the directory is
    /// returned unchanged.
    /// </summary>
    public static string AppendTerminfoDirs(
        string? existing, string bundledDirectory, IReadOnlyList<string> systemDirectories, Func<string, bool> directoryExists)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundledDirectory);
        ArgumentNullException.ThrowIfNull(systemDirectories);
        ArgumentNullException.ThrowIfNull(directoryExists);
        char separator = Path.PathSeparator;
        if (!string.IsNullOrEmpty(existing))
        {
            foreach (string entry in existing.Split(separator))
            {
                if (string.Equals(entry, bundledDirectory, StringComparison.Ordinal)) return existing;
            }
            return existing + separator + bundledDirectory;
        }

        var value = new StringBuilder();
        foreach (string directory in systemDirectories)
        {
            if (directoryExists(directory)) value.Append(separator).Append(directory);
        }
        return value.Append(separator).Append(bundledDirectory).ToString();
    }

    /// <summary>The first directory holding <paramref name="name"/> in the <c>x/name</c> or hexadecimal <c>78/name</c> layout.</summary>
    public static string? FindEntryDirectory(string name, IReadOnlyList<string> directories, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!IsValidName(name)) return null;
        foreach (string directory in directories)
        {
            if (HasEntry(directory, name, fileExists)) return directory;
        }
        return null;
    }

    private static bool HasEntry(string directory, string name, Func<string, bool> fileExists)
    {
        try
        {
            return fileExists(Path.Combine(directory, name[..1], name))
                || fileExists(Path.Combine(directory, ((int)name[0]).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), name));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || name[0] == '.') return false;
        foreach (char c in name)
        {
            if (c is '/' or '\\' || c > '\u007e' || char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }
        return true;
    }

    private static void AddSystemDirectories(List<string> directories, Func<string, bool> directoryExists)
    {
        foreach (string directory in SystemDirectories)
        {
            if (directoryExists(directory)) directories.Add(directory);
        }
    }

    private static List<string> CurrentSearchDirectories()
    {
        List<string> directories = SearchDirectories(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.Exists);
        if (BundledTerminfoDirectory is not null) directories.Add(BundledTerminfoDirectory);
        return directories;
    }

    private static string? FindBundledDirectory()
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, BundledDirectoryName);
            return Directory.Exists(directory) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
