using System.Runtime.CompilerServices;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

[CollectionDefinition(nameof(BufferMemoryMetricsCollection), DisableParallelization = true)]
public sealed class BufferMemoryMetricsCollection { }

[Collection(nameof(BufferMemoryMetricsCollection))]
public sealed class BufferMemoryMetricsTests
{
    private static readonly long CellBytes = Unsafe.SizeOf<CellHot>() + Unsafe.SizeOf<ColdCell>();

    private static void ScrollRows(TerminalBuffer buffer, int count)
    {
        for (int i = 0; i < count; i++)
            buffer.ScrollUpLines(1);
    }

    [Fact]
    public void BufferArenaCapacityTracksExactInitialAndResizedPayloadThenFrees()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 8, columns: 40, scrollbackCapacity: 16);
        var allocated = BufferMemoryMetrics.GetSnapshot();

        // Capacity is the reserved virtual size of the arenas, not what is resident.
        Assert.Equal(24L * 40 * CellBytes, allocated.LiveArenaCapacityBytes - before.LiveArenaCapacityBytes);
        Assert.Equal(24L * 40 * CellBytes, allocated.ArenaCapacityAllocatedBytes - before.ArenaCapacityAllocatedBytes);
        // Only the visible window can be committed.
        Assert.Equal(8L * 40 * CellBytes, allocated.LiveArenaCommittedBoundBytes - before.LiveArenaCommittedBoundBytes);

        // The destination reserves 36 x 80 cells and the parked spare re-reserves the same size for the next
        // resize (so resizing stays allocation-free); both are virtual.
        buffer.Resize(20, 80);
        var grown = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(4800L * CellBytes, grown.LiveArenaCapacityBytes - allocated.LiveArenaCapacityBytes);
        Assert.Equal(5760L * CellBytes, grown.ArenaCapacityAllocatedBytes - allocated.ArenaCapacityAllocatedBytes);

        buffer.Dispose();
        var released = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(before.LiveArenaCapacityBytes, released.LiveArenaCapacityBytes);
        Assert.Equal(before.LiveArenaCommittedBoundBytes, released.LiveArenaCommittedBoundBytes);
    }

    [Fact]
    public void FreshBufferReservesTheRingButCommitsOnlyTheVisibleWindow()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);
        var after = BufferMemoryMetrics.GetSnapshot();

        Assert.Equal((5000L + 24) * 80 * CellBytes, after.LiveArenaCapacityBytes - before.LiveArenaCapacityBytes);
        Assert.Equal(24L * 80 * CellBytes, after.LiveArenaCommittedBoundBytes - before.LiveArenaCommittedBoundBytes);
    }

    [Fact]
    public void CommittedBoundGrowsWithTheHistoryThatWasScrolled()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);

        ScrollRows(buffer, 1000);

        var after = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(1000, buffer.ScrollbackCount);
        Assert.Equal((1000L + 24) * 80 * CellBytes, after.LiveArenaCommittedBoundBytes - before.LiveArenaCommittedBoundBytes);
        Assert.Equal((5000L + 24) * 80 * CellBytes, after.LiveArenaCapacityBytes - before.LiveArenaCapacityBytes);
    }

    [Fact]
    public void CommittedBoundNeverExceedsTheReservationOnceTheRingWraps()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 8, columns: 40, scrollbackCapacity: 16);

        ScrollRows(buffer, 200);

        var after = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(24L * 40 * CellBytes, after.LiveArenaCommittedBoundBytes - before.LiveArenaCommittedBoundBytes);
        Assert.Equal(24L * 40 * CellBytes, after.LiveArenaCapacityBytes - before.LiveArenaCapacityBytes);
    }

    [Fact]
    public void ResizeLeavesTheSpareScreenWithNoCommittedBytes()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);
        ScrollRows(buffer, 1000);

        buffer.Resize(24, 100);

        var after = BufferMemoryMetrics.GetSnapshot();
        // History is reflowed into the destination's ring tail; the old (now spare) ring is discarded, so only
        // the destination counts: 1000 history rows + the 24 visible rows at the new width.
        Assert.Equal(1000, buffer.ScrollbackCount);
        Assert.Equal((1000L + 24) * 100 * CellBytes, after.LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);
        // Destination and spare both keep their virtual reservation.
        Assert.Equal(2 * (5000L + 24) * 100 * CellBytes, after.LiveArenaCapacityBytes - baseline.LiveArenaCapacityBytes);
    }

    [Fact]
    public void EraseDisplayReturnsTheBoundToTheVisibleWindow()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);
        ScrollRows(buffer, 1000);

        buffer.EraseDisplay(2);

        Assert.Equal(24L * 80 * CellBytes, BufferMemoryMetrics.GetSnapshot().LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);
    }

    [Fact]
    public void ClearScrollbackReturnsTheBoundToTheVisibleWindow()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);
        ScrollRows(buffer, 1000);

        buffer.ClearScrollback();

        Assert.Equal(24L * 80 * CellBytes, BufferMemoryMetrics.GetSnapshot().LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);
        Assert.Equal(0, buffer.ScrollbackCount);
    }

    [Fact]
    public void FullResetReturnsTheBoundToTheVisibleWindow()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);
        ScrollRows(buffer, 1000);

        buffer.FullReset();

        Assert.Equal(24L * 80 * CellBytes, BufferMemoryMetrics.GetSnapshot().LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);
        Assert.Equal(0, buffer.ScrollbackCount);
    }

    [Fact]
    public void LeavingTheAlternateScreenReturnsItsBoundToItsVisibleWindow()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 5000);

        buffer.SetAlternateScreen(true);
        ScrollRows(buffer, 300);
        var inAlternate = BufferMemoryMetrics.GetSnapshot();
        // Main window (24 rows) + the alternate ring that was scrolled through (300 + 24 rows).
        Assert.Equal((24L + 300 + 24) * 80 * CellBytes, inAlternate.LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);

        buffer.SetAlternateScreen(false);

        // Main window + the alternate screen's (blank) window; the 300 scrolled rows went back to the OS.
        Assert.Equal((24L + 24) * 80 * CellBytes, BufferMemoryMetrics.GetSnapshot().LiveArenaCommittedBoundBytes - baseline.LiveArenaCommittedBoundBytes);
    }

    [Fact]
    public void DisposeReturnsBothCountersToTheBaseline()
    {
        var baseline = BufferMemoryMetrics.GetSnapshot();
        var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 500);
        ScrollRows(buffer, 100);
        buffer.SetAlternateScreen(true);
        ScrollRows(buffer, 50);
        buffer.Resize(30, 100);
        buffer.SetAlternateScreen(false);

        buffer.Dispose();

        var after = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(baseline.LiveArenaCapacityBytes, after.LiveArenaCapacityBytes);
        Assert.Equal(baseline.LiveArenaCommittedBoundBytes, after.LiveArenaCommittedBoundBytes);
    }

    [Fact]
    public void ArenaBackendIsReported()
    {
        Assert.False(string.IsNullOrEmpty(BufferMemoryMetrics.ArenaBackend));
    }

    [Fact]
    public void SnapshotCapacityIsRetainedByPoolThenReleasedAfterLateReturn()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        var buffer = new TerminalBuffer(rows: 8, columns: 40, scrollbackCapacity: 16);
        var snapshot = buffer.CaptureRenderSnapshotVisible();
        var captured = BufferMemoryMetrics.GetSnapshot();
        long snapshotArrayBytes = 8L * 40 * (Unsafe.SizeOf<CellHot>() + Unsafe.SizeOf<ColdCell>())
            + 8L * sizeof(int) // RowOffsets
            + 8L * sizeof(int) // RowMap
            + 8L * sizeof(ulong); // RowGenerations

        Assert.Equal(snapshotArrayBytes, captured.OwnedSnapshotArrayCapacityBytes - before.OwnedSnapshotArrayCapacityBytes);
        Assert.Equal(snapshotArrayBytes, captured.SnapshotCapacityAllocatedBytes - before.SnapshotCapacityAllocatedBytes);

        snapshot.Dispose();
        Assert.Equal(captured.OwnedSnapshotArrayCapacityBytes, BufferMemoryMetrics.GetSnapshot().OwnedSnapshotArrayCapacityBytes);

        buffer.WriteText("retained".AsSpan(), CellAttributes.Default);
        var outstanding = buffer.CaptureRenderSnapshotVisible();
        buffer.Dispose();
        Assert.Equal(snapshotArrayBytes, BufferMemoryMetrics.GetSnapshot().OwnedSnapshotArrayCapacityBytes - before.OwnedSnapshotArrayCapacityBytes);
        Assert.Equal((uint)'r', outstanding.Cells[0].Rune);

        outstanding.Dispose();
        var released = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(before.OwnedSnapshotArrayCapacityBytes, released.OwnedSnapshotArrayCapacityBytes);
        Assert.Equal(before.LiveArenaCapacityBytes, released.LiveArenaCapacityBytes);
        Assert.Equal(before.LiveArenaCommittedBoundBytes, released.LiveArenaCommittedBoundBytes);
    }
}
