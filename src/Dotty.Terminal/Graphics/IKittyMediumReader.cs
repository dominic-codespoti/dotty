using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Outcome of a medium read: <see cref="KittyStatus.Ok"/> with an owned buffer, or exactly one of two failure
/// outcomes that map to protocol replies — <see cref="KittyStatus.BadFile"/> (<c>EBADF:Failed to read image
/// file</c>, for every denial and I/O failure, so replies reveal nothing about the file system) and
/// <see cref="KittyStatus.UnsupportedMedium"/> (<c>EINVAL:Unsupported transmission type</c>, for a medium that is
/// disabled or unavailable on this platform). Failure results are shared singletons without detail.
/// </summary>
/// <remarks>The buffer is rented from <see cref="ArrayPool{T}.Shared"/>; <see cref="Dispose"/> returns it (idempotent).
/// <see cref="Data"/> is empty after disposal.</remarks>
internal sealed class KittyMediumReadResult : IDisposable
{
    /// <summary>The single outcome for every denial or read failure.</summary>
    public static KittyMediumReadResult BadFile { get; } = new(KittyStatus.BadFile);

    /// <summary>The medium is disabled by options, unavailable on this platform, or file media were shut off.</summary>
    public static KittyMediumReadResult Unsupported { get; } = new(KittyStatus.UnsupportedMedium);

    private byte[]? _buffer;
    private readonly int _length;

    private KittyMediumReadResult(KittyStatus status)
    {
        Status = status;
    }

    private KittyMediumReadResult(byte[] buffer, int length, bool exceedsCap)
    {
        Status = KittyStatus.Ok;
        _buffer = buffer;
        _length = length;
        ExceedsCap = exceedsCap;
    }

    /// <summary>Wraps a rented buffer; ownership moves to the result.</summary>
    internal static KittyMediumReadResult Success(byte[] rented, int length, bool exceedsCap) => new(rented, length, exceedsCap);

    public KittyStatus Status { get; }

    public bool IsOk => Status == KittyStatus.Ok;

    /// <summary>
    /// True when the requested range was longer than the cap: <see cref="Data"/> then holds exactly
    /// <c>maxBytes + 1</c> bytes, so the caller can fail with "payload too large" without reading more.
    /// </summary>
    public bool ExceedsCap { get; }

    /// <summary>Bytes read (0 for failures and after disposal).</summary>
    public int Length => _buffer is null ? 0 : _length;

    public ReadOnlySpan<byte> Data => _buffer is null ? default : new ReadOnlySpan<byte>(_buffer, 0, _length);

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// Reads the content of a <c>t=f</c>, <c>t=t</c> or <c>t=s</c> transmission. At transfer completion the caller
/// base64-decodes the payload (the UTF-8 path or shared-memory name), calls <see cref="Read"/>, and on
/// <see cref="KittyMediumReadResult.IsOk"/> uses <see cref="KittyMediumReadResult.Data"/> as the image payload
/// instead; otherwise it replies with <see cref="KittyMediumReadResult.Status"/>.
/// </summary>
internal interface IKittyMediumReader
{
    /// <param name="medium"><c>'f'</c>, <c>'t'</c> or <c>'s'</c>; anything else is unsupported.</param>
    /// <param name="pathUtf8">The decoded payload: file path or shared-memory name.</param>
    /// <param name="offset"><c>O</c> key (0 when absent).</param>
    /// <param name="size"><c>S</c> key; 0 reads everything from <paramref name="offset"/>.</param>
    /// <param name="maxBytes">Payload cap; at most <c>maxBytes + 1</c> bytes are ever allocated.</param>
    /// <param name="allowed">The session's <see cref="KittyGraphicsOptions.Media"/>.</param>
    KittyMediumReadResult Read(char medium, ReadOnlySpan<byte> pathUtf8, long offset, long size, long maxBytes, KittyMediaFlags allowed);
}

/// <summary>
/// Default <see cref="IKittyMediumReader"/>: policy-checked, bounded-open reads of regular files, temporary files
/// (deleted only after a successful read) and shared memory (POSIX <c>/dev/shm</c> on Linux, named mappings on
/// Windows, unsupported elsewhere). Holds the per-session abandoned-open budget, so use one instance per session.
/// </summary>
internal sealed class KittyMediumReader : IKittyMediumReader
{
    /// <summary>Longest an open may take before the attempt is abandoned.</summary>
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Abandoned (hung) opens tolerated before file media are disabled until restart.</summary>
    public const int DefaultMaxAbandonedOpens = 2;

