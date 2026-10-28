using System;
using System.Buffers;
using Dotty.Abstractions.Graphics;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// One in-flight image load: the (possibly chunked) payload of a transmit or query command, from the first chunk's
/// keys to the finished <see cref="ImagePixels"/>.
/// </summary>
/// <remarks>
/// <para>Everything that can be decided from the keys alone is decided in <see cref="TryStart"/>, before any pixel
/// memory exists, in this order: format (<c>f</c>), medium (<c>t</c>), compression (<c>o</c>), dimensions, decoded
/// size against the quota, payload cap. Declared sizes are computed in 64-bit arithmetic. A transfer that failed
/// has released its buffers and ignores further input; <see cref="Status"/> keeps the first error.</para>
/// <para>Payload handling per kind: uncompressed <c>f=24/32</c> decodes base64 straight into the destination
/// <see cref="ImagePixels"/> (allocated at the first payload byte from <c>s*v*bpp</c>); <c>o=z</c> and PNG
/// accumulate the decoded bytes in a pooled buffer capped at <see cref="KittyGraphicsOptions.MaxPayloadBytes"/>.
/// <see cref="Complete"/> inflates / decodes and premultiplies; when the work is large it runs with the buffer lock
/// released through the host (see <see cref="IKittyGraphicsHost.ReleaseSyncRootForBlockingWork"/>).</para>
/// <para>Threading: all members run on the PTY consumer thread except <see cref="Cancel"/>, which any thread may call
/// under the buffer lock; <see cref="Busy"/> tells the canceller not to free the buffers the consumer is using.</para>
/// </remarks>
internal sealed class KittyTransfer : IDisposable
{
    /// <summary>Decoded-size threshold from which inflate/decode/premultiply run without the buffer lock.</summary>
    public const long HeavyThresholdBytes = 256 * 1024;

    private const int InitialAccumulatorBytes = 16 * 1024;
    private const int PngHeaderBytes = 33;

    private readonly KittyGraphicsCommand _first;
    private readonly KittyGraphicsOptions _options;
    private readonly IImageDecoder? _decoder;
    private readonly KittyBase64Decoder _base64 = new();
    private readonly bool _isPng;
    private readonly bool _compressed;
    private readonly ImagePixelFormat _rawFormat;
    // Raw formats: destination bytes (s*v*bpp). Compressed PNG: inflated PNG size (S). Plain PNG: 0.
    private readonly long _expectedBytes;

    private ImagePixels? _dest;
    private int _destPos;
    private byte[]? _acc;
    private int _accLen;
    private bool _pngHeaderChecked;
    private PngInfo _pngInfo;
    private bool _more;
    private byte _quiet;
    private KittyStatus _status;
    private volatile bool _cancelled;
    private volatile bool _busy;

    private KittyTransfer(
        in KittyGraphicsCommand first, KittyGraphicsOptions options, IImageDecoder? decoder,
        bool isPng, bool compressed, ImagePixelFormat rawFormat, long expectedBytes)
    {
        _first = first;
        _options = options;
        _decoder = decoder;
        _isPng = isPng;
        _compressed = compressed;
        _rawFormat = rawFormat;
        _expectedBytes = expectedBytes;
        _more = first.More != 0;
        _quiet = first.Quiet;
    }

    /// <summary>The first chunk's command: ids for replies, hints, action.</summary>
    public KittyGraphicsCommand First => _first;

    /// <summary>Quiet level in force (the first chunk's <c>q</c>, overridden by any later chunk that carries <c>q</c>).</summary>
    public byte Quiet => _quiet;

    /// <summary>The chunk that is currently being received says more chunks follow (<c>m</c> non-zero).</summary>
    public bool MoreExpected => _more;

    /// <summary>True for <c>a=q</c>: the result is validated and decoded but never stored.</summary>
    public bool IsQuery => _first.Action == KittyAction.Query;

    /// <summary>The first error of the transfer, or <see cref="KittyStatus.Ok"/>.</summary>
    public KittyStatus Status => _status;

    public bool Failed => _status != KittyStatus.Ok;

    /// <summary>True while <see cref="Complete"/> runs without the buffer lock; the buffers must not be disposed then.</summary>
    public bool Busy => _busy;

    /// <summary>True when <see cref="Cancel"/> was called; the result of an unlocked <see cref="Complete"/> must be dropped.</summary>
    public bool Cancelled => _cancelled;

    /// <summary>Marks the transfer as cancelled without freeing anything (safe while <see cref="Busy"/>).</summary>
    public void Cancel() => _cancelled = true;

