using System;
using System.Diagnostics;
using System.Threading;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// Fixed-size, opt-in counters for the real Avalonia terminal render path.
/// Recording performs no formatting, logging, collection growth, or managed allocation.
/// Snapshots allocate only when requested by the test/diagnostic command interface.
/// </summary>
internal sealed class TerminalRenderTelemetry
{
    private static readonly long[] DurationBucketUpperMicroseconds =
    {
        250,
        500,
        1_000,
        2_000,
        4_000,
        8_000,
        16_000,
        33_000,
        67_000,
        long.MaxValue,
    };

    private readonly long[] _renderDurationHistogram = new long[DurationBucketUpperMicroseconds.Length];

    private bool _enabled;
    private long _collectionStartedTimestamp;
    private long _renderNotifications;
    private long _coalescedRenderNotifications;
    private long _uiRenderUpdates;
    private long _frameRequests;
    private long _renderCalls;
    private long _contentRenderAttempts;
    private long _contentFrames;
    private long _bufferLockMisses;
    private long _bitmapRecreations;
    private long _totalRenderTicks;
    private long _minimumRenderTicks = long.MaxValue;
    private long _maximumRenderTicks;
    private long _totalContentTicks;
    private long _maximumContentTicks;
    private long _totalRenderAllocatedBytes;
    private long _maximumRenderAllocatedBytes;
    private long _lastBufferGeneration;
    private long _lastRenderScaleThousandths;
    private long _lastPixelWidth;
    private long _lastPixelHeight;
    private long _presentFrames;
    private long _totalPresentTicks;
    private long _minimumPresentTicks = long.MaxValue;
    private long _maximumPresentTicks;

    internal static bool DefaultEnabled =>
        !string.IsNullOrWhiteSpace(Dotty.Env.GetEnvironmentVariable("DOTTY_RENDER_DIAGNOSTICS"));

    internal bool Enabled => Volatile.Read(ref _enabled);

    internal void Start(bool reset = true)
    {
        if (reset)
        {
            Reset();
        }
        else if (Volatile.Read(ref _collectionStartedTimestamp) == 0)
        {
            Volatile.Write(ref _collectionStartedTimestamp, Stopwatch.GetTimestamp());
        }

        Volatile.Write(ref _enabled, true);
    }

