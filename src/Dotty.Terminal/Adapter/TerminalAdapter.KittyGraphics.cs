using System;
using System.Threading;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Graphics;

namespace Dotty.Terminal.Adapter;

// Kitty graphics protocol (APC G ... ST): the adapter forwards the parser's callbacks to a
// KittyGraphicsController and exposes the environment the controller needs (IKittyGraphicsHost).
// The controller is created by the first ConfigureGraphics call that enables the protocol; until then
// KittyGraphicsEnabled is false and the parser discards graphics APCs without any reply.
public partial class TerminalAdapter : IKittyGraphicsHost
{
    private KittyGraphicsController? _kittyGraphics;

    /// <summary>The graphics controller; null until <see cref="ConfigureGraphics"/> enabled the protocol once.</summary>
    internal KittyGraphicsController? KittyGraphics => _kittyGraphics;

    /// <summary>
    /// Applies the Kitty graphics settings and the PNG decoder (<c>null</c>: PNG payloads, <c>f=100</c>, are
    /// answered as an unknown format). Takes the buffer lock. Lowering the storage limit evicts immediately;
    /// disabling the protocol deletes every image and aborts a transfer that is in flight. Safe to call at any time,
    /// including while a large image is being decoded on the PTY thread.
    /// </summary>
    public void ConfigureGraphics(KittyGraphicsOptions options, IImageDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_kittyGraphics is null && !options.Enabled)
            return;
        _buffer.WithSyncRoot(() =>
        {
            _kittyGraphics ??= new KittyGraphicsController(this);
            _kittyGraphics.Configure(options, decoder);
        });
    }

    bool ITerminalHandler.KittyGraphicsEnabled => _kittyGraphics is { Enabled: true };

    void ITerminalHandler.OnKittyGraphicsBegin(ReadOnlySpan<byte> control) => _kittyGraphics?.OnBegin(control);

    void ITerminalHandler.OnKittyGraphicsPayload(ReadOnlySpan<byte> base64) => _kittyGraphics?.OnPayload(base64);

    void ITerminalHandler.OnKittyGraphicsEnd(bool completed) => _kittyGraphics?.OnEnd(completed);

    /// <summary>An alternate-screen switch ends a chunked load that is still open.</summary>
    private void AbortKittyGraphicsTransferIfScreenChanged(bool wasAlternate)
    {
        if (wasAlternate != _buffer.IsAlternateScreenActive)
            _kittyGraphics?.AbortTransfer();
    }

    /// <summary>RIS: deletes every image and aborts the open transfer; the configured limits stay.</summary>
    private void ResetKittyGraphics() => _kittyGraphics?.ClearImages();

    private void DisposeKittyGraphics()
    {
        var graphics = _kittyGraphics;
        if (graphics is null)
            return;
        lock (_buffer.SyncRoot)
            graphics.Dispose();
    }

    int IKittyGraphicsHost.CursorRow => _buffer.CursorRow;

    int IKittyGraphicsHost.CursorCol => _buffer.CursorCol;

    int IKittyGraphicsHost.Rows => _buffer.Rows;

    int IKittyGraphicsHost.Columns => _buffer.Columns;

    int IKittyGraphicsHost.CellPixelWidth
    {
        get
        {
            UnpackPair(Volatile.Read(ref _cellPixelSize), out int width, out _);
            return width;
        }
    }

    int IKittyGraphicsHost.CellPixelHeight
    {
        get
        {
            UnpackPair(Volatile.Read(ref _cellPixelSize), out _, out int height);
            return height;
        }
    }

    void IKittyGraphicsHost.Reply(ReadOnlySpan<char> reply) => ReplyRequested?.Invoke(reply);

    bool IKittyGraphicsHost.ReleaseSyncRootForBlockingWork() => _buffer.ReleaseSyncRootForBlockingWork();

    void IKittyGraphicsHost.ReacquireSyncRoot() => _buffer.ReacquireSyncRoot();

    KittyImageStore IKittyGraphicsHost.ActiveImageStore => _buffer.ActiveImageStore;

    bool IKittyGraphicsHost.ApplyImageLimits(long storageLimitBytes, int maxPlacementsPerScreen) =>
        _buffer.ApplyImageLimits(storageLimitBytes, maxPlacementsPerScreen);

    bool IKittyGraphicsHost.ClearImageStores() => _buffer.ClearImageStores();

    void IKittyGraphicsHost.DisposeImageStores() => _buffer.DisposeImageStores();

    void IKittyGraphicsHost.MoveCursorAfterImage(int effCols, int effRows) =>
        _buffer.MoveCursorAfterImage(effCols, effRows);

    void IKittyGraphicsHost.GraphicsChanged()
    {
        _buffer.BumpGraphicsGeneration();
        RequestRender();
    }

    long IKittyGraphicsHost.NowTicks => _timeProvider.GetTimestamp();

    bool IKittyGraphicsHost.IsDisposed => _buffer.IsDisposed;
}
