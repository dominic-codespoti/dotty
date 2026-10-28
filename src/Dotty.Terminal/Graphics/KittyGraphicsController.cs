using System;
using Dotty.Abstractions.Graphics;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Executes kitty graphics commands for one terminal: receives the parser's APC callbacks, owns the single
/// in-flight <see cref="KittyTransfer"/> and the <see cref="KittyImageStore"/>, and produces the replies.
/// </summary>
/// <remarks>
/// <para>Command routing. A command whose only keys are <c>m</c> and/or <c>q</c> continues the open transfer (a
/// stray one with no open transfer is dropped silently, which is also how the rest of a failed load is swallowed).
/// Any other command first aborts an open transfer, then runs: <c>a=t</c>/<c>a=T</c> start a transfer,
/// <c>a=q</c> starts a query transfer (needs <c>i</c>, else it is ignored), the remaining actions are parsed and
/// ignored for now (<see cref="HandleOtherAction"/> is their seam).</para>
/// <para>Replies are produced when the APC terminator arrives (never for a cancelled APC), in parse order, so a
/// reply is queued before anything the application sends after the APC. An OK is sent only when image data was
/// actually loaded, never for an <c>m=1</c> chunk; errors carry the ids of the first chunk.</para>
/// <para>No exception leaves the controller: any failure frees the transfer and answers
/// <see cref="KittyStatus.InvalidKeyValue"/> only when an id is known.</para>
/// <para>Thread model: parser callbacks run on the PTY consumer thread under the buffer lock, except while a large
/// load runs with the lock released; <see cref="Configure"/>, <see cref="ClearImages"/> and <see cref="Dispose"/>
/// run under the lock on any thread and are safe against that window (<see cref="KittyTransfer.Busy"/>).</para>
/// </remarks>
internal sealed class KittyGraphicsController : IDisposable
{
    private static readonly ulong ChunkKeysMask =
        (1UL << KittyGraphicsCommand.BitIndex('m')) | (1UL << KittyGraphicsCommand.BitIndex('q'));

    private enum CommandKind : byte
    {
        /// <summary>No APC open, or its payload is to be discarded.</summary>
        Ignore,
        /// <summary>The open APC feeds <see cref="_transfer"/>.</summary>
        Chunk,
    }

    private struct PendingReply
    {
        public bool Has;
        public KittyStatus Status;
        public uint ImageId, ImageNumber, PlacementId;
        public byte Quiet;
    }

    private readonly IKittyGraphicsHost _host;
    private readonly KittyImageStore _store;
    private KittyGraphicsOptions _options;
    private IImageDecoder? _decoder;
    private KittyTransfer? _transfer;
    private CommandKind _kind;
    private PendingReply _pending;
    private uint _curImageId, _curImageNumber, _curPlacementId;
    private byte _curQuiet;
    private bool _disposed;

    public KittyGraphicsController(IKittyGraphicsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = new KittyGraphicsOptions().Normalize();
        _store = new KittyImageStore(_options.StorageLimitBytes);
    }

    /// <summary>True while the protocol is enabled; the parser discards graphics APCs otherwise.</summary>
    public bool Enabled => !_disposed && _options.Enabled;

    public KittyGraphicsOptions Options => _options;

    public KittyImageStore Store => _store;

    /// <summary>True while a multi-chunk load is waiting for more chunks (or is being finished).</summary>
    public bool HasOpenTransfer => _transfer != null;

    /// <summary>
    /// Applies new options and decoder. Disabling deletes every image and aborts the open transfer; a new storage
    /// limit evicts down to it immediately. Takes effect for transfers started later (an open one keeps the limits
    /// it started with, but its result is checked against the live quota at commit).
    /// </summary>
    public void Configure(KittyGraphicsOptions options, IImageDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options.Normalize();
        _options = normalized;
        _decoder = decoder;
        if (_disposed)
            return;
        if (!normalized.Enabled)
        {
            ClearImages();
            _store.SetStorageLimit(normalized.StorageLimitBytes);
            return;
        }
        long before = _store.UsedBytes;
        _store.SetStorageLimit(normalized.StorageLimitBytes);
        if (_store.UsedBytes != before)
            _host.GraphicsChanged();
    }

    /// <summary>Aborts the open transfer, if any, without a reply. Safe against an unlocked decode in progress.</summary>
    public void AbortTransfer()
    {
        var t = _transfer;
        if (t == null)
            return;
        if (t.Busy)
        {
            // The consumer thread is inside an unlocked step that uses the buffers; it frees them when it returns.
            t.Cancel();
            return;
        }
        _transfer = null;
        t.Dispose();
    }

    /// <summary>RIS: aborts the open transfer and deletes every image; the options (storage limit) are kept.</summary>
    public void ClearImages()
    {
        AbortTransfer();
        bool hadImages = _store.Count != 0;
        _store.Clear();
        if (hadImages)
            _host.GraphicsChanged();
    }

