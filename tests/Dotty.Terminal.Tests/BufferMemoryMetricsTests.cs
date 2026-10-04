using System.Runtime.CompilerServices;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

[CollectionDefinition(nameof(BufferMemoryMetricsCollection), DisableParallelization = true)]
public sealed class BufferMemoryMetricsCollection { }

[Collection(nameof(BufferMemoryMetricsCollection))]
public sealed class BufferMemoryMetricsTests
{
    [Fact]
    public void BufferArenaCapacityTracksExactInitialAndResizedPayloadThenFrees()
    {
        var before = BufferMemoryMetrics.GetSnapshot();
        using var buffer = new TerminalBuffer(rows: 8, columns: 40, scrollbackCapacity: 16);
        var allocated = BufferMemoryMetrics.GetSnapshot();
        long cellBytes = Unsafe.SizeOf<CellHot>() + Unsafe.SizeOf<ColdCell>();

        Assert.Equal(24L * 40 * cellBytes, allocated.LiveArenaCapacityBytes - before.LiveArenaCapacityBytes);
        Assert.Equal(24L * 40 * cellBytes, allocated.ArenaCapacityAllocatedBytes - before.ArenaCapacityAllocatedBytes);

        buffer.Resize(20, 80);
        var grown = BufferMemoryMetrics.GetSnapshot();
        Assert.Equal(4800L * cellBytes, grown.LiveArenaCapacityBytes - allocated.LiveArenaCapacityBytes);
        Assert.Equal(5760L * cellBytes, grown.ArenaCapacityAllocatedBytes - allocated.ArenaCapacityAllocatedBytes);

        buffer.Dispose();
        Assert.Equal(before.LiveArenaCapacityBytes, BufferMemoryMetrics.GetSnapshot().LiveArenaCapacityBytes);
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
    }
}
