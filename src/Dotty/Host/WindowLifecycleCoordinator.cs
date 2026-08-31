using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Dotty.Silk;

/// <summary>
/// Coordinates UI-thread callbacks and idempotent window shutdown.
/// </summary>
public sealed class WindowLifecycleCoordinator : IDisposable
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private int _closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsClosed)
            return false;

        _pending.Enqueue(callback);
        return !IsClosed;
    }

    public int Drain()
    {
        if (IsClosed)
            return 0;

        int executed = 0;
        while (!IsClosed && _pending.TryDequeue(out var callback))
        {
            try
            {
                callback();
            }
            catch
            {
                // One stale callback must not prevent later UI work from draining.
            }
            executed++;
        }
        return executed;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            while (_pending.TryDequeue(out _))
            {
            }
        }
    }

    public void Dispose() => Close();
}
