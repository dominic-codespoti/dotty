using System;
using System.Collections.Generic;
using System.Threading;

namespace Dotty.Silk;

/// <summary>
/// Coordinates UI-thread callbacks and idempotent window shutdown.
/// </summary>
public sealed class WindowLifecycleCoordinator : IDisposable
{
    private readonly Queue<Action> _pending = new(16);
    private readonly object _pendingLock = new();
    private int _pendingCount;
    private int _closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsClosed)
            return false;

        lock (_pendingLock)
        {
            if (IsClosed)
                return false;

            _pending.Enqueue(callback);
            Volatile.Write(ref _pendingCount, _pending.Count);
        }

        return !IsClosed;
    }

    public int Drain()
    {
        if (IsClosed || Volatile.Read(ref _pendingCount) == 0)
            return 0;

        int executed = 0;
        while (!IsClosed)
        {
            Action callback;
            lock (_pendingLock)
            {
                if (IsClosed || _pending.Count == 0)
                {
                    Volatile.Write(ref _pendingCount, 0);
                    break;
                }

                callback = _pending.Dequeue();
                Volatile.Write(ref _pendingCount, _pending.Count);
            }

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
            lock (_pendingLock)
            {
                _pending.Clear();
                Volatile.Write(ref _pendingCount, 0);
            }
        }
    }

    public void Dispose() => Close();
}
