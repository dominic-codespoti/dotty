using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class BlinkPolicyTests
{
    [Theory]
    // origin, interval, now, expected on, expected next toggle
    [InlineData(1000L, 500, 1000L, true, 1500L)]
    [InlineData(1000L, 500, 1499L, true, 1500L)]
    [InlineData(1000L, 500, 1500L, false, 2000L)]
    [InlineData(1000L, 500, 1999L, false, 2000L)]
    [InlineData(1000L, 500, 2000L, true, 2500L)]
    [InlineData(1000L, 500, 10_250L, true, 10_500L)]
    [InlineData(1000L, 500, 10_750L, false, 11_000L)]
    // Before the origin the phase is on and the first change is the end of the first on segment.
    [InlineData(1000L, 500, 0L, true, 1500L)]
    // Negative origins (a clock near zero) keep the same parity rule.
    [InlineData(-400L, 500, 50L, true, 100L)]
    [InlineData(-400L, 500, 100L, false, 600L)]
    // Intervals below 1 are raised to 1.
    [InlineData(0L, 0, 3L, false, 4L)]
    public void IsOnAndNextToggleFollowTheSegmentGrid(long origin, int interval, long now, bool expectedOn, long expectedNext)
    {
        var policy = new BlinkPolicy(origin, interval);

        Assert.True(policy.IsBlinking);
        Assert.Equal(expectedOn, policy.IsOn(now));
        Assert.Equal(expectedNext, policy.NextToggleMs(now));
        Assert.NotEqual(policy.IsOn(now), policy.IsOn(policy.NextToggleMs(now)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123_456L)]
    [InlineData(long.MinValue)]
    public void SteadyIsAlwaysOnAndNeverSchedules(long now)
    {
        Assert.False(BlinkPolicy.Steady.IsBlinking);
        Assert.True(BlinkPolicy.Steady.IsOn(now));
        Assert.Equal(long.MaxValue, BlinkPolicy.Steady.NextToggleMs(now));
    }
}
