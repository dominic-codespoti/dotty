using System.Threading;
using Dotty.App.Controls.Canvas.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalRenderTelemetryTests
{
    [Fact]
    public void DisabledRecording_DoesNotAllocateOrChangeCounters()
    {
        var telemetry = new TerminalRenderTelemetry();

        RecordDisabledSample(telemetry);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000; i++)
        {
            RecordDisabledSample(telemetry);
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = telemetry.Snapshot();

        Assert.Equal(allocatedBefore, allocatedAfter);
        Assert.False(snapshot.Enabled);
        Assert.Equal(0, snapshot.RenderNotifications);
        Assert.Equal(0, snapshot.RenderCalls);
        Assert.Equal(0, snapshot.ContentRenderAttempts);
        Assert.Equal(0, snapshot.BufferLockMisses);
        Assert.Equal(0, snapshot.BitmapRecreations);
    }

    [Fact]
    public void EnabledRecording_CapturesRenderContractAndResetPreservesState()
    {
        var telemetry = new TerminalRenderTelemetry();
        telemetry.Start();

        telemetry.RecordRenderNotification(coalesced: false);
        telemetry.RecordRenderNotification(coalesced: true);
        telemetry.RecordUiRenderUpdate();
        telemetry.RecordFrameRequest();

        var render = telemetry.BeginRender();
        Thread.SpinWait(10_000);
        telemetry.CompleteRender(render);

        long content = telemetry.BeginContentRender();
        Thread.SpinWait(10_000);
        telemetry.CompleteContentRender(content, completed: true);
        telemetry.RecordBufferLockMiss();
        telemetry.RecordBitmapRecreation();
        telemetry.RecordBufferState(42, 1.25, 1_000, 750);

        var snapshot = telemetry.Snapshot();

        Assert.True(snapshot.Enabled);
        Assert.Equal(2, snapshot.RenderNotifications);
        Assert.Equal(1, snapshot.CoalescedRenderNotifications);
        Assert.Equal(1, snapshot.UiRenderUpdates);
        Assert.Equal(1, snapshot.FrameRequests);
        Assert.Equal(1, snapshot.RenderCalls);
        Assert.Equal(1, snapshot.ContentRenderAttempts);
        Assert.Equal(1, snapshot.ContentFrames);
        Assert.Equal(1, snapshot.BufferLockMisses);
        Assert.Equal(1, snapshot.BitmapRecreations);
        Assert.Equal(42UL, snapshot.LastBufferGeneration);
        Assert.Equal(1.25, snapshot.LastRenderScale);
        Assert.Equal(1_000, snapshot.LastPixelWidth);
        Assert.Equal(750, snapshot.LastPixelHeight);
        Assert.True(snapshot.TotalRenderTicks > 0);
        Assert.True(snapshot.MaximumContentTicks > 0);
        Assert.Equal(1, snapshot.RenderDurationHistogram.Sum());

        telemetry.Reset();
        var reset = telemetry.Snapshot();

        Assert.True(reset.Enabled);
        Assert.Equal(0, reset.RenderCalls);
        Assert.Equal(0, reset.RenderDurationHistogram.Sum());

        telemetry.Stop();
        Assert.False(telemetry.Snapshot().Enabled);
    }

    [Fact]
    public void Aggregate_SumsMountedViewsAndKeepsWorstFrame()
    {
        var first = new TerminalRenderTelemetry();
        var second = new TerminalRenderTelemetry();
        first.Start();
        second.Start();

        first.RecordRenderNotification(coalesced: false);
        first.RecordFrameRequest();
        CompleteMeasuredRender(first);

        second.RecordRenderNotification(coalesced: true);
        second.RecordUiRenderUpdate();
        second.RecordBufferLockMiss();
        CompleteMeasuredRender(second);
        CompleteMeasuredRender(second);

        var aggregate = TerminalRenderTelemetry.Aggregate(
            new[] { first.Snapshot(), second.Snapshot() });

        Assert.True(aggregate.Enabled);
        Assert.Equal(2, aggregate.RenderNotifications);
        Assert.Equal(1, aggregate.CoalescedRenderNotifications);
        Assert.Equal(1, aggregate.UiRenderUpdates);
        Assert.Equal(1, aggregate.FrameRequests);
        Assert.Equal(3, aggregate.RenderCalls);
        Assert.Equal(1, aggregate.BufferLockMisses);
        Assert.Equal(3, aggregate.RenderDurationHistogram.Sum());
        Assert.True(aggregate.MaximumRenderTicks >= aggregate.MinimumRenderTicks);
        Assert.True(aggregate.P95RenderMilliseconds > 0);
    }

    private static void RecordDisabledSample(TerminalRenderTelemetry telemetry)
    {
        telemetry.RecordRenderNotification(coalesced: true);
        telemetry.RecordUiRenderUpdate();
        telemetry.RecordFrameRequest();
        var render = telemetry.BeginRender();
        telemetry.CompleteRender(render);
        long content = telemetry.BeginContentRender();
        telemetry.CompleteContentRender(content, completed: true);
        telemetry.RecordBufferLockMiss();
        telemetry.RecordBitmapRecreation();
        telemetry.RecordBufferState(1, 1, 1, 1);
    }

    private static void CompleteMeasuredRender(TerminalRenderTelemetry telemetry)
    {
        var measurement = telemetry.BeginRender();
        Thread.SpinWait(10_000);
        telemetry.CompleteRender(measurement);
    }
}