    /// <summary>
    /// The single seam for transmission media: which <c>t</c> values this build can read. Only direct payloads
    /// are implemented; file, temporary-file and shared-memory transmission answer
    /// <see cref="KittyStatus.UnsupportedMedium"/> until a medium reader exists.
    /// </summary>
    internal static KittyStatus MediumStatus(char medium, KittyGraphicsOptions options) =>
        medium == 'd' && (options.Media & KittyMediaFlags.Direct) != 0 ? KittyStatus.Ok : KittyStatus.UnsupportedMedium;

    /// <summary>
    /// Validates the first chunk's keys and creates the transfer. Returns null with the error in
    /// <paramref name="status"/> when the command can never succeed; nothing has been allocated then.
    /// </summary>
    public static KittyTransfer? TryStart(in KittyGraphicsCommand first, KittyGraphicsOptions options, IImageDecoder? decoder, out KittyStatus status)
    {
        ArgumentNullException.ThrowIfNull(options);
        status = KittyStatus.Ok;

        bool isPng = false;
        ImagePixelFormat rawFormat = ImagePixelFormat.Rgba8Premultiplied;
        int bytesPerPixel = 4;
        switch (first.Format)
        {
            case 24: rawFormat = ImagePixelFormat.Rgb8; bytesPerPixel = 3; break;
            case 32: break;
            case 100:
                isPng = true;
                break;
            default:
                status = KittyStatus.UnknownFormat;
                return null;
        }
        if (isPng && decoder == null)
        {
            status = KittyStatus.UnknownFormat; // no PNG decoder available: report the format as unsupported
            return null;
        }

        status = MediumStatus(first.Medium, options);
        if (status != KittyStatus.Ok)
            return null;

        bool compressed;
        switch (first.Compression)
        {
            case '\0': compressed = false; break;
            case 'z': compressed = true; break;
            default:
                status = KittyStatus.InvalidKeyValue;
                return null;
        }

        long expected = 0;
        if (isPng)
        {
            if (compressed)
            {
                // The inflated size of a compressed PNG is not derivable from the keys; S carries it.
                if (first.DataSize == 0)
                {
                    status = KittyStatus.InvalidKeyValue;
                    return null;
                }
                if (first.DataSize > options.MaxPayloadBytes)
                {
                    status = KittyStatus.PayloadTooLarge;
                    return null;
                }
                expected = first.DataSize;
            }
        }
        else
        {
            ulong w = first.Width, h = first.Height;
            if (w == 0 || h == 0)
            {
                status = KittyStatus.InvalidKeyValue;
                return null;
            }
            if (w > (ulong)options.MaxImageDimension || h > (ulong)options.MaxImageDimension)
            {
                status = KittyStatus.ImageTooLarge;
                return null;
            }
            ulong bytes = w * h * (ulong)bytesPerPixel; // <= 100000 * 100000 * 4: cannot overflow
            if (bytes > (ulong)options.StorageLimitBytes)
            {
                status = KittyStatus.StorageQuota;
                return null;
            }
            if (bytes > int.MaxValue)
            {
                status = KittyStatus.ImageTooLarge;
                return null;
            }
            if (!compressed && bytes > (ulong)options.MaxPayloadBytes)
            {
                status = KittyStatus.PayloadTooLarge;
                return null;
            }
            expected = (long)bytes;
        }

        return new KittyTransfer(in first, options, decoder, isPng, compressed, rawFormat, expected);
    }

    /// <summary>Starts a continuation chunk (only <c>m</c> and <c>q</c> keys): updates "more" and the quiet override.</summary>
    public void BeginChunk(in KittyGraphicsCommand chunk)
    {
        _more = chunk.More != 0;
        if (chunk.Has('q'))
            _quiet = chunk.Quiet;
    }

    /// <summary>Feeds base64 payload bytes of the current chunk (any slicing). Errors latch into <see cref="Status"/>.</summary>
    public void AddPayload(ReadOnlySpan<byte> base64)
    {
        if (_status != KittyStatus.Ok || base64.IsEmpty)
            return;
        bool final = !_more;

        if (!_isPng && !_compressed)
        {
            _dest ??= ImagePixels.Allocate((int)_first.Width, (int)_first.Height, _rawFormat);
            var window = _dest.AsWritableSpan().Slice(_destPos);
            var st = _base64.Decode(base64, window, out int written, final);
            if (st != KittyBase64Status.Ok)
            {
                Fail(MapBase64(st));
                return;
            }
            _destPos += written;
            return;
        }

        // Every complete quartet (a padded one included) is counted as 3 bytes, which over-counts a padded quartet by
        // at most 2: beyond the cap + 2 the real size is certainly beyond the cap.
        long need = _accLen + ((long)_base64.PendingChars + base64.Length) / 4 * 3;
        if (need > _options.MaxPayloadBytes + 2)
        {
            Fail(KittyStatus.PayloadTooLarge);
            return;
        }
        EnsureAccumulator((int)Math.Max(need, 1));
        var acc = _acc!;
        var accStatus = _base64.Decode(base64, acc.AsSpan(_accLen), out int accWritten, final);
        if (accStatus != KittyBase64Status.Ok)
        {
            Fail(MapBase64(accStatus));
            return;
        }
        _accLen += accWritten;
        if (_accLen > _options.MaxPayloadBytes)
        {
            Fail(KittyStatus.PayloadTooLarge);
            return;
        }

        // Reject an impossible PNG as soon as its header is in, instead of buffering the rest of a doomed payload.
        if (_isPng && !_compressed && !_pngHeaderChecked && _accLen >= PngHeaderBytes)
        {
            _pngHeaderChecked = true;
            var headerStatus = CheckPngHeader(acc.AsSpan(0, _accLen), out _);
            if (headerStatus != KittyStatus.Ok)
                Fail(headerStatus);
        }
    }

