using System.Threading;

namespace Dotty.Terminal.Graphics;

/// <summary>Point-in-time copy of the graphics ownership counters (not RSS).</summary>
public sealed record GraphicsMemorySnapshot(
    long LiveImagePixelBytes,
    long LiveImageCount,
    long PendingTransferBytes,
    long LivePlacementCount,
    long ImagePixelBytesAllocated);

/// <summary>
/// Process-wide ownership counters for graphics storage. <c>LiveImagePixelBytes</c>/<c>LiveImageCount</c> count live
/// <see cref="ImagePixels"/> buffers (a buffer is live until its last reference is released).
/// </summary>
public static class GraphicsMemoryMetrics
{
    private static long _liveImagePixelBytes;
    private static long _liveImageCount;
    private static long _pendingTransferBytes;
    private static long _livePlacementCount;
    private static long _imagePixelBytesAllocated;

    public static GraphicsMemorySnapshot GetSnapshot() => new(
        Interlocked.Read(ref _liveImagePixelBytes),
        Interlocked.Read(ref _liveImageCount),
        Interlocked.Read(ref _pendingTransferBytes),
        Interlocked.Read(ref _livePlacementCount),
        Interlocked.Read(ref _imagePixelBytesAllocated));

    /// <summary>Adjusts live pixel bytes/image count; positive byte deltas also accumulate into the allocated total.</summary>
    internal static void ChangeLiveImagePixels(long bytes, int images)
    {
        Interlocked.Add(ref _liveImagePixelBytes, bytes);
        Interlocked.Add(ref _liveImageCount, images);
        if (bytes > 0) Interlocked.Add(ref _imagePixelBytesAllocated, bytes);
    }

    internal static void ChangePendingTransferBytes(long bytes) => Interlocked.Add(ref _pendingTransferBytes, bytes);

    internal static void ChangeLivePlacements(int count) => Interlocked.Add(ref _livePlacementCount, count);
}
