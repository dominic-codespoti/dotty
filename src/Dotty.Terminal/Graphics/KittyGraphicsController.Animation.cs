using System;

namespace Dotty.Terminal.Graphics;

// Animation actions: a=f (frame load / edit, chunked like a transmit), a=a (control), a=c (compose). The target
// image is addressed by i (or the newest image with number I); replies carry the image's id, plus I as given.
internal sealed partial class KittyGraphicsController
{
    private static readonly ulong ActionKeyBit = 1UL << KittyGraphicsCommand.BitIndex('a');

    /// <summary>
    /// A later chunk of an open frame load may repeat <c>a=f</c> next to <c>m</c>/<c>q</c> (frame loads continue "via
    /// <c>a=f</c>"); it continues the transfer instead of starting a new command. Without an open frame load such a
    /// command is a new (and unaddressable) frame load.
    /// </summary>
    private bool IsFrameContinuation(in KittyGraphicsCommand cmd) =>
        _transfer is { IsFrame: true }
        && cmd.Action == KittyAction.Frame
        && (cmd.Present & ~(ChunkKeysMask | ActionKeyBit)) == 0;

    /// <summary>
    /// Runs the command held back until the APC terminator (<c>a=p a=d a=a a=c</c>): a cancelled APC never gets here.
    /// </summary>
    private void HandleDeferred(in KittyGraphicsCommand cmd)
    {
        switch (cmd.Action)
        {
            case KittyAction.Put:
                HandlePut(in cmd);
                break;
            case KittyAction.Animate:
                HandleAnimate(in cmd);
                break;
            case KittyAction.Compose:
                HandleCompose(in cmd);
                break;
            default:
                HandleDelete(in cmd);
                break;
        }
    }

    /// <summary>
    /// Resolves the image of a frame command. Both <c>i</c> and <c>I</c> answer
    /// <see cref="KittyStatus.BothIdAndNumber"/>; neither means nothing is addressable and nothing is answered; an
    /// unknown image answers <see cref="KittyStatus.ImageNotFound"/>. Returns null in all those cases.
    /// </summary>
    private KittyImage? ResolveFrameTarget(in KittyGraphicsCommand cmd)
    {
        if (cmd.HasBothIdAndNumber)
        {
            SetPending(KittyStatus.BothIdAndNumber, cmd.ImageId, cmd.ImageNumber, 0, cmd.Quiet);
            return null;
        }
        if (cmd.ImageId == 0 && cmd.ImageNumber == 0)
            return null;
        var store = Store;
        var image = cmd.ImageId != 0 ? store.FindByClientId(cmd.ImageId) : store.FindByNumber(cmd.ImageNumber);
        if (image == null)
            SetPending(KittyStatus.ImageNotFound, cmd.ImageId, cmd.ImageNumber, 0, cmd.Quiet);
        return image;
    }

    /// <summary>
    /// <c>a=f</c>: resolves the target image and the frame numbers (and the quota for the canvas) from the keys, then
    /// opens a transfer for the payload; the frame is built when the final chunk completes. Every payload rule of a
    /// transmit applies (format, medium, compression, sizes), and the image itself is never replaced.
    /// </summary>
    private void StartFrameLoad(in KittyGraphicsCommand cmd)
    {
        var image = ResolveFrameTarget(in cmd);
        if (image == null)
            return;
        var status = Store.CheckFrameTarget(image, in cmd);
        if (status != KittyStatus.Ok)
        {
            SetPending(status, cmd.ImageId, cmd.ImageNumber, 0, cmd.Quiet);
            return;
        }
        var transfer = KittyTransfer.TryStart(cmd, _options, _decoder, _reader, out status);
        if (transfer == null)
        {
            SetPending(status, cmd.ImageId, cmd.ImageNumber, 0, cmd.Quiet);
            return;
        }
        _transfer = transfer;
        _kind = CommandKind.Chunk;
    }

    /// <summary>
    /// Final step of a frame load. The image is looked up again (the lock may have been released while the payload
    /// was decoded); <paramref name="pixels"/> is always disposed here.
    /// </summary>
    private void CommitFrame(in KittyGraphicsCommand first, ImagePixels pixels, byte quiet)
    {
        try
        {
            var store = Store;
            var image = first.ImageId != 0 ? store.FindByClientId(first.ImageId) : store.FindByNumber(first.ImageNumber);
            if (image == null)
            {
                SetPending(KittyStatus.ImageNotFound, first.ImageId, first.ImageNumber, 0, quiet);
                return;
            }
            var status = store.LoadFrame(image, in first, pixels, _host.NowTicks);
            if (status == KittyStatus.Ok)
                _host.GraphicsChanged();
            SetPending(status, image.ClientId, first.ImageNumber, 0, quiet);
        }
        finally
        {
            pixels.Dispose();
        }
    }

    /// <summary><c>a=a</c>: animation control (state, loops, frame gap, current frame).</summary>
    private void HandleAnimate(in KittyGraphicsCommand cmd)
    {
        var image = ResolveFrameTarget(in cmd);
        if (image == null)
            return;
        var status = Store.Animate(image, in cmd);
        if (status == KittyStatus.Ok)
            _host.GraphicsChanged();
        SetPending(status, image.ClientId, cmd.ImageNumber, 0, cmd.Quiet);
    }

    /// <summary><c>a=c</c>: composes a rectangle of one frame onto another.</summary>
    private void HandleCompose(in KittyGraphicsCommand cmd)
    {
        var image = ResolveFrameTarget(in cmd);
        if (image == null)
            return;
        var status = Store.ComposeFrames(image, in cmd, _host.NowTicks);
        if (status == KittyStatus.Ok)
            _host.GraphicsChanged();
        SetPending(status, image.ClientId, cmd.ImageNumber, 0, cmd.Quiet);
    }
}
