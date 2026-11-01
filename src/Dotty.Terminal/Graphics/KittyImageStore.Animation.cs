using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;

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
    /// Advances the playing animations of images displayed in the viewport and returns the earliest absolute
    /// deadline, in <paramref name="nowTicks"/> units, at which one of them changes frame; 0 = none. At most one
    /// frame step per image per call. Images that are not displayed are not touched, so a hidden animation neither
    /// advances nor asks for a wake-up. Must run under the same lock hold as the capture it precedes. An image is
    /// displayed (same rules as <see cref="CaptureVisible"/>) when it has
    /// <list type="bullet">
    /// <item>a normal placement in the viewport;</item>
    /// <item>a relative placement whose resolved position (the root's anchor plus the summed offsets) is in the
    /// viewport; a chain rooted at a virtual placement counts as displayed when that virtual placement is;</item>
    /// <item>a virtual placement, which is displayed through placeholder cells that this overload cannot see: it
    /// counts as displayed whenever the screen is captured (conservative: a virtual placement scrolled out of view
    /// keeps its animation, and its wake-ups, running). Use the overload taking the captured cells to be exact.</item>
    /// </list>
    /// </summary>
    public long AdvanceAnimations(long nowTicks, int rows, int scrollOffset) =>
        AdvanceAnimations(nowTicks, rows, scrollOffset, default(NoPlaceholderCells), cellsKnown: false);

    /// <summary>
    /// As <see cref="AdvanceAnimations(long, int, int)"/>, with the cells of the screen being captured: a virtual
    /// placement (or a relative chain rooted at one) counts as displayed only when some placeholder cell in
    /// <paramref name="cells"/> names its image (<see cref="PlaceholderResolver.CollectImageIds"/>; the grid and the
    /// placement id are not checked, so this errs towards advancing). The cells are scanned at most once per call and
    /// only when an animated image has such a placement.
    /// </summary>
    public long AdvanceAnimations<TSource>(long nowTicks, int rows, int scrollOffset, TSource cells)
        where TSource : IPlaceholderSource =>
        AdvanceAnimations(nowTicks, rows, scrollOffset, cells, cellsKnown: true);

    private long AdvanceAnimations<TSource>(long nowTicks, int rows, int scrollOffset, TSource cells, bool cellsKnown)
        where TSource : IPlaceholderSource
    {
        if (_placements.Count == 0 || rows <= 0)
            return 0;

        long epoch = ++_advanceEpoch;
        long earliest = 0;
        bool placeholdersScanned = false;
        foreach (var p in _placements.Values)
        {
            var image = p.Image;
            if (image.AdvanceStamp == epoch || image.AnimationState == KittyAnimationState.Stopped || image.Frames.Count < 2)
                continue;

            bool displayed;
            switch (p.Kind)
            {
                case PlacementKind.Normal:
                    displayed = RowsOverlap(p.AnchorRow - _viewTop + scrollOffset, p.Geometry.EffRows, rows);
                    break;
                case PlacementKind.Virtual:
                    displayed = ShownThroughPlaceholders(p, cells, cellsKnown, ref placeholdersScanned);
                    break;
                default:
                    {
                        var root = FindRelativeRoot(p, out _, out long dy);
                        if (root == null)
                            continue;
                        displayed = root.Kind == PlacementKind.Virtual
                            ? ShownThroughPlaceholders(root, cells, cellsKnown, ref placeholdersScanned)
                            : RowsOverlap(root.AnchorRow - _viewTop + scrollOffset + dy, p.Geometry.EffRows, rows);
                        break;
                    }
            }
            if (!displayed)
                continue;

            image.AdvanceStamp = epoch;
            if (KittyAnimation.Advance(image, nowTicks, _ticksPerSecond)
                && (earliest == 0 || image.FrameDeadlineTicks < earliest))
                earliest = image.FrameDeadlineTicks;
        }
        return earliest;
    }

    private static bool RowsOverlap(long viewRow, int effRows, int rows) => viewRow + effRows > 0 && viewRow < rows;

    private readonly HashSet<uint> _placeholderImageIds = new();

    // A virtual placement is captured only for images with a client id (nothing else can name it); it is drawn only
    // where a placeholder cell names its image.
    private bool ShownThroughPlaceholders<TSource>(KittyPlacement virtualPlacement, TSource cells, bool cellsKnown,
        ref bool scanned)
        where TSource : IPlaceholderSource
    {
        uint id = virtualPlacement.Image.ClientId;
        if (id == 0)
            return false;
        if (!cellsKnown)
            return true;
        if (!scanned)
        {
            _placeholderImageIds.Clear();
            PlaceholderResolver.CollectImageIds(cells, _placeholderImageIds);
            scanned = true;
        }
        return _placeholderImageIds.Contains(id);
    }

    /// <summary>Stand-in cells for the overload that does not look at the screen.</summary>
    private readonly struct NoPlaceholderCells : IPlaceholderSource
    {
        public int Rows => 0;
        public int Columns => 0;
        public ReadOnlySpan<CellHot> GetRowCells(int row) => default;
        public ReadOnlySpan<ColdCell> GetRowColdCells(int row) => default;
        public ref readonly CellAttributes GetStyle(ushort styleId) => ref CellAttributes.Default;
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
    /// For a raw format (<c>s</c> and <c>v</c> declare the data size, whatever the medium) checks that the data
    /// rectangle at <c>(x, y)</c> lies inside the image, so a frame that cannot fit is refused by its first chunk
    /// instead of after its whole payload was buffered. A PNG's size is only known once its header has arrived, so
    /// <see cref="LoadFrame"/> checks the rectangle again at commit.
    /// </summary>
    public KittyStatus CheckFrameRect(KittyImage image, in KittyGraphicsCommand cmd)
    {
        if (cmd.Format == 100 || cmd.Width == 0 || cmd.Height == 0)
            return KittyStatus.Ok;
        var root = image.Frames[0].Canvas;
        return (long)cmd.SrcX + cmd.Width > root.Width || (long)cmd.SrcY + cmd.Height > root.Height
            ? KittyStatus.RectOutOfBounds
            : KittyStatus.Ok;
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
    /// restarts the loop counter. Stopping resets the loop counter too (spec: "stopping the animation resets the loop
    /// counter"), and so does starting from the stopped state, which an animation that ran into its loop limit is in;
    /// switching between loading and running keeps it. <c>r</c> with a non-zero <c>z</c> sets the gap of
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
            if (next == KittyAnimationState.Stopped || image.AnimationState == KittyAnimationState.Stopped)
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
