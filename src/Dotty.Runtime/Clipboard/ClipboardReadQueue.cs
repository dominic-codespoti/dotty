using System;
using System.Collections.Generic;
using System.Threading;
using Dotty.Abstractions.Clipboard;
using Dotty.Runtime.Sessions;

namespace Dotty.Runtime.Clipboard;

/// <summary>One queued OSC 52 read request. <see cref="CreatedMs"/> is on the queue's injected clock.</summary>
public readonly record struct PendingClipboardRead(TerminalSession Session, ClipboardSelection Selection, long CreatedMs, string ReplySelector = "");

/// <summary>
/// Bounded, expiring queue of OSC 52 read requests awaiting the host's main thread.
/// At most <see cref="MaxPendingTotal"/> requests are held and at most one per session; further requests are
/// rejected (the oldest request wins, so a flood cannot starve it). Requests older than <see cref="ExpiryMs"/>
/// are dropped. All methods are thread-safe and only take a private lock: <see cref="TryEnqueue"/> runs on PTY
/// consumer threads while the terminal buffer lock is held, so nothing here calls back into the host.
/// </summary>
public sealed class ClipboardReadQueue
{
    /// <summary>Maximum requests held across all sessions.</summary>
    public const int MaxPendingTotal = 4;

    /// <summary>A request older than this (in milliseconds) is discarded without a reply.</summary>
    public const long ExpiryMs = 30_000;

    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private readonly List<PendingClipboardRead> _items = new(MaxPendingTotal);
    private int _count;

    /// <param name="nowMs">Monotonic millisecond clock; injected so expiry is testable.</param>
    public ClipboardReadQueue(Func<long> nowMs)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>Number of queued requests, including any that expired but have not been pruned yet. Lock-free.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Queues a request. Returns false (request dropped) when the session already has one pending or the queue
    /// is full after expired entries are pruned.
    /// </summary>
    public bool TryEnqueue(TerminalSession session, ClipboardSelection selection, string replySelector = "")
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            long now = _nowMs();
            PruneExpired(now);
            if (_items.Count >= MaxPendingTotal)
                return false;
            for (int i = 0; i < _items.Count; i++)
            {
                if (ReferenceEquals(_items[i].Session, session))
                    return false;
            }

            _items.Add(new PendingClipboardRead(session, selection, now, replySelector));
            Volatile.Write(ref _count, _items.Count);
            return true;
        }
    }

    /// <summary>
    /// Removes and returns the oldest non-expired request whose session satisfies
    /// <paramref name="isOnActiveTab"/>. Requests from other sessions stay queued until they expire.
    /// <paramref name="isOnActiveTab"/> runs while the queue lock is held: it must be quick and must not call
    /// back into this queue.
    /// </summary>
    public bool TryDequeue(Func<TerminalSession, bool> isOnActiveTab, out PendingClipboardRead read)
    {
        ArgumentNullException.ThrowIfNull(isOnActiveTab);

        if (Count == 0)
        {
            read = default;
            return false;
        }

        lock (_lock)
        {
            PruneExpired(_nowMs());
            for (int i = 0; i < _items.Count; i++)
            {
                if (!isOnActiveTab(_items[i].Session))
                    continue;

                read = _items[i];
                _items.RemoveAt(i);
                Volatile.Write(ref _count, _items.Count);
                return true;
            }
        }

        read = default;
        return false;
    }

    /// <summary>Drops every request belonging to <paramref name="session"/> (the session left the window).</summary>
    public void RemoveSession(TerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_lock)
        {
            bool removed = false;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_items[i].Session, session))
                    continue;
                _items.RemoveAt(i);
                removed = true;
            }

            if (removed)
                Volatile.Write(ref _count, _items.Count);
        }
    }

    /// <summary>Drops every request.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            Volatile.Write(ref _count, 0);
        }
    }

    private void PruneExpired(long now)
    {
        // Items are appended in clock order, so expired entries form a prefix.
        int expired = 0;
        while (expired < _items.Count && now - _items[expired].CreatedMs > ExpiryMs)
            expired++;
        if (expired == 0)
            return;

        _items.RemoveRange(0, expired);
        Volatile.Write(ref _count, _items.Count);
    }
}
