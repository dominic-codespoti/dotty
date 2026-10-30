using System.Text;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class FrameTimingsTests
{
    // 1000 ticks per second: one tick is one millisecond, so expected values read directly.
    private static FrameTimings Create() => new(ticksPerSecond: 1000);

    [Fact]
    public void EmptyMetricSummarisesToZero()
    {
        Assert.Equal(default(FrameTimingSummary), Create().Summarize(FrameTimingMetric.Compose));
    }

    [Theory]
    // samples (ms), p50, p95, max — nearest rank: index ceil(p/100*n)-1 of the sorted samples.
    [InlineData(new long[] { 7 }, 7d, 7d, 7d)]
    [InlineData(new long[] { 4, 1, 3, 2 }, 2d, 4d, 4d)]
    [InlineData(new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 }, 50d, 100d, 100d)]
    [InlineData(new long[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 90 }, 5d, 5d, 90d)]
    public void PercentilesUseNearestRank(long[] samples, double p50, double p95, double max)
    {
        var timings = Create();
        foreach (long sample in samples)
            timings.RecordCompose(100, 100 + sample);

        var summary = timings.Summarize(FrameTimingMetric.Compose);

        Assert.Equal(new FrameTimingSummary(samples.Length, p50, p95, max), summary);
    }

    [Fact]
    public void RingKeepsOnlyTheNewestCapacitySamples()
    {
        var timings = Create();
        for (int i = 0; i < FrameTimings.Capacity; i++)
            timings.RecordRender(0, 1000);
        for (int i = 0; i < FrameTimings.Capacity; i++)
            timings.RecordRender(0, 2);

        var summary = timings.Summarize(FrameTimingMetric.Render);

        Assert.Equal(new FrameTimingSummary(FrameTimings.Capacity, 2, 2, 2), summary);
    }

    [Fact]
    public void SwapDerivesPresentIntervalFromConsecutiveSwapReturns()
    {
        var timings = Create();
        timings.RecordSwap(100, 101);
        timings.RecordSwap(110, 117);
        timings.RecordSwap(140, 141);

        Assert.Equal(new FrameTimingSummary(3, 1, 7, 7), timings.Summarize(FrameTimingMetric.Swap));
        // Intervals 117-101 = 16 and 141-117 = 24; the first swap has no predecessor.
        Assert.Equal(new FrameTimingSummary(2, 16, 24, 24), timings.Summarize(FrameTimingMetric.PresentInterval));
    }

    [Fact]
    public void InputToSwapMeasuresTheOldestUnpresentedInput()
    {
        var timings = Create();
        timings.MarkInput(1000);
        timings.MarkInput(1005); // newer input before the same present: ignored
        timings.RecordSwap(1010, 1012);
        timings.RecordSwap(1020, 1021); // no input since the last swap: no sample
        timings.MarkInput(1030);
        timings.RecordSwap(1033, 1034);

        Assert.Equal(new FrameTimingSummary(2, 4, 12, 12), timings.Summarize(FrameTimingMetric.InputToSwap));
    }

    [Fact]
    public void ClockGoingBackwardsRecordsZeroNotNegative()
    {
        var timings = Create();
        timings.RecordCompose(50, 40);

        Assert.Equal(new FrameTimingSummary(1, 0, 0, 0), timings.Summarize(FrameTimingMetric.Compose));
    }

    [Fact]
    public void ClearDropsSamplesAndPendingInput()
    {
        var timings = Create();
        timings.RecordSwap(0, 1);
        timings.MarkInput(2);
        timings.Clear();
        timings.RecordSwap(10, 11);

        Assert.Equal(1, timings.Summarize(FrameTimingMetric.Swap).Count);
        Assert.Equal(0, timings.Summarize(FrameTimingMetric.PresentInterval).Count);
        Assert.Equal(0, timings.Summarize(FrameTimingMetric.InputToSwap).Count);
    }

    [Fact]
    public void SubMillisecondTicksKeepFractionsInJson()
    {
        var timings = new FrameTimings(ticksPerSecond: 1_000_000);
        timings.RecordCompose(0, 1_250);
        var json = new StringBuilder();

        timings.AppendJson(json);

        Assert.Equal(
            "{\"compose\":{\"count\":1,\"p50Ms\":1.25,\"p95Ms\":1.25,\"maxMs\":1.25}," +
            "\"render\":{\"count\":0,\"p50Ms\":0,\"p95Ms\":0,\"maxMs\":0}," +
            "\"swap\":{\"count\":0,\"p50Ms\":0,\"p95Ms\":0,\"maxMs\":0}," +
            "\"presentInterval\":{\"count\":0,\"p50Ms\":0,\"p95Ms\":0,\"maxMs\":0}," +
            "\"inputToSwap\":{\"count\":0,\"p50Ms\":0,\"p95Ms\":0,\"maxMs\":0}}",
            json.ToString());
    }

    [Fact]
    public void RecordingIsAllocationFree()
    {
        var timings = Create();
        long t = 1;
        AllocationAssert.NoAllocations(() =>
        {
            timings.MarkInput(t);
            timings.RecordCompose(t, t + 1);
            timings.RecordRender(t + 1, t + 3);
            timings.RecordSwap(t + 3, t + 4);
            t += 5;
        });
    }

    [Fact]
    public void SummarisingIsAllocationFree()
    {
        var timings = Create();
        for (int i = 0; i < FrameTimings.Capacity; i++)
            timings.RecordCompose(0, i % 17);

        AllocationAssert.NoAllocations(() => timings.Summarize(FrameTimingMetric.Compose));
    }
}
