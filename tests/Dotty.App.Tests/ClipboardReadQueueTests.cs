using System;
using System.Collections.Generic;
using Dotty.Abstractions.Clipboard;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClipboardReadQueueTests : IDisposable
{
    private readonly List<TerminalSession> _sessions = new();
    private long _now = 1_000;
    private readonly ClipboardReadQueue _queue;

    public ClipboardReadQueueTests()
    {
        _queue = new ClipboardReadQueue(() => _now);
    }

    public void Dispose()
    {
        foreach (TerminalSession session in _sessions)
            session.Dispose();
    }

    private TerminalSession NewSession()
    {
        var session = new TerminalSession(2, 8, SilentPty.Create);
        _sessions.Add(session);
        return session;
    }

    private static bool Always(TerminalSession _) => true;

    [Fact]
    public void Enqueue_AllowsOnePendingRequestPerSession()
    {
        TerminalSession session = NewSession();

        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));
        Assert.False(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void Enqueue_RejectsRequestsBeyondTotalCap()
    {
        for (int i = 0; i < ClipboardReadQueue.MaxPendingTotal; i++)
            Assert.True(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));

        Assert.False(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));
        Assert.Equal(4, ClipboardReadQueue.MaxPendingTotal);
        Assert.Equal(ClipboardReadQueue.MaxPendingTotal, _queue.Count);
    }

    [Fact]
    public void Dequeue_FreesCapacityForNewRequests()
    {
        var first = NewSession();
        Assert.True(_queue.TryEnqueue(first, ClipboardSelection.Clipboard));
        for (int i = 1; i < ClipboardReadQueue.MaxPendingTotal; i++)
            Assert.True(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));
        var late = NewSession();
        Assert.False(_queue.TryEnqueue(late, ClipboardSelection.Clipboard));

        Assert.True(_queue.TryDequeue(Always, out PendingClipboardRead read));

        Assert.Same(first, read.Session);
        Assert.True(_queue.TryEnqueue(late, ClipboardSelection.Clipboard));
        // Full again: the served session has to wait for room like any other.
        Assert.False(_queue.TryEnqueue(first, ClipboardSelection.Clipboard));
    }

    [Fact]
    public void Dequeue_ServesOldestFirstAndKeepsSelection()
    {
        var a = NewSession();
        var b = NewSession();
        Assert.True(_queue.TryEnqueue(a, ClipboardSelection.Clipboard));
        _now += 10;
        Assert.True(_queue.TryEnqueue(b, ClipboardSelection.Clipboard | ClipboardSelection.Primary));

        Assert.True(_queue.TryDequeue(Always, out PendingClipboardRead first));
        Assert.True(_queue.TryDequeue(Always, out PendingClipboardRead second));

        Assert.Same(a, first.Session);
        Assert.Equal(ClipboardSelection.Clipboard, first.Selection);
        Assert.Equal(1_000, first.CreatedMs);
        Assert.Same(b, second.Session);
        Assert.Equal(ClipboardSelection.Clipboard | ClipboardSelection.Primary, second.Selection);
        Assert.Equal(1_010, second.CreatedMs);
        Assert.False(_queue.TryDequeue(Always, out _));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Dequeue_SkipsSessionsOffTheActiveTabButKeepsThem()
    {
        var background = NewSession();
        var foreground = NewSession();
        Assert.True(_queue.TryEnqueue(background, ClipboardSelection.Clipboard));
        Assert.True(_queue.TryEnqueue(foreground, ClipboardSelection.Clipboard));

        Assert.True(_queue.TryDequeue(s => ReferenceEquals(s, foreground), out PendingClipboardRead read));
        Assert.Same(foreground, read.Session);
        Assert.False(_queue.TryDequeue(s => ReferenceEquals(s, foreground), out _));
        Assert.Equal(1, _queue.Count);

        // Switching tabs later serves the waiting request.
        Assert.True(_queue.TryDequeue(s => ReferenceEquals(s, background), out read));
        Assert.Same(background, read.Session);
    }

    [Fact]
    public void Dequeue_DropsRequestsOlderThanThirtySeconds()
    {
        var session = NewSession();
        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));

        _now += ClipboardReadQueue.ExpiryMs;
        Assert.True(_queue.TryDequeue(Always, out _));

        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));
        _now += ClipboardReadQueue.ExpiryMs + 1;
        Assert.False(_queue.TryDequeue(Always, out _));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Expiry_AppliesToWaitingBackgroundRequests()
    {
        var session = NewSession();
        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));

        _now += 29_000;
        Assert.False(_queue.TryDequeue(static _ => false, out _));
        Assert.Equal(1, _queue.Count);

        _now += 2_000;
        Assert.False(_queue.TryDequeue(Always, out _));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Enqueue_PrunesExpiredRequestsSoTheyDoNotBlockNewOnes()
    {
        for (int i = 0; i < ClipboardReadQueue.MaxPendingTotal; i++)
            Assert.True(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));
        Assert.False(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));

        _now += ClipboardReadQueue.ExpiryMs + 1;

        var fresh = NewSession();
        Assert.True(_queue.TryEnqueue(fresh, ClipboardSelection.Clipboard));
        Assert.Equal(1, _queue.Count);
        Assert.True(_queue.TryDequeue(Always, out PendingClipboardRead read));
        Assert.Same(fresh, read.Session);
    }

    [Fact]
    public void Enqueue_AfterExpiryAllowsTheSameSessionAgain()
    {
        var session = NewSession();
        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));
        _now += ClipboardReadQueue.ExpiryMs + 1;

        Assert.True(_queue.TryEnqueue(session, ClipboardSelection.Clipboard));
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void RemoveSession_DropsOnlyThatSessionsRequest()
    {
        var gone = NewSession();
        var kept = NewSession();
        Assert.True(_queue.TryEnqueue(gone, ClipboardSelection.Clipboard));
        Assert.True(_queue.TryEnqueue(kept, ClipboardSelection.Clipboard));

        _queue.RemoveSession(gone);

        Assert.Equal(1, _queue.Count);
        Assert.True(_queue.TryDequeue(Always, out PendingClipboardRead read));
        Assert.Same(kept, read.Session);
        _queue.RemoveSession(gone);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        Assert.True(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));
        Assert.True(_queue.TryEnqueue(NewSession(), ClipboardSelection.Clipboard));

        _queue.Clear();

        Assert.Equal(0, _queue.Count);
        Assert.False(_queue.TryDequeue(Always, out _));
    }

    [Fact]
    public void ConcurrentEnqueueNeverExceedsTheCaps()
    {
        var sessions = new List<TerminalSession>();
        for (int i = 0; i < 16; i++)
            sessions.Add(NewSession());

        int accepted = 0;
        Parallel.ForEach(sessions, session =>
        {
            // Every thread asks twice; only the first request per session can ever be queued.
            if (_queue.TryEnqueue(session, ClipboardSelection.Clipboard))
                Interlocked.Increment(ref accepted);
            if (_queue.TryEnqueue(session, ClipboardSelection.Clipboard))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(ClipboardReadQueue.MaxPendingTotal, accepted);
        Assert.Equal(ClipboardReadQueue.MaxPendingTotal, _queue.Count);
    }
}
