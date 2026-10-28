using System.Collections.Generic;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// One stored kitty image. Owned by <see cref="KittyImageStore"/>; the store releases the pixel buffers when the
/// image is deleted (renderers that retained a buffer keep it alive until their own release).
/// </summary>
internal sealed class KittyImage
{
    internal KittyImage(long internalId, uint clientId, uint number, bool transient, ImagePixels root, long nowTicks)
    {
        InternalId = internalId;
        ClientId = clientId;
        Number = number;
        Transient = transient;
        LastAccessTicks = nowTicks;
        Frames = new List<ImagePixels>(1) { root };
    }

    /// <summary>Process-unique id assigned by the store; monotonic and never reused.</summary>
    public long InternalId { get; }

    /// <summary>Client-visible id (<c>i</c>); 0 for anonymous images.</summary>
    public uint ClientId { get; }

    /// <summary>Image number (<c>I</c>) the image was transmitted with; 0 when none.</summary>
    public uint Number { get; }

    /// <summary>Usage hint: the image is expected to be shown once and is evicted first.</summary>
    public bool Transient { get; }

    /// <summary>Store clock value of the last transmit or use; eviction removes the smallest first.</summary>
    public long LastAccessTicks { get; internal set; }

    /// <summary>
    /// Frames of the image; index 0 is the root frame. Animation support appends here; every frame counts against
    /// the storage quota.
    /// </summary>
    public List<ImagePixels> Frames { get; }

    public ImagePixels Root => Frames[0];

    /// <summary>
    /// Placements of this image, maintained by <see cref="KittyImageStore"/> only (null until the first one).
    /// </summary>
    internal List<KittyPlacement>? PlacementList { get; set; }

    /// <summary>
    /// Number of placements that reference the image. An image with none is "unreferenced" and is the first
    /// eviction candidate.
    /// </summary>
    public int PlacementCount => PlacementList?.Count ?? 0;

    /// <summary>True once the store deleted the image and released its pixels.</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>Total bytes of all frames (what the quota is charged).</summary>
    public long ByteLength
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Frames.Count; i++)
                total += Frames[i].ByteLength;
            return total;
        }
    }

    internal void ReleasePixels()
    {
        IsDeleted = true;
        for (int i = 0; i < Frames.Count; i++)
            Frames[i].Dispose();
        Frames.Clear();
    }
}
