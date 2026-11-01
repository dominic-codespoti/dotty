using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Pure rules for <c>file:</c> link targets. No I/O: the Unix executable-bit check lives in the
/// opener, right before launch.
/// </summary>
internal static class FileTargetRules
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    // Executable / auto-running / handler-registering extensions. Checked against every
    // dot-separated segment after the first one in the file name, so "a.exe.txt" is blocked.
    // "sh", "bash" and "zsh" are intentionally absent: they are inert on Windows and on
    // Unix the opener refuses files carrying an execute permission bit.
    private static readonly string[] BlockedExtensions =
    [
        "exe", "com", "bat", "cmd", "scr", "pif", "msi", "msp", "mst", "lnk", "url", "scf",
        "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "cpl", "reg", "msc",
        "jar", "application", "appref-ms", "gadget", "diagcab", "theme", "library-ms",
        "search-ms", "settingcontent-ms", "inf", "command", "tool", "terminal", "app",
        "bundle", "workflow", "action", "pkg", "mpkg", "dmg", "scpt", "scptd", "desktop",
        "run", "appimage", "bin",
    ];

    /// <summary>
    /// Validates <paramref name="uri"/> (scheme already known to be <c>file</c>). Returns null and the
    /// decoded <paramref name="localPath"/> when acceptable, otherwise a human-readable block reason.
    /// </summary>
    public static string? Check(Uri uri, string machineName, bool isWindows, out string? localPath)
    {
        localPath = null;

        string host = uri.Host;
        if (host.Length != 0 && !IsThisMachine(host, machineName))
            return $"file link refers to host '{host}', not this machine";

        string decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        HyperlinkUriStatus status = HyperlinkUri.Validate(decoded.AsSpan());
        if (status is HyperlinkUriStatus.ControlChar or HyperlinkUriStatus.FormatChar)
            return "file path contains control or invisible characters";
        if (decoded.Length == 0)
            return "file link has no path";

        string path = decoded;
        if (isWindows)
        {
            if (host.Length != 0 || decoded.StartsWith("//", StringComparison.Ordinal) || decoded.StartsWith("\\\\", StringComparison.Ordinal))
                return "network (UNC) paths are not allowed";
            if (decoded.Contains('\\'))
                return "file path contains backslashes";

            // "C:/dir/x" (Uri.AbsolutePath of file:///C:/dir/x) or "/C:/dir/x" -> "C:/dir/x"; the only ':' allowed
            // is the drive colon (anything else is an alternate data stream or device name).
            int driveAt = decoded[0] == '/' ? 1 : 0;
            bool drive = decoded.Length >= driveAt + 2 && char.IsAsciiLetter(decoded[driveAt])
                && decoded[driveAt + 1] == ':' && (decoded.Length == driveAt + 2 || decoded[driveAt + 2] == '/');
            string trimmed = drive ? decoded.Substring(driveAt) : decoded;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == ':' && !(drive && i == 1))
                    return "file path contains ':' (alternate data stream)";
            }

            if (HasDotDotSegment(trimmed))
                return "file path contains '..' segments";
            path = trimmed.Replace('/', '\\');
        }
        else if (HasDotDotSegment(decoded))
        {
            return "file path contains '..' segments";
        }

        if (HasBlockedExtension(path, isWindows))
            return "opening executable files from terminal links is disabled";

        localPath = path;
        return null;
    }

    private static bool IsThisMachine(string host, string machineName)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (machineName.Length == 0)
            return false;
        if (host.Equals(machineName, StringComparison.OrdinalIgnoreCase))
            return true;
        int dot = machineName.IndexOf('.');
        return dot > 0 && host.Equals(machineName.Substring(0, dot), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDotDotSegment(string path)
    {
        foreach (string segment in path.Split('/', '\\'))
        {
            if (segment == "..")
                return true;
        }

        return false;
    }

    private static bool HasBlockedExtension(string path, bool isWindows)
    {
        int slash = path.LastIndexOfAny(PathSeparators);
        string name = slash >= 0 ? path.Substring(slash + 1) : path;
        if (isWindows)
            name = name.TrimEnd('.', ' ');

        string[] parts = name.Split('.');
        for (int i = 1; i < parts.Length; i++)
        {
            string ext = isWindows ? parts[i].TrimEnd(' ') : parts[i];
            for (int b = 0; b < BlockedExtensions.Length; b++)
            {
                if (ext.Equals(BlockedExtensions[b], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
