using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Threading;
using Dotty.Terminal.Adapter;
using Microsoft.Win32.SafeHandles;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Opens an already authorised, symlink-free path for reading. This is the only call that can block
/// indefinitely (a FIFO without a writer), which is why <see cref="KittyFileMedium.TryOpenBounded"/> runs it on a
/// helper thread; it is an interface so the timeout and budget logic can be tested without a FIFO.
/// </summary>
internal interface IKittyFileOpener
{
    SafeFileHandle Open(string path);
}

/// <summary>The production opener: a plain read-only <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>.</summary>
internal sealed class KittyFileOpener : IKittyFileOpener
{
    public static KittyFileOpener Instance { get; } = new();

    public SafeFileHandle Open(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
}

/// <summary>Range reading and the bounded open shared by the file and shared-memory media.</summary>
internal static class KittyFileMedium
{
    /// <summary>
    /// Reads the requested range of <paramref name="source"/> into a pooled buffer of at most
    /// <paramref name="maxBytes"/> + 1 bytes (the extra byte is the cap probe: when the range is longer than the
    /// cap the result holds <c>maxBytes + 1</c> bytes and <see cref="KittyMediumReadResult.ExceedsCap"/> is set).
    /// The memory comes from <see cref="ArrayPool{T}.Shared"/>, so the physical array may be larger than the
    /// logical length, which never exceeds <c>maxBytes + 1</c>.
    /// </summary>
    /// <param name="source">Must be seekable; its length is sampled once, so a file that grows while reading
    /// still yields exactly the requested bytes.</param>
    /// <param name="offset">First byte to read.</param>
    /// <param name="size">Bytes to read; 0 means everything from <paramref name="offset"/> to the sampled end.</param>
    /// <param name="maxBytes">Payload cap (positive).</param>
    /// <returns><see cref="KittyMediumReadResult.BadFile"/> for every failure: not seekable, offset at or beyond the
    /// end, fewer than <paramref name="size"/> bytes available, nothing to read, a short read, an I/O error.</returns>
    public static KittyMediumReadResult ReadRange(Stream source, long offset, long size, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[]? buffer = null;
        try
        {
            if (offset < 0 || size < 0 || maxBytes <= 0) return Fail("invalid range arguments");
            if (!source.CanSeek) return Fail("source is not seekable (not a regular file)");

            long length = source.Length;
            if (offset >= length) return Fail($"offset {offset} at or beyond end ({length})");

            long available = length - offset;
            long want;
            if (size == 0)
            {
                want = available;
            }
            else
            {
                if (size > available) return Fail($"declared size {size} exceeds the {available} bytes available");
                want = size;
            }

            long cap = Math.Min(maxBytes, Array.MaxLength - 1L);
            bool exceedsCap = want > cap;
            if (exceedsCap) want = cap + 1;

            int count = (int)want;
            buffer = ArrayPool<byte>.Shared.Rent(count);
            source.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int read = source.Read(buffer, total, count - total);
                if (read <= 0) return Fail($"short read: {total} of {count} bytes");
                total += read;
            }

            var result = KittyMediumReadResult.Success(buffer, count, exceedsCap);
            buffer = null; // owned by the result now
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }

        static KittyMediumReadResult Fail(string detail)
        {
            DebugLog.Log($"[kitty-media] read failed: {detail}");
            return KittyMediumReadResult.BadFile;
        }
    }

    /// <summary>
    /// Runs <paramref name="opener"/> on a dedicated background thread and waits up to <paramref name="timeout"/>.
    /// Returns the handle, or null when the open failed or timed out. On timeout <paramref name="abandoned"/> is
    /// set: the thread stays blocked in the OS and, should the open ever complete, closes the late handle itself.
    /// </summary>
    public static SafeFileHandle? TryOpenBounded(IKittyFileOpener opener, string path, TimeSpan timeout, out bool abandoned)
    {
        ArgumentNullException.ThrowIfNull(opener);
        abandoned = false;

        var attempt = new OpenAttempt(opener, path);
        var thread = new Thread(static state => ((OpenAttempt)state!).Run(), 256 * 1024)
        {
            IsBackground = true,
            Name = "dotty-kitty-open",
        };
        thread.Start(attempt);

        if (!attempt.Done.Wait(timeout))
        {
            if (Interlocked.CompareExchange(ref attempt.State, OpenAttempt.Abandoned, OpenAttempt.Pending) == OpenAttempt.Pending)
            {
                abandoned = true;
                DebugLog.Log($"[kitty-media] open of '{path}' timed out after {timeout.TotalMilliseconds:0} ms; abandoned");
                return null;
            }
            // The worker finished in the instant between the timeout and the exchange: its result is final.
            attempt.Done.Wait();
        }

        if (attempt.Error is not null)
        {
            DebugLog.Log($"[kitty-media] open of '{path}' failed: {attempt.Error.GetType().Name}: {attempt.Error.Message}");
            return null;
        }
        return attempt.Handle;
    }

    /// <summary>
    /// Defence in depth against a swap between the path check and the open: on Linux the kernel names the object
    /// behind the opened handle (<c>/proc/self/fd/N</c>) and that name must again be an absolute path outside the
    /// denied pseudo-filesystem locations; a FIFO, socket or anonymous inode reports a <c>pipe:[n]</c>-style name
    /// and is refused. Elsewhere, and where <c>/proc</c> is not mounted, nothing is checked here: the range read
    /// (seekable and non-empty) remains the backstop.
    /// </summary>
    /// <param name="handle">An open, not yet read handle.</param>
    /// <param name="allowDevShm">True for <c>t=t</c> and <c>t=s</c>, which may live in <c>/dev/shm</c>.</param>
    public static bool OpenedHandleAllowed(SafeFileHandle handle, bool allowDevShm)
    {
        if (!OperatingSystem.IsLinux())
            return true;

        string? target;
        try
        {
            long descriptor = (long)handle.DangerousGetHandle();
            target = new FileInfo("/proc/self/fd/" + descriptor.ToString(CultureInfo.InvariantCulture)).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            return true;
        }

        if (target is null)
            return true;
        if (target.Length == 0 || target[0] != '/')
        {
            DebugLog.Log("[kitty-media] opened object is not a file (pipe, socket or anonymous inode)");
            return false;
        }
        if (KittyPathPolicy.IsDeniedPrefix(target, allowDevShm))
        {
            DebugLog.Log($"[kitty-media] opened object '{target}' is in a denied location");
            return false;
        }
        return true;
    }

    private sealed class OpenAttempt(IKittyFileOpener opener, string path)
    {
        public const int Pending = 0;
        public const int Delivered = 1;
        public const int Abandoned = 2;

        public readonly ManualResetEventSlim Done = new(false);
        public int State;
        public SafeFileHandle? Handle;
        public Exception? Error;

        public void Run()
        {
            SafeFileHandle? handle = null;
            Exception? error = null;
            try
            {
                handle = opener.Open(path);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (Interlocked.CompareExchange(ref State, Delivered, Pending) == Pending)
            {
                Handle = handle;
                Error = error;
                Done.Set();
            }
            else
            {
                // The waiter gave up: nobody will ever look at this handle.
                handle?.Dispose();
            }
        }
    }
}
