using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Pre-open checks for the file media (<c>t=f</c>, <c>t=t</c>, <c>t=s</c> on POSIX): decodes the client supplied
/// path, rejects device/UNC/pseudo-filesystem locations, resolves every symbolic link (also in intermediate
/// directories) and re-checks the real path, and for temporary files enforces the temp-root and marker rules.
/// </summary>
/// <remarks>
/// <para>Every denial answers <c>false</c> without detail so callers can reply with the one protocol error;
/// the reason goes to <see cref="DebugLog"/> only. The string-level predicates
/// (<see cref="TryDecodePath"/>, <see cref="IsUncOrDevicePath"/>, <see cref="IsDeniedPrefix"/>,
/// <see cref="IsStrictlyInside"/>) are pure and behave identically on every OS.</para>
/// <para>Relative paths are denied: the terminal's working directory has nothing to do with the client's.
/// A final-component swap between this check and the open (time-of-check/time-of-use) is not defended against;
/// the threat model is a program that can already write the files it names.</para>
/// </remarks>
internal static class KittyPathPolicy
{
    /// <summary>Longest accepted path, in UTF-8 bytes.</summary>
    public const int MaxPathBytes = 2048;

    /// <summary>Substring a temporary file's path must contain for <c>t=t</c> deletion (protocol rule).</summary>
    public const string TempMarker = "tty-graphics-protocol";

