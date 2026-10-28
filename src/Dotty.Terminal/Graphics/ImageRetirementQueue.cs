using System.Collections.Concurrent;
using System.Threading;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Hand-off from <see cref="ImagePixels"/> final release (any thread) to the GL
/// thread's texture cache: the serial of every image whose pixels were freed, so
/// the cache can delete the matching texture. Collection is off until a GL
/// texture cache enables it, so headless processes never accumulate entries.
/// </summary>
public static class ImageRetirementQueue
{
    private static readonly ConcurrentQueue<long> Queue = new();
    private static int _enabled;
    private static int _count;

    /// <summary>
    /// Upper bound on queued serials while enabled. A consumer that stops draining (hidden or minimised window)
    /// must not let the queue grow without limit; a dropped serial is harmless because the texture cache also
    /// evicts textures that stay unused for a number of frames.
    /// </summary>
    internal const int MaxPending = 1 << 16;

    /// <summary>True while a GL consumer is draining the queue.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            if (!value)
            {
                Queue.Clear();
                Volatile.Write(ref _count, 0);
            }
        }
    }

    /// <summary>Called from <c>ImagePixels</c>' last release. Cheap no-op when no consumer is enabled.</summary>
    public static void Retire(long serial)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;
        if (Interlocked.Increment(ref _count) > MaxPending)
        {
            Interlocked.Decrement(ref _count);
            return;
        }
        Queue.Enqueue(serial);
    }

    /// <summary>Dequeues one retired serial (GL thread).</summary>
    public static bool TryDequeue(out long serial)
    {
        if (!Queue.TryDequeue(out serial))
            return false;
        Interlocked.Decrement(ref _count);
        return true;
    }
}
