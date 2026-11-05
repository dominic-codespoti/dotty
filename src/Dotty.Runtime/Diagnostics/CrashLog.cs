using System.Globalization;
using System.Text;

namespace Dotty.Runtime.Diagnostics;

/// <summary>One crash-log entry. <paramref name="Details"/> is an exception's <c>ToString()</c> or an error text.</summary>
public readonly record struct CrashEntry(
    DateTimeOffset Time,
    string Source,
    string Version,
    string Runtime,
    string OperatingSystem,
    string Details);

/// <summary>
/// Local, bounded crash log: <c>crash.log</c> in <see cref="Directory"/>, rotated to <c>crash.1.log</c> …
/// <c>crash.{MaxFiles-1}.log</c> before an append would exceed <see cref="MaxFileBytes"/>. Entries are
/// truncated to <see cref="MaxDetailChars"/> characters of details, so the log never exceeds
/// <c>MaxFiles × MaxFileBytes</c> plus one entry. Nothing is ever sent anywhere, and entries hold
/// only what <see cref="Format"/> prints: no environment and no terminal content.
/// </summary>
public sealed class CrashLog
{
    public const string FileName = "crash.log";
    public const long DefaultMaxFileBytes = 256 * 1024;
    public const int DefaultMaxFiles = 3;
    public const int MaxDetailChars = 32 * 1024;

    public CrashLog(string directory, long maxFileBytes = DefaultMaxFileBytes, int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        Directory = directory;
        MaxFileBytes = maxFileBytes;
        MaxFiles = maxFiles;
    }

    public string Directory { get; }
    public long MaxFileBytes { get; }
    public int MaxFiles { get; }
    public string CurrentPath => Path.Combine(Directory, FileName);

    /// <summary>The crash-log directory below a state root (see <c>PlatformPaths.StateRoot</c>).</summary>
    public static string DirectoryFor(string stateRoot) => Path.Combine(stateRoot, "logs");

    /// <summary><c>crash.log</c> for generation 0, else <c>crash.N.log</c>.</summary>
    public static string RotatedFileName(int generation) =>
        generation == 0 ? FileName : string.Create(CultureInfo.InvariantCulture, $"crash.{generation}.log");

    /// <summary>True when appending <paramref name="entryBytes"/> to a non-empty log of <paramref name="existingBytes"/> would pass the limit.</summary>
    public static bool ShouldRotate(long existingBytes, long entryBytes, long maxFileBytes) =>
        existingBytes > 0 && existingBytes + entryBytes > maxFileBytes;

    /// <summary>Formats one entry; lines end with <c>\n</c> and the entry ends with a blank line.</summary>
    public static string Format(CrashEntry entry)
    {
        string details = entry.Details;
        if (details.Length > MaxDetailChars)
        {
            int omitted = details.Length - MaxDetailChars;
            details = string.Create(CultureInfo.InvariantCulture, $"{details.AsSpan(0, MaxDetailChars)}\n[{omitted} more characters omitted]");
        }
        details = details.ReplaceLineEndings("\n").TrimEnd('\n');

        var sb = new StringBuilder(details.Length + 256);
        sb.Append("=== Dotty crash report ")
            .Append(entry.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" ===\n");
        sb.Append("Source:  ").Append(entry.Source).Append('\n');
        sb.Append("Version: ").Append(entry.Version).Append('\n');
        sb.Append("Runtime: ").Append(entry.Runtime).Append('\n');
        sb.Append("OS:      ").Append(entry.OperatingSystem).Append('\n');
        sb.Append('\n').Append(details).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Appends a formatted entry, rotating first when needed, and returns the path written. Creates the
    /// directory. Not synchronized: callers serialize appends (see <see cref="CrashReporter"/>).
    /// </summary>
    public string Append(string formattedEntry)
    {
        ArgumentNullException.ThrowIfNull(formattedEntry);
        System.IO.Directory.CreateDirectory(Directory);
        string path = CurrentPath;
        var info = new FileInfo(path);
        long existing = info.Exists ? info.Length : 0;
        if (ShouldRotate(existing, Encoding.UTF8.GetByteCount(formattedEntry), MaxFileBytes))
            Rotate();
        File.AppendAllText(path, formattedEntry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private void Rotate()
    {
        if (MaxFiles == 1)
        {
            File.Delete(CurrentPath);
            return;
        }

        File.Delete(Path.Combine(Directory, RotatedFileName(MaxFiles - 1)));
        for (int generation = MaxFiles - 2; generation >= 0; generation--)
        {
            string source = Path.Combine(Directory, RotatedFileName(generation));
            if (File.Exists(source))
                File.Move(source, Path.Combine(Directory, RotatedFileName(generation + 1)));
        }
    }
}
