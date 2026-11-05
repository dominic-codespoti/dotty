using System;
using System.Globalization;
using System.Text;

namespace Dotty.Silk;

/// <summary>Timed stages of a presented frame (plan-11 WI-0b).</summary>
internal enum FrameTimingMetric
{
    /// <summary><c>TerminalSceneComposer.Compose</c>: CPU time to build the frame's instances.</summary>
    Compose,
    /// <summary><c>SilkTerminalRenderer.Render</c>: CPU time to upload and submit the draw calls.</summary>
    Render,
    /// <summary>The <c>SwapBuffers</c> call itself (blocking time in the driver/compositor).</summary>
    Swap,
    /// <summary>Time between the returns of two consecutive <c>SwapBuffers</c> calls.</summary>
    PresentInterval,
    /// <summary>From the first input event not yet presented to the return of the <c>SwapBuffers</c> that presents it.</summary>
    InputToSwap,
}

/// <summary>Nearest-rank percentiles over the samples currently held for one metric, in milliseconds.</summary>
internal readonly record struct FrameTimingSummary(int Count, double P50Ms, double P95Ms, double MaxMs);

/// <summary>
/// Fixed-size rings of frame-stage durations with percentile summaries for the <c>STATS</c> control command.
/// Recording is O(1) and allocation-free (all storage is allocated in the constructor); summarising sorts a
/// preallocated scratch copy and is meant for diagnostics, not the frame loop. UI thread only.
/// Timestamps are raw ticks of a clock whose frequency is given to the constructor
/// (<see cref="System.Diagnostics.Stopwatch.Frequency"/> in the host, an injected value in tests).
/// </summary>
internal sealed class FrameTimings
{
    internal const int Capacity = 512;
    private const int MetricCount = (int)FrameTimingMetric.InputToSwap + 1;

    private readonly long _ticksPerSecond;
    private readonly long[][] _samples;
    private readonly int[] _counts = new int[MetricCount];
    private readonly int[] _next = new int[MetricCount];
    private readonly long[] _scratch = new long[Capacity];
    private long _lastSwapEndTicks;
    private long _pendingInputTicks;

    internal FrameTimings(long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _ticksPerSecond = ticksPerSecond;
        _samples = new long[MetricCount][];
        for (int i = 0; i < MetricCount; i++)
            _samples[i] = new long[Capacity];
    }

    /// <summary>
    /// Marks an input event. Only the first input since the last swap is kept, so <see cref="FrameTimingMetric.InputToSwap"/>
    /// measures the oldest unpresented input (the latency the user feels), not the newest.
    /// </summary>
    internal void MarkInput(long nowTicks)
    {
        if (_pendingInputTicks == 0)
            _pendingInputTicks = nowTicks;
    }

    internal void RecordCompose(long startTicks, long endTicks) => Add(FrameTimingMetric.Compose, endTicks - startTicks);

    internal void RecordRender(long startTicks, long endTicks) => Add(FrameTimingMetric.Render, endTicks - startTicks);

    /// <summary>Records one <c>SwapBuffers</c> call; also derives the present interval and input-to-swap latency.</summary>
    internal void RecordSwap(long startTicks, long endTicks)
    {
        Add(FrameTimingMetric.Swap, endTicks - startTicks);
        if (_lastSwapEndTicks != 0)
            Add(FrameTimingMetric.PresentInterval, endTicks - _lastSwapEndTicks);
        _lastSwapEndTicks = endTicks;
        if (_pendingInputTicks != 0)
        {
            Add(FrameTimingMetric.InputToSwap, endTicks - _pendingInputTicks);
            _pendingInputTicks = 0;
        }
    }

    internal void Clear()
    {
        Array.Clear(_counts);
        Array.Clear(_next);
        _lastSwapEndTicks = 0;
        _pendingInputTicks = 0;
    }

    internal FrameTimingSummary Summarize(FrameTimingMetric metric)
    {
        int index = (int)metric;
        int count = _counts[index];
        if (count == 0)
            return default;

        Array.Copy(_samples[index], _scratch, count);
        Array.Sort(_scratch, 0, count);
        return new FrameTimingSummary(
            count,
            ToMs(_scratch[NearestRank(count, 50)]),
            ToMs(_scratch[NearestRank(count, 95)]),
            ToMs(_scratch[count - 1]));
    }

    /// <summary>
    /// Appends <c>{"compose":{"count":n,"p50Ms":x,"p95Ms":x,"maxMs":x},"render":{...},"swap":{...},
    /// "presentInterval":{...},"inputToSwap":{...}}</c>.
    /// </summary>
    internal void AppendJson(StringBuilder builder)
    {
        builder.Append('{');
        for (int i = 0; i < MetricCount; i++)
        {
            if (i > 0)
                builder.Append(',');
            var metric = (FrameTimingMetric)i;
            var summary = Summarize(metric);
            builder.Append('"').Append(JsonName(metric)).Append("\":{\"count\":")
                .Append(summary.Count.ToString(CultureInfo.InvariantCulture))
                .Append(",\"p50Ms\":").Append(FormatMs(summary.P50Ms))
                .Append(",\"p95Ms\":").Append(FormatMs(summary.P95Ms))
                .Append(",\"maxMs\":").Append(FormatMs(summary.MaxMs))
                .Append('}');
        }
        builder.Append('}');
    }

    private void Add(FrameTimingMetric metric, long elapsedTicks)
    {
        int index = (int)metric;
        _samples[index][_next[index]] = Math.Max(0L, elapsedTicks);
        _next[index] = _next[index] + 1 == Capacity ? 0 : _next[index] + 1;
        if (_counts[index] < Capacity)
            _counts[index]++;
    }

    /// <summary>Zero-based nearest-rank index: ceil(p/100 * n) - 1, clamped to [0, n-1].</summary>
    private static int NearestRank(int count, int percentile) =>
        Math.Clamp((int)(((long)percentile * count + 99) / 100) - 1, 0, count - 1);

    private double ToMs(long ticks) => ticks * 1000.0 / _ticksPerSecond;

    private static string FormatMs(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture);

    private static string JsonName(FrameTimingMetric metric) => metric switch
    {
        FrameTimingMetric.Compose => "compose",
        FrameTimingMetric.Render => "render",
        FrameTimingMetric.Swap => "swap",
        FrameTimingMetric.PresentInterval => "presentInterval",
        _ => "inputToSwap",
    };
}