    internal void Stop() => Volatile.Write(ref _enabled, false);

    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Start(reset: false);
        }
        else
        {
            Stop();
        }
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _collectionStartedTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _renderNotifications, 0);
        Interlocked.Exchange(ref _coalescedRenderNotifications, 0);
        Interlocked.Exchange(ref _uiRenderUpdates, 0);
        Interlocked.Exchange(ref _frameRequests, 0);
        Interlocked.Exchange(ref _renderCalls, 0);
        Interlocked.Exchange(ref _contentRenderAttempts, 0);
        Interlocked.Exchange(ref _contentFrames, 0);
        Interlocked.Exchange(ref _bufferLockMisses, 0);
        Interlocked.Exchange(ref _bitmapRecreations, 0);
        Interlocked.Exchange(ref _totalRenderTicks, 0);
        Interlocked.Exchange(ref _minimumRenderTicks, long.MaxValue);
        Interlocked.Exchange(ref _maximumRenderTicks, 0);
        Interlocked.Exchange(ref _totalContentTicks, 0);
        Interlocked.Exchange(ref _maximumContentTicks, 0);
        Interlocked.Exchange(ref _totalRenderAllocatedBytes, 0);
        Interlocked.Exchange(ref _maximumRenderAllocatedBytes, 0);
        Interlocked.Exchange(ref _lastBufferGeneration, 0);
        Interlocked.Exchange(ref _lastRenderScaleThousandths, 0);
        Interlocked.Exchange(ref _lastPixelWidth, 0);
        Interlocked.Exchange(ref _lastPixelHeight, 0);
        Interlocked.Exchange(ref _presentFrames, 0);
        Interlocked.Exchange(ref _totalPresentTicks, 0);
        Interlocked.Exchange(ref _minimumPresentTicks, long.MaxValue);
        Interlocked.Exchange(ref _maximumPresentTicks, 0);

        for (int i = 0; i < _renderDurationHistogram.Length; i++)
        {
            Interlocked.Exchange(ref _renderDurationHistogram[i], 0);
        }
    }

    internal void RecordRenderNotification(bool coalesced)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _renderNotifications);
        if (coalesced)
        {
            Interlocked.Increment(ref _coalescedRenderNotifications);
        }
    }

    internal void RecordUiRenderUpdate()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _uiRenderUpdates);
        }
    }

    internal void RecordFrameRequest()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frameRequests);
        }
    }

    internal RenderMeasurement BeginRender()
    {
        if (!Enabled)
        {
            return default;
        }

        Interlocked.Increment(ref _renderCalls);
        return new RenderMeasurement(
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread());
    }

    internal void CompleteRender(in RenderMeasurement measurement)
    {
        if (!measurement.IsEnabled)
        {
            return;
        }

        long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - measurement.StartedTimestamp);
        long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - measurement.AllocatedBytes);

        Interlocked.Add(ref _totalRenderTicks, elapsedTicks);
        UpdateMinimum(ref _minimumRenderTicks, elapsedTicks);
        UpdateMaximum(ref _maximumRenderTicks, elapsedTicks);
        Interlocked.Add(ref _totalRenderAllocatedBytes, allocatedBytes);
        UpdateMaximum(ref _maximumRenderAllocatedBytes, allocatedBytes);

        long elapsedMicroseconds = TicksToMicroseconds(elapsedTicks);
        int bucket = FindDurationBucket(elapsedMicroseconds);
        Interlocked.Increment(ref _renderDurationHistogram[bucket]);
    }

    internal long BeginContentRender()
    {
        if (!Enabled)
        {
            return 0;
        }

        Interlocked.Increment(ref _contentRenderAttempts);
        return Stopwatch.GetTimestamp();
    }

    internal void CompleteContentRender(long startedTimestamp, bool completed)
    {
        if (startedTimestamp == 0)
        {
            return;
        }

        long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startedTimestamp);
        Interlocked.Add(ref _totalContentTicks, elapsedTicks);
        UpdateMaximum(ref _maximumContentTicks, elapsedTicks);
        if (completed)
        {
            Interlocked.Increment(ref _contentFrames);
        }
    }

    internal void RecordBufferLockMiss()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _bufferLockMisses);
        }
    }

    internal void RecordBitmapRecreation()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _bitmapRecreations);
        }
    }

    internal void RecordBufferState(ulong generation, double renderScale, int pixelWidth, int pixelHeight)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Exchange(ref _lastBufferGeneration, unchecked((long)generation));
        Interlocked.Exchange(ref _lastRenderScaleThousandths, (long)Math.Round(renderScale * 1_000.0));
        Interlocked.Exchange(ref _lastPixelWidth, pixelWidth);
        Interlocked.Exchange(ref _lastPixelHeight, pixelHeight);
    }

    /// <summary>
    /// Records the interval between consecutive presentation-gate frame
    /// callbacks. The difference between this and the canvas render time is
    /// the compositor/upload cost not visible to canvas.Render.
    /// </summary>
    internal void RecordPresentInterval(long ticks)
    {
        if (!Enabled)
        {
            return;
        }

        ticks = Math.Max(0, ticks);
        Interlocked.Increment(ref _presentFrames);
        Interlocked.Add(ref _totalPresentTicks, ticks);
        UpdateMinimum(ref _minimumPresentTicks, ticks);
        UpdateMaximum(ref _maximumPresentTicks, ticks);
    }

    internal TerminalRenderTelemetrySnapshot Snapshot()
    {
        var histogram = new long[_renderDurationHistogram.Length];
        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] = Interlocked.Read(ref _renderDurationHistogram[i]);
        }

        long started = Interlocked.Read(ref _collectionStartedTimestamp);
        long elapsed = started == 0 ? 0 : Math.Max(0, Stopwatch.GetTimestamp() - started);
        long minimumRenderTicks = Interlocked.Read(ref _minimumRenderTicks);
        if (minimumRenderTicks == long.MaxValue)
        {
            minimumRenderTicks = 0;
        }
        long minimumPresentTicks = Interlocked.Read(ref _minimumPresentTicks);
        if (minimumPresentTicks == long.MaxValue)
        {
            minimumPresentTicks = 0;
        }

        return new TerminalRenderTelemetrySnapshot(
            Enabled,
            elapsed,
            Interlocked.Read(ref _renderNotifications),
            Interlocked.Read(ref _coalescedRenderNotifications),
            Interlocked.Read(ref _uiRenderUpdates),
            Interlocked.Read(ref _frameRequests),
            Interlocked.Read(ref _renderCalls),
            Interlocked.Read(ref _contentRenderAttempts),
            Interlocked.Read(ref _contentFrames),
            Interlocked.Read(ref _bufferLockMisses),
            Interlocked.Read(ref _bitmapRecreations),
            Interlocked.Read(ref _totalRenderTicks),
            minimumRenderTicks,
            Interlocked.Read(ref _maximumRenderTicks),
            Interlocked.Read(ref _totalContentTicks),
            Interlocked.Read(ref _maximumContentTicks),
            Interlocked.Read(ref _totalRenderAllocatedBytes),
            Interlocked.Read(ref _maximumRenderAllocatedBytes),
            unchecked((ulong)Interlocked.Read(ref _lastBufferGeneration)),
            Interlocked.Read(ref _lastRenderScaleThousandths) / 1_000.0,
            (int)Interlocked.Read(ref _lastPixelWidth),
            (int)Interlocked.Read(ref _lastPixelHeight),
            Interlocked.Read(ref _presentFrames),
            Interlocked.Read(ref _totalPresentTicks),
            minimumPresentTicks,
            Interlocked.Read(ref _maximumPresentTicks),
            histogram);
    }

    internal static TerminalRenderTelemetrySnapshot Aggregate(
        ReadOnlySpan<TerminalRenderTelemetrySnapshot> snapshots)
    {
        if (snapshots.IsEmpty)
        {
            return TerminalRenderTelemetrySnapshot.Empty;
        }

        bool enabled = false;
        long elapsedTicks = 0;
        long renderNotifications = 0;
        long coalescedRenderNotifications = 0;
        long uiRenderUpdates = 0;
        long frameRequests = 0;
        long renderCalls = 0;
        long contentRenderAttempts = 0;
        long contentFrames = 0;
        long bufferLockMisses = 0;
        long bitmapRecreations = 0;
        long totalRenderTicks = 0;
        long minimumRenderTicks = long.MaxValue;
        long maximumRenderTicks = 0;
        long totalContentTicks = 0;
        long maximumContentTicks = 0;
        long totalRenderAllocatedBytes = 0;
        long maximumRenderAllocatedBytes = 0;
        long presentFrames = 0;
        long totalPresentTicks = 0;
        long minimumPresentTicks = long.MaxValue;
        long maximumPresentTicks = 0;
        var histogram = new long[DurationBucketUpperMicroseconds.Length];

        for (int i = 0; i < snapshots.Length; i++)
        {
            ref readonly var snapshot = ref snapshots[i];
            enabled |= snapshot.Enabled;
            elapsedTicks = Math.Max(elapsedTicks, snapshot.ElapsedTicks);
            renderNotifications += snapshot.RenderNotifications;
            coalescedRenderNotifications += snapshot.CoalescedRenderNotifications;
            uiRenderUpdates += snapshot.UiRenderUpdates;
            frameRequests += snapshot.FrameRequests;
            renderCalls += snapshot.RenderCalls;
            contentRenderAttempts += snapshot.ContentRenderAttempts;
            contentFrames += snapshot.ContentFrames;
            bufferLockMisses += snapshot.BufferLockMisses;
            bitmapRecreations += snapshot.BitmapRecreations;
            totalRenderTicks += snapshot.TotalRenderTicks;
            if (snapshot.RenderCalls > 0)
            {
                minimumRenderTicks = Math.Min(minimumRenderTicks, snapshot.MinimumRenderTicks);
            }
            maximumRenderTicks = Math.Max(maximumRenderTicks, snapshot.MaximumRenderTicks);
            totalContentTicks += snapshot.TotalContentTicks;
            maximumContentTicks = Math.Max(maximumContentTicks, snapshot.MaximumContentTicks);
            totalRenderAllocatedBytes += snapshot.TotalRenderAllocatedBytes;
            maximumRenderAllocatedBytes = Math.Max(maximumRenderAllocatedBytes, snapshot.MaximumRenderAllocatedBytes);
            presentFrames += snapshot.PresentFrames;
            totalPresentTicks += snapshot.TotalPresentTicks;
            if (snapshot.PresentFrames > 0)
            {
                minimumPresentTicks = Math.Min(minimumPresentTicks, snapshot.MinimumPresentTicks);
            }
            maximumPresentTicks = Math.Max(maximumPresentTicks, snapshot.MaximumPresentTicks);

            for (int bucket = 0; bucket < histogram.Length; bucket++)
            {
                histogram[bucket] += snapshot.RenderDurationHistogram[bucket];
            }
        }

        if (minimumRenderTicks == long.MaxValue)
        {
            minimumRenderTicks = 0;
        }
        if (minimumPresentTicks == long.MaxValue)
        {
            minimumPresentTicks = 0;
        }

        return new TerminalRenderTelemetrySnapshot(
            enabled,
            elapsedTicks,
            renderNotifications,
            coalescedRenderNotifications,
            uiRenderUpdates,
            frameRequests,
            renderCalls,
            contentRenderAttempts,
            contentFrames,
            bufferLockMisses,
            bitmapRecreations,
            totalRenderTicks,
            minimumRenderTicks,
            maximumRenderTicks,
            totalContentTicks,
            maximumContentTicks,
            totalRenderAllocatedBytes,
            maximumRenderAllocatedBytes,
            0,
            0,
            0,
            0,
            presentFrames,
            totalPresentTicks,
            minimumPresentTicks,
            maximumPresentTicks,
            histogram);
    }

    internal static double TicksToMilliseconds(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1_000.0 / Stopwatch.Frequency;

    internal static double TicksToSeconds(long ticks) =>
        ticks <= 0 ? 0 : ticks / (double)Stopwatch.Frequency;

    internal static double HistogramP95Milliseconds(long[] histogram)
    {
        long sampleCount = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            sampleCount += histogram[i];
        }

        if (sampleCount == 0)
        {
            return 0;
        }

        long target = (long)Math.Ceiling(sampleCount * 0.95);
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
            {
                long upperMicroseconds = DurationBucketUpperMicroseconds[i];
                return upperMicroseconds == long.MaxValue
                    ? 67.0
                    : upperMicroseconds / 1_000.0;
            }
        }

        return 0;
    }

    private static long TicksToMicroseconds(long ticks) =>
        ticks <= 0 ? 0 : (long)(ticks * 1_000_000.0 / Stopwatch.Frequency);

    private static int FindDurationBucket(long elapsedMicroseconds)
    {
        for (int i = 0; i < DurationBucketUpperMicroseconds.Length; i++)
        {
            if (elapsedMicroseconds <= DurationBucketUpperMicroseconds[i])
            {
                return i;
            }
        }

        return DurationBucketUpperMicroseconds.Length - 1;
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    private static void UpdateMinimum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate < current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }
}

