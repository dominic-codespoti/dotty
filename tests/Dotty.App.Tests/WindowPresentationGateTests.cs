using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

[Collection(HostWakeCollection.Name)]
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

    [Fact]
    public void EveryFrameReasonIsADistinctSingleBit()
    {
        int seen = 0;
        foreach (WindowFrameReason reason in Enum.GetValues<WindowFrameReason>())
        {
            if (reason == WindowFrameReason.None)
                continue;

            int bit = (int)reason;
            Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)bit));
            Assert.Equal(0, seen & bit);
            seen |= bit;
        }

        // The image reasons extend the existing 10 bits (0..9) without renumbering any of them.
        Assert.Equal(1 << 10, (int)WindowFrameReason.ImageAnimation);
        Assert.Equal(1 << 11, (int)WindowFrameReason.ImageUpload);
        Assert.Equal(1 << 9, (int)WindowFrameReason.TabOrPane);
    }

    [Fact]
    public void ImageReasonsCoalesceWithOthersAndSurviveRequeue()
    {
        WindowFrameReason previous = WindowPresentationGate.Consume();
        try
        {
            WindowPresentationGate.Invalidate(WindowFrameReason.ImageAnimation);
            WindowPresentationGate.Invalidate(WindowFrameReason.ImageUpload | WindowFrameReason.Content);

            WindowFrameReason expected =
                WindowFrameReason.ImageAnimation | WindowFrameReason.ImageUpload | WindowFrameReason.Content;
            Assert.Equal(expected, WindowPresentationGate.PendingReasons);
            Assert.Equal(expected, WindowPresentationGate.Consume());
            Assert.Equal(WindowFrameReason.None, WindowPresentationGate.PendingReasons);

            // A frame that deferred texture uploads asks for another one with exactly that reason.
            WindowPresentationGate.Requeue(WindowFrameReason.ImageUpload);
            Assert.Equal(WindowFrameReason.ImageUpload, WindowPresentationGate.Consume());
        }
        finally
        {
            WindowPresentationGate.Consume();
            WindowPresentationGate.Requeue(previous);
        }
    }

    [Fact]
    public async Task BackgroundInvalidatePostsWakeButNoneDoesNot()
    {
        WindowFrameReason previous = WindowPresentationGate.Consume();
        int posts = 0;
        HostWake.Shutdown();
        HostWake.Initialize(() => Interlocked.Increment(ref posts), Environment.CurrentManagedThreadId);
        try
        {
            await Task.Run(() => WindowPresentationGate.Invalidate(WindowFrameReason.Content), TestContext.Current.CancellationToken);
            Assert.Equal(1, posts);
            HostWake.Reset();

            await Task.Run(() => WindowPresentationGate.Invalidate(WindowFrameReason.None), TestContext.Current.CancellationToken);
            Assert.Equal(1, posts);
        }
        finally
        {
            HostWake.Shutdown();
            WindowPresentationGate.Consume();
            WindowPresentationGate.Requeue(previous);
        }
    }
}
