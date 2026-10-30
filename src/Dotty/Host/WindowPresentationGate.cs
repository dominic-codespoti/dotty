using System;
using System.Threading;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk;

[Flags]
public enum WindowFrameReason
{
    None = 0,
    Initial = 1 << 0,
    Content = 1 << 1,
    Resize = 1 << 2,
    Input = 1 << 3,
    Overlay = 1 << 4,
    CursorBlink = 1 << 5,
    ThemeConfig = 1 << 6,
    Selection = 1 << 7,
    Atlas = 1 << 8,
    TabOrPane = 1 << 9,
    /// <summary>An animated image's next frame is due (Kitty graphics).</summary>
    ImageAnimation = 1 << 10,
    /// <summary>Image textures were left un-uploaded by the per-frame upload budget; render again.</summary>
    ImageUpload = 1 << 11,
}

/// <summary>
/// Accumulates frame invalidation reasons across threads and wakes the UI loop. Pacing (when a dirty frame may
/// present) is decided by <see cref="FramePlanner"/>.
/// </summary>
public static class WindowPresentationGate
{
    private static int _pendingReasons = (int)WindowFrameReason.Initial;

    public static bool ShouldPresent(TerminalAdapter? adapter) =>
        adapter is null || !adapter.SynchronizedUpdateHolding;

    public static WindowFrameReason PendingReasons =>
        (WindowFrameReason)Volatile.Read(ref _pendingReasons);

    public static void Invalidate(WindowFrameReason reason)
    {
        if (reason != WindowFrameReason.None)
        {
            Interlocked.Or(ref _pendingReasons, (int)reason);
            HostWake.Request();
        }
    }

    public static WindowFrameReason Consume() =>
        (WindowFrameReason)Interlocked.Exchange(ref _pendingReasons, 0);

    public static void Requeue(WindowFrameReason reason)
    {
        if (reason != WindowFrameReason.None)
        {
            Interlocked.Or(ref _pendingReasons, (int)reason);
            HostWake.Request();
        }
    }
}
