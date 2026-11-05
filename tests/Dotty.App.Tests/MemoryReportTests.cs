using System.Linq;
using System.Text.Json;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.App.Tests;

public sealed class MemoryReportTests
{
    // Every number is distinct so a swapped or mislabelled field cannot pass.
    private static MemoryReportInputs Sample() => new(
        ManagedHeapEstimateBytes: 11,
        LastGcHeapSizeBytes: 12,
        LastGcCommittedBytes: 13,
        LastGcIndex: 14,
        Buffers: new BufferMemorySnapshot(
            LiveArenaCapacityBytes: 21,
            OwnedSnapshotArrayCapacityBytes: 22,
            ArenaCapacityAllocatedBytes: 23,
            SnapshotCapacityAllocatedBytes: 24,
            LiveArenaCommittedBoundBytes: 25),
        ArenaBackend: "mmap-madvise",
        AtlasBitmapBytes: 31,
        AtlasCount: 32,
        R8TexturePayloadBytesEstimate: 33,
        Rgba8TexturePayloadBytesEstimate: 34,
        IntrinsicColorProbeScratchBytes: 35,
        Graphics: new GraphicsMemorySnapshot(
            LiveImagePixelBytes: 41,
            LiveImageCount: 42,
            PendingTransferBytes: 43,
            LivePlacementCount: 44,
            ImagePixelBytesAllocated: 9_999),
        ImageTextureBytesEstimate: 45);

    [Fact]
    public void MemoryJson_IsValidJsonWithTheImageCounters()
    {
        using JsonDocument document = JsonDocument.Parse(MemoryReport.ToJson(Sample()));
        JsonElement root = document.RootElement;

        Assert.Equal(41, root.GetProperty("imagePixelBytes").GetInt64());
        Assert.Equal(42, root.GetProperty("imageCount").GetInt64());
        Assert.Equal(43, root.GetProperty("pendingTransferBytes").GetInt64());
        Assert.Equal(44, root.GetProperty("placementCount").GetInt64());
        Assert.Equal(45, root.GetProperty("imageTextureBytesEstimate").GetInt64());
    }

    [Fact]
    public void MemoryJson_KeepsTheExistingFieldsAndAddsExactlyTheFiveImageOnes()
    {
        using JsonDocument document = JsonDocument.Parse(MemoryReport.ToJson(Sample()));
        JsonElement root = document.RootElement;

        var expectedNumbers = new (string Name, long Value)[]
        {
            ("managedHeapEstimateBytes", 11),
            ("lastGcHeapSizeBytes", 12),
            ("lastGcCommittedBytes", 13),
            ("lastGcIndex", 14),
            ("terminalArenaCapacityBytes", 21),
            ("ownedSnapshotArrayCapacityBytes", 22),
            ("arenaCapacityAllocatedBytes", 23),
            ("snapshotCapacityAllocatedBytes", 24),
            ("terminalArenaCommittedBoundBytes", 25),
            ("atlasBitmapBytes", 31),
            ("atlasCount", 32),
            ("r8TexturePayloadBytesEstimate", 33),
            ("rgba8TexturePayloadBytesEstimate", 34),
            ("intrinsicColorProbeScratchBytes", 35),
        };
        foreach ((string name, long value) in expectedNumbers)
            Assert.Equal(value, root.GetProperty(name).GetInt64());

        Assert.Equal("mmap-madvise", root.GetProperty("terminalArenaBackend").GetString());

        // 14 numeric + 1 string existing fields, 5 new image fields; nothing else (e.g. no allocated-total leak).
        Assert.Equal(20, root.EnumerateObject().Count());
    }
}
