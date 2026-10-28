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
        Frames = new List<KittyFrame>(1) { new KittyFrame(root, KittyAnimation.DefaultGapMs) };
    }

    /// <summary>Process-unique id assigned by the store; monotonic and never reused.</summary>
    public long InternalId { get; }

    /// <summary>Client-visible id (<c>i</c>); 0 for anonymous images.</summary>
    public uint ClientId { get; }

    /// <summary>Image number (<c>I</c>) the image was transmitted with; 0 when none.</summary>
    public uint Number { get; }

    /// <summary>Usage hint: the image is expected to be shown once and is evicted first.</summary>
    public bool Transient { get; internal set; }

    /// <summary>Store clock value of the last transmit or use; eviction removes the smallest first.</summary>
    public long LastAccessTicks { get; internal set; }

    /// <summary>
    /// Frames of the image; index 0 is the root frame (frame 1 of the protocol). Every frame counts against the
    /// storage quota.
    /// </summary>
    public List<KittyFrame> Frames { get; }

    /// <summary>Index into <see cref="Frames"/> of the frame that is displayed now.</summary>
    public int CurrentFrameIndex { get; internal set; }

    /// <summary>
    /// The displayed pixels: the current frame's canvas (the root frame until an animation moves on). Placement
    /// capture reads this, so it needs no animation knowledge; every frame has the image's dimensions.
    /// </summary>
    public ImagePixels Root => Frames[CurrentFrameIndex].Canvas;

    /// <summary>Playback state (<c>a=a,s=</c>); images start stopped.</summary>
    public KittyAnimationState AnimationState { get; internal set; } = KittyAnimationState.Stopped;

    /// <summary>Loop limit: 0 = loop forever, otherwise playback stops after this many complete passes (<c>v</c> minus one).</summary>
    public uint MaxLoops { get; internal set; }

    /// <summary>Complete passes finished since playback (re)started or the loop count was set.</summary>
    public uint LoopsDone { get; internal set; }

    /// <summary>Absolute clock ticks at which the displayed frame is due to change; 0 = not armed.</summary>
    internal long FrameDeadlineTicks { get; set; }

    /// <summary>
    /// Loading-wait state: playback reached the end while loading (or nothing is displayable) and waits for a
    /// structural change instead of arming a deadline.
    /// </summary>
    internal bool AnimationWaiting { get; set; }

    /// <summary>Visit marker of the current <c>AdvanceAnimations</c> pass (an image may have several placements).</summary>
    internal long AdvanceStamp { get; set; }

    /// <summary>Forgets the armed deadline and the loading-wait, so the next advance starts timing from "now".</summary>
    internal void ResetAnimationClock()
    {
        FrameDeadlineTicks = 0;
        AnimationWaiting = false;
    }

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
                total += Frames[i].Canvas.ByteLength;
            return total;
        }
    }

    internal void ReleasePixels()
    {
        IsDeleted = true;
        for (int i = 0; i < Frames.Count; i++)
            Frames[i].Canvas.Dispose();
        Frames.Clear();
    }
}
