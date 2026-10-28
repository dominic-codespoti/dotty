using System;

namespace Dotty.Terminal.Graphics;

/// <summary>Playback state of an image (<c>a=a,s=</c>): the protocol values 1, 2 and 3.</summary>
internal enum KittyAnimationState : byte
{
    /// <summary>Not playing (the initial state).</summary>
    Stopped = 1,

    /// <summary>Playing until the last frame, then waiting there for more frames to be loaded.</summary>
    Loading = 2,

    /// <summary>Playing, looping according to the loop limit.</summary>
    Running = 3,
}

/// <summary>
/// Animation model shared by the frame commands and the clock: constants, key-to-gap rules, canvas helpers and the
/// pure playback step.
/// </summary>
/// <remarks>
/// <para>Storage is eager: every loaded or composed frame is a full premultiplied-RGBA canvas of the image's size
/// (the root frame keeps its transmitted format until it is edited), so each displayed frame is its own
/// <see cref="ImagePixels"/> (own GPU texture key) and the protocol's base-frame/rectangle semantics are resolved at
/// load time. Long animations therefore reach the storage quota earlier than a delta-based store would.</para>
/// <para>Playback is pull-driven: <see cref="KittyImageStore.AdvanceAnimations"/> is called when a snapshot is
/// captured, advances at most one frame per image per call (no catch-up burst after a stall) and reports the next
/// deadline; there are no timers or threads. An image that is not captured (hidden tab, off-screen) never advances.</para>
/// </remarks>
internal static class KittyAnimation
{
    /// <summary>Gap of a new frame whose <c>z</c> is absent or 0, in milliseconds. The root frame has none until it is set.</summary>
    public const int DefaultGapMs = 40;

    /// <summary>
    /// Most frames one image may hold. The quota alone would admit tens of millions of 1x1 frames (each a managed
    /// object plus a native block), so the count is bounded too; exceeding it answers like the quota does.
    /// </summary>
    public const int MaxFramesPerImage = 4096;

    /// <summary>Gap stored for a <c>z</c> value: positive milliseconds, negative = gapless (0), 0 = <see cref="DefaultGapMs"/>.</summary>
    public static int GapFromKey(int z) => z > 0 ? z : z < 0 ? 0 : DefaultGapMs;

    /// <summary>Milliseconds to ticks, rounded down but at least one tick so a deadline is always in the future.</summary>
    public static long MsToTicks(int ms, long ticksPerSecond)
    {
        long ticks = (long)ms * ticksPerSecond / 1000;
        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>
    /// A new premultiplied-RGBA canvas holding a copy of <paramref name="src"/> (RGB8 gains alpha 255). The caller
    /// owns the result.
    /// </summary>
    public static ImagePixels CopyAsRgba(ImagePixels src)
    {
        var dst = ImagePixels.Allocate(src.Width, src.Height, ImagePixelFormat.Rgba8Premultiplied);
        try
        {
            var d = dst.AsWritableSpan();
            var s = src.AsSpan();
            int w = src.Width;
            for (int y = 0; y < src.Height; y++)
            {
                var srow = s.Slice(y * src.Stride, w * (int)src.Format);
                var drow = d.Slice(y * dst.Stride, w * 4);
                if (src.Format == ImagePixelFormat.Rgba8Premultiplied)
                {
                    srow.CopyTo(drow);
                    continue;
                }
                for (int x = 0; x < w; x++)
                {
                    drow[x * 4] = srow[x * 3];
                    drow[x * 4 + 1] = srow[x * 3 + 1];
                    drow[x * 4 + 2] = srow[x * 3 + 2];
                    drow[x * 4 + 3] = 255;
                }
            }
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new canvas filled with <paramref name="rgba"/> (<c>0xRRGGBBAA</c>, straight alpha) premultiplied; the
    /// default 0 is transparent black. The caller owns the result.
    /// </summary>
    public static ImagePixels Filled(int width, int height, uint rgba)
    {
        var dst = ImagePixels.Allocate(width, height, ImagePixelFormat.Rgba8Premultiplied);
        if (rgba == 0 || (rgba & 0xFF) == 0)
            return dst; // alpha 0 premultiplies to all zero, which Allocate already provides
        try
        {
            Span<byte> px = stackalloc byte[4];
            px[0] = (byte)(rgba >> 24);
            px[1] = (byte)(rgba >> 16);
            px[2] = (byte)(rgba >> 8);
            px[3] = (byte)rgba;
            PixelOps.PremultiplyInPlace(px);
            var d = dst.AsWritableSpan();
            for (int y = 0; y < height; y++)
            {
                var row = d.Slice(y * dst.Stride, width * 4);
                for (int x = 0; x < width; x++)
                    px.CopyTo(row.Slice(x * 4, 4));
            }
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Advances <paramref name="image"/> one step when its deadline passed and re-arms it. Returns true when the
    /// image has an armed deadline afterwards (<see cref="KittyImage.FrameDeadlineTicks"/>), false when it is
    /// stopped, waiting, or has fewer than two frames. A displayed frame that has no gap (the root frame until
    /// <c>a=a,r=1,z=</c> gives it one, or a gapless frame selected with <c>c=</c>) is not held: the protocol skips
    /// gapless frames instantly, so the first pass after the timer was (re)started steps past it at once.
    /// </summary>
    public static bool Advance(KittyImage image, long nowTicks, long ticksPerSecond)
    {
        if (image.AnimationState == KittyAnimationState.Stopped || image.AnimationWaiting)
            return false;
        int count = image.Frames.Count;
        if (count < 2)
            return false;

        if (image.FrameDeadlineTicks == 0)
        {
            int gap = image.Frames[image.CurrentFrameIndex].GapMs;
            if (gap > 0)
            {
                image.FrameDeadlineTicks = nowTicks + MsToTicks(gap, ticksPerSecond);
                return true;
            }
        }
        else if (nowTicks < image.FrameDeadlineTicks)
        {
            return true;
        }

        Step(image, count);
        if (image.AnimationState == KittyAnimationState.Stopped || image.AnimationWaiting)
        {
            image.FrameDeadlineTicks = 0;
            return false;
        }
        // Step only lands on frames that have a gap.
        image.FrameDeadlineTicks = nowTicks + MsToTicks(image.Frames[image.CurrentFrameIndex].GapMs, ticksPerSecond);
        return true;
    }

    /// <summary>
    /// Moves to the next displayable frame: gapless frames are skipped; running past the last frame is a wrap, which
    /// makes a loading animation wait there and counts a loop for a running one (stopping at the loop limit).
    /// </summary>
    private static void Step(KittyImage image, int count)
    {
        var frames = image.Frames;
        int next = image.CurrentFrameIndex + 1;
        while (next < count && frames[next].GapMs == 0)
            next++;
        if (next >= count)
        {
            if (image.AnimationState == KittyAnimationState.Loading)
            {
                image.AnimationWaiting = true;
                return;
            }
            if (image.LoopsDone < uint.MaxValue)
                image.LoopsDone++;
            if (image.MaxLoops != 0 && image.LoopsDone >= image.MaxLoops)
            {
                image.AnimationState = KittyAnimationState.Stopped;
                return;
            }
            next = 0;
            while (next < count && frames[next].GapMs == 0)
                next++;
            if (next >= count)
            {
                image.AnimationWaiting = true; // every frame is gapless: nothing is displayable
                return;
            }
        }
        image.CurrentFrameIndex = next;
    }
}