    /// <summary>
    /// Finishes the final chunk: flushes the base64 tail, inflates / decodes, premultiplies. On success the caller
    /// owns <paramref name="pixels"/> (format <see cref="ImagePixelFormat.Rgb8"/> for <c>f=24</c>, otherwise
    /// premultiplied RGBA). Large work runs with the buffer lock released through <paramref name="host"/>; the caller
    /// must re-validate live state (<see cref="Cancelled"/>, host disposed, quota) afterwards. Returns the status of
    /// the transfer; <paramref name="pixels"/> is null unless it is <see cref="KittyStatus.Ok"/>.
    /// </summary>
    public KittyStatus Complete(IKittyGraphicsHost host, out ImagePixels? pixels)
    {
        pixels = null;
        if (_status != KittyStatus.Ok)
            return _status;

        KittyStatus st = CompleteCore(host, out pixels);
        if (st != KittyStatus.Ok)
        {
            pixels?.Dispose();
            pixels = null;
            Fail(st);
        }
        return st;
    }

    private KittyStatus CompleteCore(IKittyGraphicsHost host, out ImagePixels? pixels)
    {
        pixels = null;
        bool raw = !_isPng;

        // 1. Flush the base64 carry.
        if (raw && !_compressed)
        {
            if (_dest == null)
                return KittyStatus.InsufficientData;
            var window = _dest.AsWritableSpan().Slice(_destPos);
            var st = _base64.Finish(window, out int written);
            if (st != KittyBase64Status.Ok)
                return MapBase64(st);
            _destPos += written;
            if (_destPos < _expectedBytes)
                return KittyStatus.InsufficientData;
        }
        else
        {
            if (_acc != null)
            {
                EnsureAccumulator(_accLen + 3);
                var st = _base64.Finish(_acc.AsSpan(_accLen), out int written);
                if (st != KittyBase64Status.Ok)
                    return MapBase64(st);
                _accLen += written;
                if (_accLen > _options.MaxPayloadBytes)
                    return KittyStatus.PayloadTooLarge;
            }
        }

        // 2. Decide whether there is CPU work and whether it is heavy.
        bool hasWork;
        long workBytes;
        if (raw)
        {
            if (_compressed)
                _dest = ImagePixels.Allocate((int)_first.Width, (int)_first.Height, _rawFormat);
            workBytes = _expectedBytes;
            hasWork = _compressed || _rawFormat == ImagePixelFormat.Rgba8Premultiplied;
        }
        else
        {
            hasWork = true;
            if (_compressed)
            {
                workBytes = _expectedBytes;
            }
            else
            {
                var headerStatus = CheckPngHeader(_acc == null ? default : _acc.AsSpan(0, _accLen), out var info);
                if (headerStatus != KittyStatus.Ok)
                    return headerStatus;
                _pngInfo = info;
                workBytes = (long)info.Width * info.Height * 4;
            }
        }
        if (!hasWork)
        {
            pixels = _dest;
            _dest = null;
            return KittyStatus.Ok;
        }

        // 3. Run it, without the buffer lock when it is large.
        bool heavy = workBytes >= HeavyThresholdBytes || _accLen >= HeavyThresholdBytes;
        KittyStatus result;
        bool released = false;
        _busy = heavy;
        try
        {
            if (heavy)
                released = host.ReleaseSyncRootForBlockingWork();
            result = raw ? MaterializeRaw() : MaterializePng();
        }
        catch (Exception)
        {
            result = KittyStatus.DecodeFailed;
        }
        finally
        {
            if (released)
                host.ReacquireSyncRoot();
            _busy = false;
        }
        if (result != KittyStatus.Ok)
            return result;

        pixels = _dest;
        _dest = null;
        return KittyStatus.Ok;
    }

