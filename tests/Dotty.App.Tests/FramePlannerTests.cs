using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class FramePlannerTests
{
    private const long LongAgo = 10_000;
    private const long NoInteraction = 0;
    private const int Interval = FramePlanner.FixedContentFrameMinIntervalMs;

    private static FramePlan Decide(
        WindowFrameReason reasons,
        bool stateDirty = false,
        long sincePresent = 2,
        long lastPresent = LongAgo,
        long lastInteraction = NoInteraction,
        int interval = Interval) =>
        FramePlanner.Decide(new FramePlanInputs(LongAgo + sincePresent, reasons, stateDirty, lastPresent, lastInteraction, interval));

    [Theory]
    // sync held, width, height, iconified -> gate
    [InlineData(false, 800, 600, false, FrameGate.Proceed)]
    [InlineData(true, 800, 600, false, FrameGate.SyncHold)]
    [InlineData(true, 0, 0, true, FrameGate.SyncHold)]   // the hold is reported first: it has its own deadline
    [InlineData(false, 800, 600, true, FrameGate.NoSurface)]
    [InlineData(false, 0, 600, false, FrameGate.NoSurface)]
    [InlineData(false, 800, 0, false, FrameGate.NoSurface)]
    [InlineData(false, -1, -1, false, FrameGate.NoSurface)]
    public void GateSeparatesSyncHoldAndMissingSurface(bool held, int width, int height, bool iconified, FrameGate expected) =>
        Assert.Equal(expected, FramePlanner.Gate(held, width, height, iconified));

    [Fact]
    public void CleanFrameIsIdle() =>
        Assert.Equal(new FramePlan(FrameAction.Idle, 0), Decide(WindowFrameReason.None));

    [Fact]
    public void StateChangeWithoutReasonsIsStillDirtyAndPacedLikeContent() =>
        Assert.Equal(new FramePlan(FrameAction.Hold, Interval - 2), Decide(WindowFrameReason.None, stateDirty: true));

    [Theory]
    [InlineData(2L, 6L)]
    [InlineData(7L, 1L)]
    [InlineData(8L, 0L)]
    [InlineData(500L, 0L)]
    public void ContentOnlyFramesAreHeldForTheRestOfTheInterval(long sincePresent, long expectedHold)
    {
        var plan = Decide(WindowFrameReason.Content, sincePresent: sincePresent);

        Assert.Equal(expectedHold == 0 ? new FramePlan(FrameAction.Render, 0) : new FramePlan(FrameAction.Hold, expectedHold), plan);
    }

    [Fact]
    public void BackloggedIntervalHoldsLonger() =>
        Assert.Equal(new FramePlan(FrameAction.Hold, 28), Decide(WindowFrameReason.Content, interval: FramePlanner.BackloggedFrameIntervalMs));

    [Fact]
    public void FrameWithinTheInteractiveWindowIsNeverHeld()
    {
        long now = LongAgo + 2;
        Assert.Equal(FrameAction.Render, Decide(WindowFrameReason.Content, lastInteraction: now - 1).Action);
        Assert.Equal(FrameAction.Render, Decide(WindowFrameReason.Content, lastInteraction: now - FramePlanner.InteractiveWindowMs + 1).Action);
        Assert.Equal(FrameAction.Hold, Decide(WindowFrameReason.Content, lastInteraction: now - FramePlanner.InteractiveWindowMs).Action);
    }

    [Fact]
    public void FirstFrameIsNeverHeld() =>
        Assert.Equal(FrameAction.Render, Decide(WindowFrameReason.Content, lastPresent: 0, sincePresent: 1).Action);

    [Theory]
    [InlineData(WindowFrameReason.Input)]
    [InlineData(WindowFrameReason.Resize)]
    [InlineData(WindowFrameReason.Overlay)]
    [InlineData(WindowFrameReason.CursorBlink)]
    [InlineData(WindowFrameReason.Initial)]
    [InlineData(WindowFrameReason.ImageAnimation)]
    [InlineData(WindowFrameReason.Content | WindowFrameReason.Selection)]
    public void ReasonsOtherThanContentAreNeverHeld(WindowFrameReason reasons) =>
        Assert.Equal(new FramePlan(FrameAction.Render, 0), Decide(reasons, sincePresent: 1));

    [Theory]
    // refresh Hz, backlogged, refresh-aware -> interval ms
    [InlineData(30, false, true, 33)]
    [InlineData(60, false, true, 16)]
    [InlineData(75, false, true, 13)]
    [InlineData(120, false, true, 8)]
    [InlineData(144, false, true, 6)]
    [InlineData(240, false, true, 4)]
    [InlineData(360, false, true, 4)]        // clamped to 240 Hz
    [InlineData(24, false, true, 33)]        // clamped to 30 Hz
    [InlineData(0, false, true, 16)]         // unknown: 60 Hz
    [InlineData(-5, false, true, 16)]
    [InlineData(100_000, false, true, 16)]   // implausible report: 60 Hz, not 240
    [InlineData(60, true, true, 30)]         // backlogged keeps at least 30 ms
    [InlineData(30, true, true, 33)]         // ... or one period when that is longer
    [InlineData(144, true, true, 30)]
    [InlineData(144, false, false, 8)]       // default: fixed interval, refresh rate ignored
    [InlineData(30, false, false, 8)]
    [InlineData(0, true, false, 30)]
    public void MinContentFrameIntervalFollowsTheRefreshRateOnlyWhenEnabled(int hz, bool backlogged, bool refreshAware, int expected) =>
        Assert.Equal(expected, FramePlanner.MinContentFrameIntervalMs(hz, backlogged, refreshAware));

    [Theory]
    [InlineData(2L, 6L)]
    [InlineData(8L, 0L)]
    public void CoalesceRemainingUsesTheGivenInterval(long sincePresent, long expected) =>
        Assert.Equal(expected, FramePlanner.CoalesceRemainingMs(LongAgo + sincePresent, LongAgo, NoInteraction, Interval));
}
