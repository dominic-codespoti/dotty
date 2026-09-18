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
    private static int _pendingReasons = (int)WindowFrameReason.Initial;

    public static bool ShouldPresent(TerminalAdapter? adapter) =>
        adapter is null || !adapter.SynchronizedUpdateActive;

    public static WindowFrameReason PendingReasons =>
        (WindowFrameReason)Volatile.Read(ref _pendingReasons);

    public static void Invalidate(WindowFrameReason reason)
    {
        if (reason != WindowFrameReason.None)
            Interlocked.Or(ref _pendingReasons, (int)reason);
    }

    public static WindowFrameReason Consume() =>
        (WindowFrameReason)Interlocked.Exchange(ref _pendingReasons, 0);

    public static void Requeue(WindowFrameReason reason)
    {
        if (reason != WindowFrameReason.None)
            Interlocked.Or(ref _pendingReasons, (int)reason);
    }
}
