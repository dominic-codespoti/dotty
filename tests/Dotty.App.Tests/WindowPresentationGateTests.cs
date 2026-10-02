using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowPresentationGateTests
{
    [Fact]
    public void NullAdapterIsPresentable()
    {
        Assert.True(WindowPresentationGate.ShouldPresent(null));
    }

    [Fact]
    public void Mode2026SuppressesPresentationUntilDisabled()
    {
        var adapter = new TerminalAdapter(2, 8);
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed("\x1b[?2026h"u8);
        Assert.False(WindowPresentationGate.ShouldPresent(adapter));

        parser.Feed("\x1b[?2026l"u8);
        Assert.True(WindowPresentationGate.ShouldPresent(adapter));
    }

    [Fact]
    public void Mode2026HoldExpiresFromFirstBeginAndRepeatedBeginDoesNotRenewIt()
    {
        var clock = new ManualTimeProvider();
        var adapter = new TerminalAdapter(2, 8, timeProvider: clock);
        var parser = new BasicAnsiParser { Handler = adapter };
        int renders = 0;
        adapter.RenderRequested += _ => renders++;

        parser.Feed("\x1b[?2026h"u8);
        clock.AdvanceMs(TerminalAdapter.SynchronizedUpdateMaxHoldMs - 400);
        parser.Feed("\x1b[?2026hX"u8);
        clock.AdvanceMs(399);
        Assert.False(WindowPresentationGate.ShouldPresent(adapter));
        Assert.Equal(0, renders);

        clock.AdvanceMs(1);
        Assert.True(WindowPresentationGate.ShouldPresent(adapter));
        parser.Feed("Y"u8);
        adapter.FlushRender();
        Assert.Equal(1, renders);

        // END then BEGIN opens a fresh hold with its own full deadline.
        parser.Feed("\x1b[?2026l\x1b[?2026h"u8);
        clock.AdvanceMs(TerminalAdapter.SynchronizedUpdateMaxHoldMs - 1);
        Assert.False(WindowPresentationGate.ShouldPresent(adapter));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceMs(long milliseconds) => _timestamp += milliseconds;
    }

    [Fact]
    public void InvalidateCoalescesReasonsAndConsumeClearsPendingReasons()
    {
        WindowFrameReason previous = WindowPresentationGate.Consume();
        try
        {
            WindowPresentationGate.Invalidate(WindowFrameReason.Content | WindowFrameReason.Input);

            WindowFrameReason pending = WindowPresentationGate.PendingReasons;
            Assert.Equal(WindowFrameReason.Content | WindowFrameReason.Input, pending);
            Assert.Equal(pending, WindowPresentationGate.Consume());
            Assert.Equal(WindowFrameReason.None, WindowPresentationGate.PendingReasons);
        }
        finally
        {
            WindowPresentationGate.Requeue(previous);
        }
    }
}
