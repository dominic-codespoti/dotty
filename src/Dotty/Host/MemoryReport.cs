using System.Globalization;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;

namespace Dotty.Silk;

/// <summary>Everything the <c>MEMORY</c> control command reports; gathered by the host, formatted by <see cref="MemoryReport"/>.</summary>
internal readonly record struct MemoryReportInputs(
    long ManagedHeapEstimateBytes,
    long LastGcHeapSizeBytes,
    long LastGcCommittedBytes,
    long LastGcIndex,
    BufferMemorySnapshot Buffers,
    string ArenaBackend,
    long AtlasBitmapBytes,
    long AtlasCount,
    long R8TexturePayloadBytesEstimate,
    long Rgba8TexturePayloadBytesEstimate,
    long IntrinsicColorProbeScratchBytes,
    GraphicsMemorySnapshot Graphics,
    long ImageTextureBytesEstimate);

/// <summary>
/// JSON body of the <c>MEMORY</c> control command. Every value is an ownership counter or an estimate, never a
/// resident-memory (RSS) or live-object measurement: arena fields are reserved capacities, the image fields count
/// live <c>ImagePixels</c> buffers, queued transfer bytes and placements, and <c>imageTextureBytesEstimate</c> is
/// the renderer's estimate of uploaded image-texture bytes (mip chains included).
/// </summary>
internal static class MemoryReport
{
    internal static string ToJson(in MemoryReportInputs m)
    {
        BufferMemorySnapshot buffers = m.Buffers;
        GraphicsMemorySnapshot graphics = m.Graphics;
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"managedHeapEstimateBytes\":{m.ManagedHeapEstimateBytes},\"lastGcHeapSizeBytes\":{m.LastGcHeapSizeBytes},\"lastGcCommittedBytes\":{m.LastGcCommittedBytes},\"lastGcIndex\":{m.LastGcIndex},\"terminalArenaCapacityBytes\":{buffers.LiveArenaCapacityBytes},\"terminalArenaCommittedBoundBytes\":{buffers.LiveArenaCommittedBoundBytes},\"terminalArenaBackend\":\"{m.ArenaBackend}\",\"ownedSnapshotArrayCapacityBytes\":{buffers.OwnedSnapshotArrayCapacityBytes},\"arenaCapacityAllocatedBytes\":{buffers.ArenaCapacityAllocatedBytes},\"snapshotCapacityAllocatedBytes\":{buffers.SnapshotCapacityAllocatedBytes},\"atlasBitmapBytes\":{m.AtlasBitmapBytes},\"atlasCount\":{m.AtlasCount},\"r8TexturePayloadBytesEstimate\":{m.R8TexturePayloadBytesEstimate},\"rgba8TexturePayloadBytesEstimate\":{m.Rgba8TexturePayloadBytesEstimate},\"intrinsicColorProbeScratchBytes\":{m.IntrinsicColorProbeScratchBytes},\"imagePixelBytes\":{graphics.LiveImagePixelBytes},\"imageCount\":{graphics.LiveImageCount},\"placementCount\":{graphics.LivePlacementCount},\"pendingTransferBytes\":{graphics.PendingTransferBytes},\"imageTextureBytesEstimate\":{m.ImageTextureBytesEstimate}}}");
    }
}
