using System;
using System.Threading;

namespace Dotty.Silk;

internal static class HostWake
{
    private static Action? _post;
    private static int _uiThreadId;
    private static int _pending;

    internal static bool IsPending => Volatile.Read(ref _pending) != 0;

    internal static void Initialize(Action post, int uiThreadId)
    {
        Volatile.Write(ref _uiThreadId, uiThreadId);
        Volatile.Write(ref _post, post);
    }

    internal static void Shutdown()
    {
        Volatile.Write(ref _post, null);
        Volatile.Write(ref _uiThreadId, 0);
        Volatile.Write(ref _pending, 0);
    }

    internal static void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0 ||
            Environment.CurrentManagedThreadId == Volatile.Read(ref _uiThreadId))
            return;

        try
        {
            Volatile.Read(ref _post)?.Invoke();
        }
        catch
        {
        }
    }

    internal static void Reset() => Volatile.Write(ref _pending, 0);
}