    private readonly IKittyFileOpener _opener;
    private readonly TimeSpan _openTimeout;
    private readonly int _maxAbandonedOpens;
    private readonly IReadOnlyList<string>? _tempRoots;
    private int _abandonedOpens;

    public KittyMediumReader()
        : this(KittyFileOpener.Instance, DefaultOpenTimeout, DefaultMaxAbandonedOpens, null)
    {
    }

    /// <param name="opener">Seam for the (potentially blocking) open.</param>
    /// <param name="openTimeout">Bound for one open.</param>
    /// <param name="maxAbandonedOpens">Abandoned opens after which file media are disabled.</param>
    /// <param name="tempRoots">Resolved temp roots for <c>t=t</c>; null uses <see cref="KittyPathPolicy.DefaultTempRoots"/>.</param>
    internal KittyMediumReader(IKittyFileOpener opener, TimeSpan openTimeout, int maxAbandonedOpens, IReadOnlyList<string>? tempRoots)
    {
        ArgumentNullException.ThrowIfNull(opener);
        _opener = opener;
        _openTimeout = openTimeout;
        _maxAbandonedOpens = maxAbandonedOpens;
        _tempRoots = tempRoots;
    }

    /// <summary>Opens that timed out so far (their helper threads may still be blocked).</summary>
    public int AbandonedOpens => Volatile.Read(ref _abandonedOpens);

    /// <summary>True once the abandoned-open budget is spent: file, temp-file and POSIX shm media answer unsupported.</summary>
    public bool FileMediaDisabled => AbandonedOpens >= _maxAbandonedOpens;

    public KittyMediumReadResult Read(char medium, ReadOnlySpan<byte> pathUtf8, long offset, long size, long maxBytes, KittyMediaFlags allowed)
    {
        try
        {
            return ReadCore(medium, pathUtf8, offset, size, maxBytes, allowed);
        }
        catch (Exception ex)
        {
            DebugLog.Log($"[kitty-media] unexpected {ex.GetType().Name}: {ex.Message}");
            return KittyMediumReadResult.BadFile;
        }
    }

    private KittyMediumReadResult ReadCore(char medium, ReadOnlySpan<byte> pathUtf8, long offset, long size, long maxBytes, KittyMediaFlags allowed)
    {
        KittyMediaFlags needed = medium switch
        {
            'f' => KittyMediaFlags.File,
            't' => KittyMediaFlags.TempFile,
            's' => KittyMediaFlags.SharedMemory,
            _ => KittyMediaFlags.None,
        };
        if (needed == KittyMediaFlags.None || (allowed & needed) == 0) return KittyMediumReadResult.Unsupported;

        if (medium == 's')
        {
            if (!KittySharedMemory.IsSupported) return KittyMediumReadResult.Unsupported;
            if (OperatingSystem.IsWindows()) return KittySharedMemory.ReadWindows(pathUtf8, offset, size, maxBytes);
        }

        if (FileMediaDisabled)
        {
            DebugLog.Log("[kitty-media] file media disabled after abandoned opens");
            return KittyMediumReadResult.Unsupported;
        }

        string resolved;
        if (medium == 's')
        {
            if (!KittySharedMemory.TryAuthorizePosix(pathUtf8, out resolved)) return KittyMediumReadResult.BadFile;
        }
        else if (!KittyPathPolicy.TryAuthorize(medium, pathUtf8, _tempRoots, out resolved))
        {
            return KittyMediumReadResult.BadFile;
        }

        KittyMediumReadResult result = ReadResolved(resolved, offset, size, maxBytes, allowDevShm: medium != 'f');
        if (result.IsOk && (medium == 't' || medium == 's')) TryDelete(resolved);
        return result;
    }

    private KittyMediumReadResult ReadResolved(string resolved, long offset, long size, long maxBytes, bool allowDevShm)
    {
        var handle = KittyFileMedium.TryOpenBounded(_opener, resolved, _openTimeout, out bool abandoned);
        if (abandoned) Interlocked.Increment(ref _abandonedOpens);
        if (handle is null) return KittyMediumReadResult.BadFile;
        if (!KittyFileMedium.OpenedHandleAllowed(handle, allowDevShm))
        {
            handle.Dispose();
            return KittyMediumReadResult.BadFile;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1);
            return KittyFileMedium.ReadRange(stream, offset, size, maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DebugLog.Log($"[kitty-media] cannot read '{resolved}': {ex.GetType().Name}: {ex.Message}");
            return KittyMediumReadResult.BadFile;
        }
        finally
        {
            // Closes the handle; the FileStream owns it once constructed.
            if (stream is not null) stream.Dispose(); else handle.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"[kitty-media] could not delete '{path}': {ex.Message}");
        }
    }
}
