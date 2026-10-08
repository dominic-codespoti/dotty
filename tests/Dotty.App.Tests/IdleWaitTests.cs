using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class IdleWaitTests
{
    private static IdleWaitInputs Inputs(long now = 0, bool retry = false, bool blink = false, long lastBlink = 0, int blinkInterval = 100, long lastLua = 0, int luaInterval = 1000, long coalesce = 0, long syncHold = 0, bool autoscroll = false) => new(now, retry, blink, lastBlink, blinkInterval, lastLua, luaInterval, coalesce, syncHold, autoscroll);

    [Fact]
    public void NoDeadlinesUsesSafetyCap() => Assert.Equal(IdleWait.SafetyCapMs, IdleWait.Compute(Inputs()));

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
}
