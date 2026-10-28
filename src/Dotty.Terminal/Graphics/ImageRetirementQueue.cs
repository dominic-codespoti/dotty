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

    /// <summary>True while a GL consumer is draining the queue.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            if (!value)
                Queue.Clear();
        }
    }

    /// <summary>Called from <c>ImagePixels</c>' last release. Cheap no-op when no consumer is enabled.</summary>
    public static void Retire(long serial)
    {
        if (Volatile.Read(ref _enabled) != 0)
            Queue.Enqueue(serial);
    }

    /// <summary>Dequeues one retired serial (GL thread).</summary>
    public static bool TryDequeue(out long serial) => Queue.TryDequeue(out serial);
}
