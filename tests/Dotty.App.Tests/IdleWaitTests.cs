using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class IdleWaitTests
{
    private static IdleWaitInputs Inputs(long now = 0, bool retry = false, bool blink = false, long lastBlink = 0, int blinkInterval = 100, long lastLua = 0, int luaInterval = 1000, long coalesce = 0, long syncHold = 0, bool autoscroll = false, long image = 0, long control = 0) => new(now, retry, blink, lastBlink, blinkInterval, lastLua, luaInterval, coalesce, syncHold, autoscroll, image, control);

    [Fact]
    public void NoDeadlinesUsesSafetyCap() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs()));

    [Fact]
    public void ParkedControlWaitDeadlineBoundsTheWait()
    {
        Assert.Equal(7, IdleWait.Compute(Inputs(control: 7, coalesce: 14)));
        Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs(control: 0)));
    }

    [Theory]
    [InlineData(10, 0, 90)]
    [InlineData(70, 0, 30)]
    public void BlinkReturnsRemainingTime(long now, long last, int expected) => Assert.Equal(expected, IdleWait.Compute(Inputs(now: now, blink: true, lastBlink: last, blinkInterval: 100)));

    [Fact]
    public void LuaDeadlineReturnsRemainingTime() => Assert.Equal(30, IdleWait.Compute(Inputs(now: 70, luaInterval: 100)));

    [Fact]
    public void CoalesceAndSyncHoldReturnRemainingTime()
    {
        Assert.Equal(25, IdleWait.Compute(Inputs(coalesce: 25)));
        Assert.Equal(40, IdleWait.Compute(Inputs(syncHold: 40)));
    }

    [Fact]
    public void DueDeadlinesReturnZeroNeverNegative()
    {
        Assert.Equal(0, IdleWait.Compute(Inputs(now: 100, blink: true, blinkInterval: 100)));
        Assert.Equal(0, IdleWait.Compute(Inputs(now: 1100, lastLua: 0, luaInterval: 1000)));
    }

    [Fact]
    public void AutoscrollAndRetryHaveTheirContractIntervals()
    {
        Assert.Equal(IdleWait.AutoscrollTickMs, IdleWait.Compute(Inputs(autoscroll: true)));
        Assert.Equal(IdleWait.RetryMs, IdleWait.Compute(Inputs(retry: true)));
    }

    [Fact]
    public void UsesMinimumOfApplicableDeadlines() => Assert.Equal(12, IdleWait.Compute(Inputs(now: 20, blink: true, lastBlink: 0, blinkInterval: 32, coalesce: 14, syncHold: 18, autoscroll: true)));

    [Fact]
    public void InactiveBlinkDoesNotContributeOverdueDeadline() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs(now: 500, blink: false, lastBlink: 0, blinkInterval: 100)));

    [Fact]
    public void LongDeadlineIsClampedToSafetyCap() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs(coalesce: IdleWait.SafetyCapMs + 50)));

    [Fact]
    public void ImageAnimationRemainingIsOneMoreMinimumInput()
    {
        Assert.Equal(12, IdleWait.Compute(Inputs(image: 12, coalesce: 14, syncHold: 18)));
        Assert.Equal(5, IdleWait.Compute(Inputs(now: 20, blink: true, lastBlink: 0, blinkInterval: 32, image: 5)));
        // The blink deadline is 0 + 32 - 20 = 12 ms away, nearer than a 50 ms animation deadline.
        Assert.Equal(12, IdleWait.Compute(Inputs(now: 20, blink: true, lastBlink: 0, blinkInterval: 32, image: 50)));
    }

    [Fact]
    public void ZeroImageAnimationRemainingIsIgnored() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs(image: 0)));

    [Fact]
    public void LongImageAnimationRemainingIsClampedToSafetyCap() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs(image: 400)));

    [Theory]
    [InlineData(0L, 123L, 1000L, 0L)]                  // no deadline: not an input
    [InlineData(1500L, 1000L, 1000L, 500L)]            // 500 ticks at 1 kHz
    [InlineData(1000L, 1000L, 1000L, 1L)]              // due now: still a positive wait
    [InlineData(900L, 1000L, 1000L, 1L)]               // overdue
    [InlineData(1_500_001L, 0L, 1_000_000_000L, 2L)]   // 1.500001 ms rounds up, never wakes early
    [InlineData(2_000_000L, 0L, 1_000_000_000L, 2L)]   // exactly 2 ms
    [InlineData(1L, 0L, 1_000_000_000L, 1L)]           // 1 ns
    [InlineData(25_000L, 0L, 10_000_000L, 3L)]         // 2.5 ms at 10 MHz
    [InlineData(3500L, 0L, 1000L, 3500L)]              // whole seconds plus a fraction
    [InlineData(2_500_000L, 0L, 1_000_000L, 2500L)]    // 2.5 s at 1 MHz
    [InlineData(long.MaxValue, 0L, 1000L, (long)int.MaxValue)] // far future saturates instead of overflowing
    public void ImageAnimationRemainingMsConvertsTicksRoundingUp(long deadline, long now, long frequency, long expected) =>
        Assert.Equal(expected, IdleWait.ImageAnimationRemainingMs(deadline, now, frequency));
}