    private KittyStatus MaterializeRaw()
    {
        var dest = _dest!;
        if (_compressed)
        {
            var span = _acc == null ? default : new ReadOnlySpan<byte>(_acc, 0, _accLen);
            var res = BoundedInflate.Inflate(span, dest.AsWritableSpan(), out _);
            switch (res)
            {
                case BoundedInflateResult.Ok: break;
                case BoundedInflateResult.InvalidData: return KittyStatus.InflateFailed;
                default: return KittyStatus.SizeMismatch; // shorter or longer than s*v*bpp
            }
        }
        if (_rawFormat == ImagePixelFormat.Rgba8Premultiplied)
            PixelOps.PremultiplyInPlace(dest.AsWritableSpan());
        return KittyStatus.Ok;
    }

    private KittyStatus MaterializePng()
    {
        byte[]? inflated = null;
        try
        {
            ReadOnlySpan<byte> png;
            PngInfo info;
            if (_compressed)
            {
                int inflatedLength = (int)_expectedBytes;
                inflated = ArrayPool<byte>.Shared.Rent(inflatedLength);
                var span = _acc == null ? default : new ReadOnlySpan<byte>(_acc, 0, _accLen);
                var res = BoundedInflate.Inflate(span, inflated.AsSpan(0, inflatedLength), out _);
                switch (res)
                {
                    case BoundedInflateResult.Ok: break;
                    case BoundedInflateResult.InvalidData: return KittyStatus.InflateFailed;
                    default: return KittyStatus.SizeMismatch;
                }
                png = inflated.AsSpan(0, inflatedLength);
                var headerStatus = CheckPngHeader(png, out info);
                if (headerStatus != KittyStatus.Ok)
                    return headerStatus;
            }
            else
            {
                png = new ReadOnlySpan<byte>(_acc, 0, _accLen);
                info = _pngInfo; // validated by CompleteCore
            }

            _dest = ImagePixels.Allocate(info.Width, info.Height, ImagePixelFormat.Rgba8Premultiplied);
            var rgba = _dest.AsWritableSpan();
            var decoded = _decoder!.DecodePngRgba(png, info.Width, info.Height, rgba);
            switch (decoded)
            {
                case ImageDecodeStatus.Ok: break;
                case ImageDecodeStatus.Invalid: return KittyStatus.BadPng;
                default: return KittyStatus.DecodeFailed;
            }
            PixelOps.PremultiplyInPlace(rgba);
            return KittyStatus.Ok;
        }
        finally
        {
            if (inflated != null)
                ArrayPool<byte>.Shared.Return(inflated);
        }
    }

    // Signature/IHDR, dimension and size limits for a PNG whose first bytes are in "png".
    private KittyStatus CheckPngHeader(ReadOnlySpan<byte> png, out PngInfo info)
    {
        if (!PngHeader.TryRead(png, out info))
            return KittyStatus.BadPng;
        if (info.Width > _options.MaxImageDimension || info.Height > _options.MaxImageDimension)
            return KittyStatus.ImageTooLarge;
        long bytes = (long)info.Width * info.Height * 4;
        if (bytes > _options.StorageLimitBytes)
            return KittyStatus.StorageQuota;
        if (bytes > int.MaxValue)
            return KittyStatus.ImageTooLarge;
        return KittyStatus.Ok;
    }

    private static KittyStatus MapBase64(KittyBase64Status status) =>
        status == KittyBase64Status.Overflow ? KittyStatus.PayloadTooLarge : KittyStatus.InvalidBase64;

    private void EnsureAccumulator(int need)
    {
        int have = _acc?.Length ?? 0;
        if (have >= need)
            return;
        int target = (int)Math.Min(Math.Max((long)need, Math.Max(InitialAccumulatorBytes, (long)have * 2)), Array.MaxLength);
        var next = ArrayPool<byte>.Shared.Rent(target);
        if (_acc != null)
        {
            _acc.AsSpan(0, _accLen).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_acc);
        }
        GraphicsMemoryMetrics.ChangePendingTransferBytes(next.Length - have);
        _acc = next;
    }

    private void Fail(KittyStatus status)
    {
        if (_status == KittyStatus.Ok)
            _status = status;
        ReleaseBuffers();
    }

    private void ReleaseBuffers()
    {
        if (_acc != null)
        {
            GraphicsMemoryMetrics.ChangePendingTransferBytes(-_acc.Length);
            ArrayPool<byte>.Shared.Return(_acc);
            _acc = null;
            _accLen = 0;
        }
        if (_dest != null)
        {
            _dest.Dispose();
            _dest = null;
            _destPos = 0;
        }
    }

    /// <summary>Frees the accumulation buffer and any unfinished destination. Must not be called while <see cref="Busy"/>.</summary>
    public void Dispose() => ReleaseBuffers();
}