    /// <summary>Releases everything. Idempotent. Afterwards <see cref="Enabled"/> is false.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AbortTransfer();
        _store.Dispose();
    }

    // ---- APC callbacks ---------------------------------------------------------------------------------------

    public void OnBegin(ReadOnlySpan<byte> control)
    {
        _kind = CommandKind.Ignore;
        _pending = default;
        if (!Enabled)
            return;
        try
        {
            BeginCore(control);
        }
        catch (Exception)
        {
            FailCurrent();
        }
    }

    public void OnPayload(ReadOnlySpan<byte> base64)
    {
        if (_kind != CommandKind.Chunk)
            return;
        var t = _transfer;
        if (t == null)
            return;
        try
        {
            t.AddPayload(base64);
        }
        catch (Exception)
        {
            FailCurrent();
        }
    }

    public void OnEnd(bool completed)
    {
        var kind = _kind;
        _kind = CommandKind.Ignore;
        if (!completed)
        {
            // A cancelled chunk kills the whole load; nothing was committed and nothing is answered.
            if (kind == CommandKind.Chunk)
                AbortTransfer();
            _pending = default;
            return;
        }

        try
        {
            if (kind == CommandKind.Chunk)
                FinishChunk();
        }
        catch (Exception)
        {
            FailCurrent();
        }
        FlushPending();
    }

    // ---- command routing -------------------------------------------------------------------------------------

    private void BeginCore(ReadOnlySpan<byte> control)
    {
        var parse = KittyControlParser.TryParse(control, out var cmd);
        RememberIds(cmd.ImageId, cmd.ImageNumber, cmd.PlacementId, cmd.Quiet);
        bool chunkOnly = (cmd.Present & ~ChunkKeysMask) == 0;

        if (parse == KittyParseResult.Ok && chunkOnly)
        {
            var open = _transfer;
            if (open != null)
            {
                open.BeginChunk(cmd);
                var first = open.First;
                RememberIds(first.ImageId, first.ImageNumber, first.PlacementId, open.Quiet);
                _kind = CommandKind.Chunk;
            }
            // else: a stray continuation (the load failed or was aborted): drop silently.
            return;
        }

        AbortTransfer();
        if (parse == KittyParseResult.Invalid)
        {
            SetPending(KittyStatus.InvalidKeyValue, cmd.ImageId, cmd.ImageNumber, cmd.PlacementId, cmd.Quiet);
            return;
        }

        switch (cmd.Action)
        {
            case KittyAction.Transmit:
            case KittyAction.TransmitAndPut:
            case KittyAction.Query:
                StartTransfer(cmd);
                break;
            case KittyAction.Put:
            case KittyAction.Delete:
            case KittyAction.Frame:
            case KittyAction.Animate:
            case KittyAction.Compose:
                HandleOtherAction(cmd);
                break;
            default:
                SetPending(KittyStatus.UnknownAction, cmd.ImageId, cmd.ImageNumber, cmd.PlacementId, cmd.Quiet);
                break;
        }
    }

    /// <summary>Seam for the placement/delete/animation stage; these actions are parsed and ignored for now.</summary>
    private void HandleOtherAction(in KittyGraphicsCommand cmd)
    {
    }

    private void StartTransfer(in KittyGraphicsCommand cmd)
    {
        bool query = cmd.Action == KittyAction.Query;
        if (cmd.HasBothIdAndNumber)
        {
            SetPending(KittyStatus.BothIdAndNumber, cmd.ImageId, cmd.ImageNumber, cmd.PlacementId, cmd.Quiet);
            return;
        }
        if (query && cmd.ImageId == 0)
            return; // queries are only answerable with an id

        // Retransmitting an id replaces the image: the old one goes now, so a failed or interrupted transmit leaves
        // no stale image behind. A query never touches stored images.
        if (!query && cmd.ImageId != 0 && _store.DeleteByClientId(cmd.ImageId))
            _host.GraphicsChanged();

        var transfer = KittyTransfer.TryStart(cmd, _options, _decoder, out var status);
        if (transfer == null)
        {
            SetPending(status, cmd.ImageId, cmd.ImageNumber, cmd.PlacementId, cmd.Quiet);
            return;
        }
        _transfer = transfer;
        _kind = CommandKind.Chunk;
    }

    private void FinishChunk()
    {
        var t = _transfer;
        if (t == null)
            return; // aborted (configuration change, disabled) while the chunk was being received

        if (t.Failed)
        {
            _transfer = null;
            var failedFirst = t.First;
            SetPending(t.Status, failedFirst.ImageId, failedFirst.ImageNumber, failedFirst.PlacementId, t.Quiet);
            t.Dispose();
            return;
        }
        if (t.MoreExpected)
            return;

        var st = t.Complete(_host, out var pixels);

        // The lock may have been released during Complete: nothing read before it is trusted any more.
        bool dead = _disposed || _host.IsDisposed || t.Cancelled || !_options.Enabled || !ReferenceEquals(_transfer, t);
        if (ReferenceEquals(_transfer, t))
            _transfer = null;
        var first = t.First;
        byte quiet = t.Quiet;
        bool isQuery = t.IsQuery;
        t.Dispose();
        if (dead)
        {
            pixels?.Dispose();
            return;
        }

        if (st != KittyStatus.Ok)
        {
            SetPending(st, first.ImageId, first.ImageNumber, first.PlacementId, quiet);
            return;
        }
        if (isQuery)
        {
            pixels!.Dispose();
            SetPending(KittyStatus.Ok, first.ImageId, first.ImageNumber, first.PlacementId, quiet);
            return;
        }
        Commit(first, pixels!, quiet);
    }

    private void Commit(in KittyGraphicsCommand first, ImagePixels pixels, byte quiet)
    {
        // Live state: the quota may have shrunk and the id may have been taken while the lock was released.
        if (pixels.ByteLength > _store.StorageLimitBytes)
        {
            pixels.Dispose();
            SetPending(KittyStatus.StorageQuota, first.ImageId, first.ImageNumber, first.PlacementId, quiet);
            return;
        }

        // A plain transmit with neither id nor number can never be addressed again: there is nothing to keep, and
        // storing it would only evict images that can still be used.
        if (first.Action == KittyAction.Transmit && first.ImageId == 0 && first.ImageNumber == 0)
        {
            pixels.Dispose();
            return;
        }

        uint clientId = first.ImageId;
        KittyImage? image = null;
        bool added;
        try
        {
            if (clientId != 0)
                _store.DeleteByClientId(clientId);
            if (first.ImageNumber != 0)
                clientId = _store.AllocateClientId();
            // N is the usage hint; bit 0 marks the image transient.
            bool transient = (first.Hints & 1) != 0;
            added = (first.ImageNumber == 0 || clientId != 0)
                && _store.TryAdd(pixels, clientId, first.ImageNumber, transient, _host.NowTicks, out image);
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
        if (!added || image == null)
        {
            // Over the quota, or the whole I= id range is taken.
            pixels.Dispose();
            SetPending(KittyStatus.StorageQuota, first.ImageId, first.ImageNumber, first.PlacementId, quiet);
            return;
        }

        if (first.Action == KittyAction.TransmitAndPut)
        {
            var placed = PlaceAfterTransmit(first, image);
            if (placed != KittyStatus.Ok)
            {
                // The image stays stored (the transmit itself succeeded); the reply reports the failed placement.
                if (image.ClientId == 0 && image.PlacementCount == 0)
                    _store.Delete(image);
                SetPending(placed, clientId, first.ImageNumber, first.PlacementId, quiet);
                return;
            }
        }

        // An image nobody can address (no id) and nothing displays is garbage.
        if (image.ClientId == 0 && image.PlacementCount == 0)
            _store.Delete(image);

        SetPending(KittyStatus.Ok, clientId, first.ImageNumber, first.PlacementId, quiet);
    }

    /// <summary>
    /// Seam for the placement stage: <c>a=T</c> puts the freshly stored image using the transmit command's keys
    /// (<paramref name="cmd"/> is the first chunk's command, so <c>p c r x y w h X Y z C U P Q H V</c> are all
    /// there). Runs at the final-chunk commit, after the image is in the store and before the reply; a non-Ok
    /// result becomes the reply and the image is kept.
    /// </summary>
    private KittyStatus PlaceAfterTransmit(in KittyGraphicsCommand cmd, KittyImage image) => KittyStatus.Ok;

    // ---- replies and failure handling ------------------------------------------------------------------------

    private void RememberIds(uint imageId, uint imageNumber, uint placementId, byte quiet)
    {
        _curImageId = imageId;
        _curImageNumber = imageNumber;
        _curPlacementId = placementId;
        _curQuiet = quiet;
    }

    private void SetPending(KittyStatus status, uint imageId, uint imageNumber, uint placementId, byte quiet)
    {
        _pending = new PendingReply
        {
            Has = true,
            Status = status,
            ImageId = imageId,
            ImageNumber = imageNumber,
            PlacementId = placementId,
            Quiet = quiet,
        };
    }

    private void FlushPending()
    {
        var p = _pending;
        _pending = default;
        if (!p.Has)
            return;
        try
        {
            Span<char> buffer = stackalloc char[KittyResponse.MaxLength];
            int n = KittyResponse.Format(buffer, p.ImageId, p.ImageNumber, p.PlacementId, p.Status, p.Quiet);
            if (n > 0)
                _host.Reply(buffer.Slice(0, n));
        }
        catch (Exception)
        {
            // A failing reply sink must not break parsing.
        }
    }

    private void FailCurrent()
    {
        var t = _transfer;
        if (t != null && !t.Busy)
        {
            _transfer = null;
            t.Dispose();
        }
        _kind = CommandKind.Ignore;
        if (_curImageId != 0 || _curImageNumber != 0)
            SetPending(KittyStatus.InvalidKeyValue, _curImageId, _curImageNumber, _curPlacementId, _curQuiet);
        else
            _pending = default;
    }
}