    // Linux MAXSYMLINKS; a loop never terminates, so the hop budget is also the loop detector.
    private const int MaxLinkHops = 40;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] DeniedUnixPrefixes = ["/proc", "/sys", "/dev"];
    private const string DevShm = "/dev/shm";

    // On POSIX a backslash is an ordinary file-name character.
    private static readonly char[] PathSeparators = OperatingSystem.IsWindows() ? new[] { '/', '\\' } : new[] { '/' };

    /// <summary>
    /// Decodes a path payload: 1..<see cref="MaxPathBytes"/> bytes of strict UTF-8 without NUL, LF or CR.
    /// </summary>
    public static bool TryDecodePath(ReadOnlySpan<byte> utf8, out string path)
    {
        path = string.Empty;
        if (utf8.Length == 0 || utf8.Length > MaxPathBytes) return false;
        foreach (byte b in utf8)
        {
            if (b == 0 || b == (byte)'\n' || b == (byte)'\r') return false;
        }
        try
        {
            path = StrictUtf8.GetString(utf8);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for Windows device paths (<c>\\.\</c>, <c>\\?\</c>, <c>\??\</c>) and UNC paths (<c>\\host\share</c>).
    /// A leading double backslash is denied on every OS; a leading double forward slash only when
    /// <paramref name="windows"/> is set (on POSIX <c>//x</c> is an ordinary path).
    /// </summary>
    public static bool IsUncOrDevicePath(string path, bool windows)
    {
        if (path.Length >= 2)
        {
            char a = path[0], b = path[1];
            if (a == '\\' && b == '\\') return true;
            if (windows && (a == '/' || a == '\\') && (b == '/' || b == '\\')) return true;
        }
        if (windows && path.Length >= 4 && (path[0] == '\\' || path[0] == '/') && path[1] == '?' && path[2] == '?'
            && (path[3] == '\\' || path[3] == '/'))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> (normalised, absolute) is <c>/proc</c>, <c>/sys</c> or <c>/dev</c> or
    /// below one of them, compared case-insensitively. <c>/dev/shm</c> and below is exempt when
    /// <paramref name="allowDevShm"/>.
    /// </summary>
    public static bool IsDeniedPrefix(string fullPath, bool allowDevShm)
    {
        foreach (string prefix in DeniedUnixPrefixes)
        {
            if (!IsSameOrChild(fullPath, prefix)) continue;
            if (allowDevShm && prefix == "/dev" && IsSameOrChild(fullPath, DevShm)) return false;
            return true;
        }
        return false;
    }

    private static bool IsSameOrChild(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    /// <summary>
    /// True when <paramref name="path"/> lies strictly below <paramref name="root"/> (a path component boundary
    /// is required, so <c>/tmpx</c> is not inside <c>/tmp</c>, and the root itself is not inside). A filesystem
    /// root never matches anything.
    /// </summary>
    public static bool IsStrictlyInside(string root, string path)
    {
        if (root.Length == 0) return false;
        int end = root.Length;
        while (end > 0 && IsSeparator(root[end - 1])) end--;
        if (end == 0) return false; // "/" would make everything a temp file
        string? pathRoot = Path.GetPathRoot(root);
        if (pathRoot is not null && pathRoot.Length == root.Length) return false; // "C:\"
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Length > end + 1
            && string.Compare(path, 0, root, 0, end, comparison) == 0
            && IsSeparator(path[end]);
    }

    /// <summary>True when <paramref name="resolved"/> is strictly inside any root of <paramref name="roots"/>.</summary>
    public static bool IsInsideAny(IReadOnlyList<string> roots, string resolved)
    {
        for (int i = 0; i < roots.Count; i++)
        {
            if (IsStrictlyInside(roots[i], resolved)) return true;
        }
        return false;
    }

    /// <summary>
    /// The temp roots for <c>t=t</c>, symlink-resolved: <see cref="Path.GetTempPath"/>, <c>$TMPDIR</c>, and on
    /// non-Windows <c>/tmp</c> and <c>/dev/shm</c>. Roots that do not resolve are omitted.
    /// </summary>
    public static IReadOnlyList<string> DefaultTempRoots()
    {
        var roots = new List<string>(4);
        Add(roots, Path.GetTempPath());
        Add(roots, Environment.GetEnvironmentVariable("TMPDIR"));
        if (!OperatingSystem.IsWindows())
        {
            Add(roots, "/tmp");
            Add(roots, DevShm);
        }
        return roots;

        static void Add(List<string> list, string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            try
            {
                string full = Path.GetFullPath(candidate);
                if (TryResolve(full, out string real) && !ContainsOrdinal(list, real)) list.Add(real);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        static bool ContainsOrdinal(List<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Resolves every symbolic link of the absolute, normalised <paramref name="fullPath"/> (final component and
    /// intermediate directories, with correct <c>..</c> semantics after a link) into a real path. False when
    /// more than 40 links are followed (loop) or the filesystem reports an error. Components that do not exist
    /// are kept verbatim; the later open fails for them.
    /// </summary>
    public static bool TryResolve(string fullPath, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (root.Length == 0) return false;

            var pending = new Stack<string>();
            PushSegments(pending, fullPath.AsSpan(root.Length));
            string current = root;
            int hops = 0;

            while (pending.Count > 0)
            {
                string segment = pending.Pop();
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    current = ParentOf(current, root);
                    continue;
                }

                string next = Path.Join(current, segment);
                string? target = ReadLinkTarget(next);
                if (target is null)
                {
                    current = next;
                    continue;
                }

                if (++hops > MaxLinkHops) return false;

                if (Path.IsPathRooted(target))
                {
                    string targetRoot = Path.GetPathRoot(target) ?? string.Empty;
                    // Windows "\dir" is rooted but drive-less: it stays on the link's drive.
                    string newRoot = targetRoot.Length <= 1 && OperatingSystem.IsWindows()
                        ? Path.GetPathRoot(current) ?? targetRoot
                        : targetRoot;
                    if (newRoot.Length == 0) return false;
                    root = newRoot;
                    current = newRoot;
                    PushSegments(pending, target.AsSpan(targetRoot.Length));
                }
                else
                {
                    PushSegments(pending, target);
                }
            }

            resolved = current;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DebugLog.Log($"[kitty-media] resolve failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // A component that does not exist cannot be a link; the later open reports the missing file.
    private static string? ReadLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void PushSegments(Stack<string> pending, ReadOnlySpan<char> path)
    {
        // Pushed last-to-first so the first segment is popped first.
        var parts = path.ToString().Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--) pending.Push(parts[i]);
    }

    private static string ParentOf(string current, string root)
    {
        if (string.Equals(current, root, StringComparison.Ordinal)) return root;
        return Path.GetDirectoryName(current) ?? root;
    }

    /// <summary>
    /// The complete pre-open policy for <c>t=f</c> (<paramref name="medium"/> <c>'f'</c>) and <c>t=t</c>
    /// (<c>'t'</c>). On success <paramref name="resolved"/> is the symlink-free real path to open (and, for
    /// <c>t=t</c>, to delete). <paramref name="tempRoots"/> null means <see cref="DefaultTempRoots"/>.
    /// </summary>
    public static bool TryAuthorize(char medium, ReadOnlySpan<byte> utf8, IReadOnlyList<string>? tempRoots, out string resolved)
    {
        resolved = string.Empty;
        if (!TryDecodePath(utf8, out string raw))
        {
            DebugLog.Log("[kitty-media] denied: path is empty, too long, not UTF-8, or contains NUL/newline");
            return false;
        }
        try
        {
            return AuthorizeCore(medium, raw, tempRoots, out resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DebugLog.Log($"[kitty-media] denied '{raw}': {ex.GetType().Name}: {ex.Message}");
            resolved = string.Empty;
            return false;
        }
    }

    private static bool AuthorizeCore(char medium, string raw, IReadOnlyList<string>? tempRoots, out string resolved)
    {
        resolved = string.Empty;
        bool windows = OperatingSystem.IsWindows();
        // /dev/shm is a legitimate temp root (and the shm backing store); plain files must not live there.
        bool allowDevShm = medium == 't' || medium == 's';

        if (IsUncOrDevicePath(raw, windows)) return Deny(raw, "UNC or device path");
        if (IsSeparator(raw[^1])) return Deny(raw, "trailing separator");
        if (!Path.IsPathFullyQualified(raw)) return Deny(raw, "not an absolute path");

        string full = Path.GetFullPath(raw);
        if (IsUncOrDevicePath(full, windows)) return Deny(raw, "UNC or device path after normalisation");
        if (IsDeniedPrefix(full, allowDevShm)) return Deny(raw, "denied location");

        if (!TryResolve(full, out string real)) return Deny(raw, "cannot resolve symbolic links");
        if (IsUncOrDevicePath(real, windows)) return Deny(raw, "link resolves to UNC or device path");
        if (IsDeniedPrefix(real, allowDevShm)) return Deny(raw, "link resolves to denied location");

        if (medium == 't')
        {
            if (!real.Contains(TempMarker, StringComparison.Ordinal)) return Deny(raw, "temp file lacks marker");
            if (!IsInsideAny(tempRoots ?? DefaultTempRoots(), real)) return Deny(raw, "temp file outside temp roots");
        }

        resolved = real;
        return true;
    }

    private static bool Deny(string raw, string reason)
    {
        DebugLog.Log($"[kitty-media] denied '{raw}': {reason}");
        return false;
    }
}
