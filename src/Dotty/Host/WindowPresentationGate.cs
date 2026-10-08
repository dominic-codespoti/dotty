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
}

public static class WindowPresentationGate
{
    public const int BackloggedFrameIntervalMs = 30;
    public const int ContentFrameMinIntervalMs = 8;
    public const int InteractiveWindowMs = 250;

    private static int _pendingReasons = (int)WindowFrameReason.Initial;

    /// <summary>
    /// Milliseconds a content-only frame should still be held back; 0 means present now.
    /// Streaming output is capped at <see cref="ContentFrameMinIntervalMs"/> so rendering
    /// does not interrupt the PTY consumer on every flush, and at the wider
    /// <see cref="BackloggedFrameIntervalMs"/> while the consumer is behind. Recent
    /// interaction and the first frame after idle are never held back.
    /// </summary>
    public static long CoalesceRemainingMs(long now, long lastPresent, long lastInteraction, bool anyBacklogged)
    {
        if (lastPresent == 0 || now - lastInteraction < InteractiveWindowMs)
            return 0;

        int interval = anyBacklogged ? BackloggedFrameIntervalMs : ContentFrameMinIntervalMs;
        return Math.Max(0L, interval - (now - lastPresent));
    }

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