internal readonly struct RenderMeasurement
{
    internal RenderMeasurement(long startedTimestamp, long allocatedBytes)
    {
        StartedTimestamp = startedTimestamp;
        AllocatedBytes = allocatedBytes;
    }

    internal bool IsEnabled => StartedTimestamp != 0;
    internal long StartedTimestamp { get; }
    internal long AllocatedBytes { get; }
}

internal sealed record TerminalRenderTelemetrySnapshot(
    bool Enabled,
    long ElapsedTicks,
    long RenderNotifications,
    long CoalescedRenderNotifications,
    long UiRenderUpdates,
    long FrameRequests,
    long RenderCalls,
    long ContentRenderAttempts,
    long ContentFrames,
    long BufferLockMisses,
    long BitmapRecreations,
    long TotalRenderTicks,
    long MinimumRenderTicks,
    long MaximumRenderTicks,
    long TotalContentTicks,
    long MaximumContentTicks,
    long TotalRenderAllocatedBytes,
    long MaximumRenderAllocatedBytes,
    ulong LastBufferGeneration,
    double LastRenderScale,
    int LastPixelWidth,
    int LastPixelHeight,
    long PresentFrames,
    long TotalPresentTicks,
    long MinimumPresentTicks,
    long MaximumPresentTicks,
    long[] RenderDurationHistogram)
{
    internal static TerminalRenderTelemetrySnapshot Empty { get; } = new(
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        new long[10]);

    internal double ElapsedSeconds => TerminalRenderTelemetry.TicksToSeconds(ElapsedTicks);
    internal double RenderRate => ElapsedSeconds <= 0 ? 0 : RenderCalls / ElapsedSeconds;
    internal double MinimumRenderMilliseconds => TerminalRenderTelemetry.TicksToMilliseconds(MinimumRenderTicks);
    internal double MaximumRenderMilliseconds => TerminalRenderTelemetry.TicksToMilliseconds(MaximumRenderTicks);
    internal double AverageRenderMilliseconds => RenderCalls == 0
        ? 0
        : TerminalRenderTelemetry.TicksToMilliseconds(TotalRenderTicks) / RenderCalls;
    internal double P95RenderMilliseconds => TerminalRenderTelemetry.HistogramP95Milliseconds(RenderDurationHistogram);
    internal double MaximumContentMilliseconds => TerminalRenderTelemetry.TicksToMilliseconds(MaximumContentTicks);
    internal double AverageContentMilliseconds => ContentRenderAttempts == 0
        ? 0
        : TerminalRenderTelemetry.TicksToMilliseconds(TotalContentTicks) / ContentRenderAttempts;
    internal double AverageRenderAllocatedBytes => RenderCalls == 0
        ? 0
        : TotalRenderAllocatedBytes / (double)RenderCalls;
    internal double MinimumPresentMilliseconds => TerminalRenderTelemetry.TicksToMilliseconds(MinimumPresentTicks);
    internal double MaximumPresentMilliseconds => TerminalRenderTelemetry.TicksToMilliseconds(MaximumPresentTicks);
    internal double AveragePresentMilliseconds => PresentFrames == 0
        ? 0
        : TerminalRenderTelemetry.TicksToMilliseconds(TotalPresentTicks) / PresentFrames;
}
