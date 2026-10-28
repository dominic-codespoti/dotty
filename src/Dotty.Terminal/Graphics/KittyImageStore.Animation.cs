using System;

namespace Dotty.Terminal.Graphics;

// Animation half of the store: frame load (a=f), composition (a=c), control (a=a), frame delete (d=f/F), the
// playback clock and the animation invariants. All frame canvases are premultiplied RGBA and count against the
// quota like any other pixels; see KittyAnimation for the model.
internal sealed partial class KittyImageStore
{
    private long _ticksPerSecond = TimeProvider.System.TimestampFrequency;
    private long _advanceEpoch;

    /// <summary>
    /// Clock frequency of the ticks passed to <see cref="AdvanceAnimations"/> and returned from it. Defaults to
    /// <see cref="TimeProvider.TimestampFrequency"/> of <see cref="TimeProvider.System"/>, the clock behind
    /// <c>IKittyGraphicsHost.NowTicks</c>; tests set a convenient one (1000 makes a tick a millisecond).
    /// </summary>
    public long TicksPerSecond
    {
        get => _ticksPerSecond;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _ticksPerSecond = value;
        }
    }

    // ---- clock ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances the playing animations of images that have a placement in the viewport (same visibility rule as
    /// <see cref="CaptureVisible"/>; normal placements only) and returns the earliest absolute deadline, in
    /// <paramref name="nowTicks"/> units, at which one of them changes frame; 0 = none. At most one frame step per
    /// image per call. Images without a visible placement are not touched, so a hidden animation neither advances
    /// nor asks for a wake-up. Must run under the same lock hold as the capture it precedes.
    /// </summary>
    public long AdvanceAnimations(long nowTicks, int rows, int scrollOffset)
    {
        if (_placements.Count == 0 || rows <= 0)
            return 0;

        long epoch = ++_advanceEpoch;
        long earliest = 0;
        foreach (var p in _placements.Values)
        {
            if (p.Kind != PlacementKind.Normal)
                continue;
            var image = p.Image;
            if (image.AdvanceStamp == epoch || image.AnimationState == KittyAnimationState.Stopped || image.Frames.Count < 2)
                continue;
            long viewRow = p.AnchorRow - _viewTop + scrollOffset;
            if (viewRow + p.Geometry.EffRows <= 0 || viewRow >= rows)
                continue;

            image.AdvanceStamp = epoch;
            if (KittyAnimation.Advance(image, nowTicks, _ticksPerSecond)
                && (earliest == 0 || image.FrameDeadlineTicks < earliest))
                earliest = image.FrameDeadlineTicks;
        }
        return earliest;
    }

    // ---- a=f -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Key-only checks for a frame load that can run before any payload exists: the frame numbers
    /// (<c>r</c>: 0 = new frame, else an existing one; <c>c</c>: 0 = none, else an existing one), the per-image frame
    /// cap and the quota for the canvas the command would store.
    /// </summary>
    public KittyStatus CheckFrameTarget(KittyImage image, in KittyGraphicsCommand cmd)
    {
        int count = image.Frames.Count;
        uint r = cmd.FrameNumber, c = cmd.OtherFrame;
        bool isNew = r == 0;
        if (r > count || c > count)
            return KittyStatus.FrameNotFound;
        if (isNew && count >= KittyAnimation.MaxFramesPerImage)
            return KittyStatus.StorageQuota;
        long delta = CanvasDelta(image, isNew ? -1 : (int)r - 1);
        return image.ByteLength + delta > _storageLimitBytes ? KittyStatus.StorageQuota : KittyStatus.Ok;
    }

    /// <summary>
    /// Executes a frame load with the decoded payload <paramref name="data"/> (not owned: the caller disposes it).
    /// New frame (<c>r</c> absent or 0) or edit of frame <c>r</c>; the canvas starts as a copy of frame <c>c</c> when
    /// given, else (edit) as the frame's own content, else (new) as a fill with the background colour <c>Y</c>
    /// (<c>0xRRGGBBAA</c>, default transparent black); then the data is drawn at <c>(x, y)</c>, alpha-blended or, with
    /// <c>X=1</c>, copied. Gap: <c>z</c> &gt; 0 ms, &lt; 0 gapless, 0 = default 40 ms for a new frame and "unchanged"
    /// for an edit. An edited frame is replaced by a new <see cref="ImagePixels"/> (new serial); nothing changes on error.
    /// </summary>
    public KittyStatus LoadFrame(KittyImage image, in KittyGraphicsCommand cmd, ImagePixels data, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsLive(image))
            return KittyStatus.ImageNotFound;
        var status = CheckFrameTarget(image, in cmd);
        if (status != KittyStatus.Ok)
            return status;

        int width = image.Frames[0].Canvas.Width, height = image.Frames[0].Canvas.Height;
        if ((long)cmd.SrcX + data.Width > width || (long)cmd.SrcY + data.Height > height)
            return KittyStatus.RectOutOfBounds;

        uint r = cmd.FrameNumber, c = cmd.OtherFrame;
        bool isNew = r == 0;
        ImagePixels? canvas = null;
        ImagePixels? converted = null;
        try
        {
            if (c != 0)
                canvas = KittyAnimation.CopyAsRgba(image.Frames[(int)c - 1].Canvas);
            else if (!isNew)
                canvas = KittyAnimation.CopyAsRgba(image.Frames[(int)r - 1].Canvas);
            else
                canvas = KittyAnimation.Filled(width, height, cmd.BgColor);

            var src = data;
            if (data.Format != ImagePixelFormat.Rgba8Premultiplied)
                src = converted = KittyAnimation.CopyAsRgba(data);
            bool ok = PixelOps.CompositeRect(
                canvas.AsWritableSpan(), canvas.Stride, width, height, (int)cmd.SrcX, (int)cmd.SrcY,
                src.AsSpan(), src.Stride, src.Width, src.Height, 0, 0, src.Width, src.Height,
                blend: cmd.Blend != 1);
            if (!ok)
            {
                canvas.Dispose();
                return KittyStatus.RectOutOfBounds;
            }
        }
        catch
        {
            canvas?.Dispose();
            throw;
        }
        finally
        {
            converted?.Dispose();
        }

        if (isNew)
        {
            image.Frames.Add(new KittyFrame(canvas!, KittyAnimation.GapFromKey(cmd.Gap)));
            _usedBytes += canvas!.ByteLength;
        }
        else
        {
            var frame = image.Frames[(int)r - 1];
            _usedBytes += canvas!.ByteLength - frame.Canvas.ByteLength;
            frame.Canvas.Dispose();
            frame.Canvas = canvas;
            if (cmd.Gap != 0)
                frame.GapMs = KittyAnimation.GapFromKey(cmd.Gap);
        }
        FinishFrameMutation(image, in cmd, nowTicks);
        return KittyStatus.Ok;
    }

    // ---- a=c -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Composes a rectangle of frame <c>r</c> onto frame <c>c</c>: source rectangle at <c>(X, Y)</c> of size
    /// <c>w</c> x <c>h</c> (0 = to the right/bottom edge), destination at <c>(x, y)</c>, alpha-blended or, with
    /// <c>C=1</c>, copied. Both frames must exist (<see cref="KittyStatus.FrameNotFound"/>), both rectangles must lie
    /// inside the image (<see cref="KittyStatus.RectOutOfBounds"/>), and on the same frame they must not overlap
    /// (<see cref="KittyStatus.FrameOverlap"/>). The destination is replaced by a new <see cref="ImagePixels"/>.
    /// </summary>
    public KittyStatus ComposeFrames(KittyImage image, in KittyGraphicsCommand cmd, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsLive(image))
            return KittyStatus.ImageNotFound;

        int count = image.Frames.Count;
        uint r = cmd.FrameNumber, c = cmd.OtherFrame;
        if (r == 0 || r > count || c == 0 || c > count)
            return KittyStatus.FrameNotFound;

        int width = image.Frames[0].Canvas.Width, height = image.Frames[0].Canvas.Height;
        long srcX = cmd.CellXOff, srcY = cmd.CellYOff, dstX = cmd.SrcX, dstY = cmd.SrcY;
        long w = cmd.SrcW != 0 ? cmd.SrcW : width - srcX;
        long h = cmd.SrcH != 0 ? cmd.SrcH : height - srcY;
        if (w <= 0 || h <= 0 || srcX + w > width || srcY + h > height || dstX + w > width || dstY + h > height)
            return KittyStatus.RectOutOfBounds;
        if (r == c && srcX < dstX + w && dstX < srcX + w && srcY < dstY + h && dstY < srcY + h)
            return KittyStatus.FrameOverlap;

        var target = image.Frames[(int)c - 1];
        if (image.ByteLength + CanvasDelta(image, (int)c - 1) > _storageLimitBytes)
            return KittyStatus.StorageQuota;

        ImagePixels? canvas = null;
        ImagePixels? converted = null;
        try
        {
            canvas = KittyAnimation.CopyAsRgba(target.Canvas);
            var source = image.Frames[(int)r - 1].Canvas;
            if (source.Format != ImagePixelFormat.Rgba8Premultiplied)
                source = converted = KittyAnimation.CopyAsRgba(source);
            bool ok = PixelOps.CompositeRect(
                canvas.AsWritableSpan(), canvas.Stride, width, height, (int)dstX, (int)dstY,
                source.AsSpan(), source.Stride, source.Width, source.Height, (int)srcX, (int)srcY,
                (int)w, (int)h, blend: cmd.CursorPolicy != 1);
            if (!ok)
            {
                canvas.Dispose();
                return KittyStatus.RectOutOfBounds;
            }
        }
        catch
        {
            canvas?.Dispose();
            throw;
        }
        finally
        {
            converted?.Dispose();
        }

        _usedBytes += canvas!.ByteLength - target.Canvas.ByteLength;
        target.Canvas.Dispose();
        target.Canvas = canvas;
        FinishFrameMutation(image, in cmd, nowTicks);
        return KittyStatus.Ok;
    }

    // ---- a=a -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Animation control. <c>s</c>: 1 stop, 2 loading (run to the last frame and wait), 3 run (0 = unchanged; other
    /// values are invalid). <c>v</c>: loop count, <c>max_loops = v - 1</c> so 1 is infinite (0 = unchanged); setting it
    /// restarts the loop counter, as does entering the running state. <c>r</c> with a non-zero <c>z</c> sets the gap of
    /// frame <c>r</c> (<c>z</c> &lt; 0 gapless). <c>c</c> selects the displayed frame (0 = unchanged). Everything is
    /// validated first: a failing command changes nothing. Changing the state or the displayed frame restarts the frame
    /// timer; changing a gap or the loop count only lifts a loading-wait (the armed deadline is kept).
    /// </summary>
    public KittyStatus Animate(KittyImage image, in KittyGraphicsCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsLive(image))
            return KittyStatus.ImageNotFound;

        int count = image.Frames.Count;
        uint state = cmd.AnimState, loops = cmd.Loops, r = cmd.FrameNumber, c = cmd.OtherFrame;
        if (state > (uint)KittyAnimationState.Running)
            return KittyStatus.InvalidKeyValue;
        if (r > count && cmd.Gap != 0)
            return KittyStatus.FrameNotFound;
        if (c > count)
            return KittyStatus.FrameNotFound;

        if (r != 0 && cmd.Gap != 0)
        {
            image.Frames[(int)r - 1].GapMs = cmd.Gap > 0 ? cmd.Gap : 0;
            image.AnimationWaiting = false;
        }
        if (loops != 0)
        {
            image.MaxLoops = loops - 1;
            image.LoopsDone = 0;
            image.AnimationWaiting = false;
        }
        if (c != 0)
            image.CurrentFrameIndex = (int)c - 1;
        if (state != 0)
        {
            var next = (KittyAnimationState)state;
            if (next == KittyAnimationState.Running && image.AnimationState != KittyAnimationState.Running)
                image.LoopsDone = 0;
            image.AnimationState = next;
        }
        if (c != 0 || state != 0)
            image.ResetAnimationClock();
        return KittyStatus.Ok;
    }

    // ---- d=f / d=F -----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>d=f</c>/<c>d=F</c>: deletes frame <c>r</c> (1-based) of the image given by <c>i</c> or <c>I</c>; without a
    /// frame number nothing is deleted. The frame's pixels are freed either way (the upper-case form has nothing more to free).
    /// Later frames shift down, so deleting frame 1 promotes frame 2 to root; each frame keeps its own gap. The
    /// displayed frame is kept when it survives, otherwise the frame that took its place (the new last one when it was
    /// last) is shown. Deleting the only frame frees the whole image and its placements: an image cannot have zero
    /// frames. A missing image or frame deletes nothing. Returns the number of frames removed (the image counts as one).
    /// </summary>
    private int DeleteFrames(in KittyGraphicsCommand cmd)
    {
        var image = cmd.ImageId != 0 ? FindByClientId(cmd.ImageId) : FindByNumber(cmd.ImageNumber);
        if (image == null)
            return 0;
        int count = image.Frames.Count;
        if (cmd.FrameNumber == 0 || cmd.FrameNumber > (uint)count)
            return 0;
        int index = (int)cmd.FrameNumber - 1;
        if (count == 1)
            return Delete(image) ? 1 : 0;

        var frame = image.Frames[index];
        image.Frames.RemoveAt(index);
        _usedBytes -= frame.Canvas.ByteLength;
        frame.Canvas.Dispose();
        if (image.CurrentFrameIndex > index)
            image.CurrentFrameIndex--;
        else if (image.CurrentFrameIndex == index)
            image.CurrentFrameIndex = Math.Min(index, count - 2);
        image.ResetAnimationClock();
        return 1;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private bool IsLive(KittyImage image) =>
        !image.IsDeleted && _byInternalId.TryGetValue(image.InternalId, out var live) && ReferenceEquals(live, image);

    // Bytes the image grows by when the frame at "replaceIndex" (-1: a new frame) becomes a full RGBA canvas.
    private static long CanvasDelta(KittyImage image, int replaceIndex)
    {
        var root = image.Frames[0].Canvas;
        long canvas = (long)root.Width * root.Height * 4;
        return replaceIndex < 0 ? canvas : canvas - image.Frames[replaceIndex].Canvas.ByteLength;
    }

    // Common tail of a successful load/compose: transient hint, access time, quota (a grown image may push others
    // out; the image itself is protected and was checked to fit). A loading-wait ends because there may be a frame to
    // show now; the armed deadline is kept, so a stream of edits cannot starve playback.
    private void FinishFrameMutation(KittyImage image, in KittyGraphicsCommand cmd, long nowTicks)
    {
        if ((cmd.Hints & 1) != 0)
            image.Transient = true;
        TouchAccess(image, nowTicks);
        image.AnimationWaiting = false;
        EnforceQuota(protect: image);
    }

    private void ValidateAnimation()
    {
        foreach (var image in _byInternalId.Values)
        {
            int count = image.Frames.Count;
            Require(image.CurrentFrameIndex >= 0 && image.CurrentFrameIndex < count, "current frame out of range");
            Require(count <= KittyAnimation.MaxFramesPerImage, "too many frames");
            Require(image.FrameDeadlineTicks >= 0, "negative frame deadline");
            var root = image.Frames[0].Canvas;
            for (int i = 0; i < count; i++)
            {
                var frame = image.Frames[i];
                Require(frame.GapMs >= 0, "negative frame gap");
                Require(frame.Canvas.Width == root.Width && frame.Canvas.Height == root.Height, "frame size differs from the root frame");
                Require(i == 0 || frame.Canvas.Format == ImagePixelFormat.Rgba8Premultiplied, "non-root frame is not RGBA");
            }
        }
    }
}
