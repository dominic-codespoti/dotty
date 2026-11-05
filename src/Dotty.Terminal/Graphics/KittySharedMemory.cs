using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Name handling and reading for <c>t=s</c> (shared memory). On Linux the object <c>/name</c> is the file
/// <c>/dev/shm/name</c> (the POSIX shm_open mapping), read like a regular file and unlinked afterwards by the
/// caller; on Windows the payload names an existing named file mapping; macOS and other systems are unsupported
/// (shm_open there needs a libc P/Invoke and has tiny name limits).
/// </summary>
internal static class KittySharedMemory
{
    /// <summary>Directory backing POSIX shared memory on Linux.</summary>
    public const string PosixRoot = "/dev/shm";

    /// <summary>NAME_MAX: longest object name in bytes.</summary>
    public const int MaxNameBytes = 255;

    /// <summary>True when this platform can serve <c>t=s</c>: Windows, or Linux with <c>/dev/shm</c>.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Directory.Exists(PosixRoot));

    /// <summary>
    /// Maps a POSIX shared-memory name to its <c>/dev/shm</c> path. One optional leading <c>/</c> is accepted
    /// (shm_open portability form); the remaining name must be 1..255 bytes, contain no further <c>/</c> and not be
    /// <c>.</c> or <c>..</c>. Pure string logic, identical on every OS.
    /// </summary>
    public static bool TryMapPosixName(string name, out string path)
    {
        path = string.Empty;
        ReadOnlySpan<char> leaf = name.AsSpan();
        if (leaf.Length > 0 && leaf[0] == '/') leaf = leaf[1..];
        if (leaf.Length == 0 || leaf.IndexOf('/') >= 0) return false;
        if (leaf.SequenceEqual(".") || leaf.SequenceEqual("..")) return false;
        if (Encoding.UTF8.GetByteCount(leaf) > MaxNameBytes) return false;
        path = string.Concat(PosixRoot, "/", leaf);
        return true;
    }

    /// <summary>
    /// Decodes and maps a POSIX shm name, then verifies via symbolic-link resolution that the object really lies
    /// inside <see cref="PosixRoot"/> (a link planted in the directory cannot redirect the read or the unlink).
    /// </summary>
    public static bool TryAuthorizePosix(ReadOnlySpan<byte> utf8, out string resolved)
    {
        resolved = string.Empty;
        if (!KittyPathPolicy.TryDecodePath(utf8, out string name) || !TryMapPosixName(name, out string path))
        {
            DebugLog.Log("[kitty-media] shm name rejected");
            return false;
        }
        if (!KittyPathPolicy.TryResolve(PosixRoot, out string root)
            || !KittyPathPolicy.TryResolve(path, out string real)
            || !KittyPathPolicy.IsStrictlyInside(root, real))
        {
            DebugLog.Log($"[kitty-media] shm object '{path}' escapes {PosixRoot}");
            return false;
        }
        resolved = real;
        return true;
    }

    /// <summary>
    /// Windows: reads <paramref name="size"/> bytes (all when 0) at <paramref name="offset"/> from the existing
    /// named file mapping. Windows reports mapping sizes rounded up to a page, so clients should always send
    /// <c>S</c>. Not Windows: <see cref="KittyMediumReadResult.Unsupported"/>. The mapping is not removed; it
    /// disappears when the client closes its last handle.
    /// </summary>
    public static KittyMediumReadResult ReadWindows(ReadOnlySpan<byte> utf8, long offset, long size, long maxBytes)
    {
        if (!OperatingSystem.IsWindows()) return KittyMediumReadResult.Unsupported;
        if (!KittyPathPolicy.TryDecodePath(utf8, out string name))
        {
            DebugLog.Log("[kitty-media] shm name rejected");
            return KittyMediumReadResult.BadFile;
        }

        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            return KittyFileMedium.ReadRange(view, offset, size, maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            DebugLog.Log($"[kitty-media] cannot open shared memory '{name}': {ex.GetType().Name}: {ex.Message}");
            return KittyMediumReadResult.BadFile;
        }
    }
}
