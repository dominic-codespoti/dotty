using System;
using System.Buffers;
using System.Runtime.InteropServices;
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
/// <para>Payload handling per kind: uncompressed <c>f=24/32</c> decodes base64 into a native staging block that
/// grows with the data actually received (geometrically, capped at <c>s*v*bpp</c>) and becomes the final
/// <see cref="ImagePixels"/> without a copy, so a header that promises hundreds of MiB followed by a few bytes
/// never reserves that memory; staged bytes count as pending transfer bytes. <c>o=z</c> and PNG accumulate the
/// decoded bytes in a pooled buffer capped at <see cref="KittyGraphicsOptions.MaxPayloadBytes"/>; their
/// destination is allocated only after the whole payload is in and only when its size is plausible for that much
/// compressed data (zlib cannot expand beyond about 1032:1). <see cref="Complete"/> inflates / decodes and
/// premultiplies; when the work is large it runs with the buffer lock released through the host (see
/// <see cref="IKittyGraphicsHost.ReleaseSyncRootForBlockingWork"/>).</para>
/// <para>Threading: all members run on the PTY consumer thread except <see cref="Cancel"/>, which any thread may call
/// under the buffer lock; <see cref="Busy"/> tells the canceller not to free the buffers the consumer is using.</para>
/// </remarks>
internal sealed unsafe class KittyTransfer : IDisposable
{
    /// <summary>Decoded-size threshold from which inflate/decode/premultiply run without the buffer lock.</summary>
    public const long HeavyThresholdBytes = 256 * 1024;

    private const int InitialAccumulatorBytes = 16 * 1024;
    private const int InitialStagingBytes = 64 * 1024;
    private const int PngHeaderBytes = 33;

    // Deflate cannot expand by more than 258 bytes per 2 bits = 1032:1; the slack covers headers and tiny streams.
    private const long MaxInflateRatio = 1040;
    private const long InflateSlackBytes = 1024;

    // A PNG pixel costs at least one bit (1-bit grey) before deflate, so a file of N bytes cannot describe more than
    // about 8 * 1032 * N pixels.
    private const long MaxPngPixelsPerByte = 8300;
    private const long PngSlackBytes = 4096;

    private readonly KittyGraphicsCommand _first;
    private readonly KittyGraphicsOptions _options;
    private readonly IImageDecoder? _decoder;
    private readonly IKittyMediumReader? _reader;
    // t=f/t/s: the payload is a path/name, replaced by the medium's content at completion.
    private readonly bool _pathMode;
    private readonly KittyBase64Decoder _base64 = new();
    private readonly bool _isPng;
    private readonly bool _compressed;
    private readonly ImagePixelFormat _rawFormat;
    // Raw formats: destination bytes (s*v*bpp). Compressed PNG: inflated PNG size (S). Plain PNG: 0.
    private readonly long _expectedBytes;

    private ImagePixels? _dest;
    private int _destPos;
    // Uncompressed raw formats: native staging block written by base64 decoding; _rawCap <= _expectedBytes.
    private byte* _raw;
    private int _rawCap;
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
        in KittyGraphicsCommand first, KittyGraphicsOptions options, IImageDecoder? decoder, IKittyMediumReader? reader,
        bool pathMode, bool isPng, bool compressed, ImagePixelFormat rawFormat, long expectedBytes)
    {
        _reader = reader;
        _pathMode = pathMode;
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

    /// <summary>True for <c>a=f</c>: the result becomes (part of) an animation frame of an existing image instead of a new image.</summary>
    public bool IsFrame => _first.Action == KittyAction.Frame;

    /// <summary>The first error of the transfer, or <see cref="KittyStatus.Ok"/>.</summary>
    public KittyStatus Status => _status;

    public bool Failed => _status != KittyStatus.Ok;

    /// <summary>True while <see cref="Complete"/> runs without the buffer lock; the buffers must not be disposed then.</summary>
    public bool Busy => _busy;

    /// <summary>True when <see cref="Cancel"/> was called; the result of an unlocked <see cref="Complete"/> must be dropped.</summary>
    public bool Cancelled => _cancelled;

    /// <summary>Marks the transfer as cancelled without freeing anything (safe while <see cref="Busy"/>).</summary>
    public void Cancel() => _cancelled = true;

    /// <summary>The longest file path / shared-memory name (decoded bytes) a t=f/t/s payload may carry.</summary>
    public const int MaxPathBytes = 2048;

    /// <summary>
    /// The single seam for transmission media: which <c>t</c> values the session's options allow. Direct payloads
    /// are always available; file, temporary-file and shared-memory transmission need their
    /// <see cref="KittyMediaFlags"/> (and a reader; platform support is the reader's call at completion).
    /// </summary>
    internal static KittyStatus MediumStatus(char medium, KittyGraphicsOptions options)
    {
        KittyMediaFlags needed = medium switch
        {
            'd' => KittyMediaFlags.Direct,
            'f' => KittyMediaFlags.File,
            't' => KittyMediaFlags.TempFile,
            's' => KittyMediaFlags.SharedMemory,
            _ => KittyMediaFlags.None,
        };
        return needed != KittyMediaFlags.None && (options.Media & needed) != 0 ? KittyStatus.Ok : KittyStatus.UnsupportedMedium;
    }

    /// <summary>
    /// Validates the first chunk's keys and creates the transfer. Returns null with the error in
    /// <paramref name="status"/> when the command can never succeed; nothing has been allocated then.
    /// <paramref name="reader"/> serves <c>t=f/t/s</c>; without one those media are unsupported.
    /// </summary>
    public static KittyTransfer? TryStart(in KittyGraphicsCommand first, KittyGraphicsOptions options, IImageDecoder? decoder, IKittyMediumReader? reader, out KittyStatus status)
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
        if (status == KittyStatus.Ok && first.Medium != 'd' && reader == null)
            status = KittyStatus.UnsupportedMedium;
        if (status != KittyStatus.Ok)
            return null;
        bool pathMode = first.Medium != 'd';

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
                // The inflated size of a compressed PNG is not derivable from the keys, so a direct payload carries it
                // in S. With a medium the protocol gives S a second meaning, the number of bytes to read from the file
                // or shared-memory object (the compressed length here), and one key cannot say both; the inflate bound
                // would have to be guessed. The combination is refused with EINVAL instead: nothing sends it (a client
                // that has a PNG on disk names the PNG file itself, f=100 without o=z), and refusing keeps the single
                // "size of what was read" rule for every medium.
                if (first.DataSize == 0 || pathMode)
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

        return new KittyTransfer(in first, options, decoder, reader, pathMode, isPng, compressed, rawFormat, expected);
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

        if (_pathMode)
        {
            AddPathPayload(base64, final);
            return;
        }

        if (!_isPng && !_compressed)
        {
            // Upper bound of the bytes this call can write: whole quartets, plus up to 2 bytes when padding that
            // arrives in its own slice completes a carried quartet. Never beyond the declared size, so a surplus is
            // reported by the decoder as an overflow.
            long bound = _destPos + ((long)_base64.PendingChars + base64.Length) / 4 * 3 + 3;
            EnsureStaging(Math.Min(bound, _expectedBytes));
            var st = _base64.Decode(base64, StagingWindow(), out int written, final);
            if (st != KittyBase64Status.Ok)
            {
                Fail(MapBase64(st));
                return;
            }
            _destPos += written;
            return;
        }

        // Every complete quartet (a padded one included) is counted as 3 bytes, which over-counts a padded quartet by
        // at most 2: beyond the cap + 2 the real size is certainly beyond the cap. The 3 spare bytes cover padding
        // that arrives in a slice of its own and completes a carried quartet.
        long need = _accLen + ((long)_base64.PendingChars + base64.Length) / 4 * 3;
        if (need > _options.MaxPayloadBytes + 2)
        {
            Fail(KittyStatus.PayloadTooLarge);
            return;
        }
        EnsureAccumulator((int)(need + 3));
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
    /// For t=f/t/s the payload is the path: it accumulates (base64-decoded) up to <see cref="MaxPathBytes"/>; a
    /// longer one can never name a file and fails like any other unreadable file.
    /// </summary>
    private void AddPathPayload(ReadOnlySpan<byte> base64, bool final)
    {
        long need = _accLen + ((long)_base64.PendingChars + base64.Length) / 4 * 3;
        if (need > MaxPathBytes + 2)
        {
            Fail(KittyStatus.BadFile);
            return;
        }
        EnsureAccumulator((int)(need + 3));
        var st = _base64.Decode(base64, _acc!.AsSpan(_accLen), out int written, final);
        if (st != KittyBase64Status.Ok)
        {
            Fail(MapBase64(st));
            return;
        }
        _accLen += written;
        if (_accLen > MaxPathBytes)
            Fail(KittyStatus.BadFile);
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

    /// <summary>
    /// The most bytes a medium read may return. An uncompressed raw image can use exactly <c>s*v*bpp</c> bytes, so
    /// the reader probes one byte beyond that and stops (the surplus is reported as "payload too large" either way)
    /// instead of pulling up to <see cref="KittyGraphicsOptions.MaxPayloadBytes"/> from the file for a small image.
    /// </summary>
    private long ReadCap() =>
        !_isPng && !_compressed
            ? Math.Max(1, Math.Min(_expectedBytes, _options.MaxPayloadBytes))
            : _options.MaxPayloadBytes;

    /// <summary>
    /// Medium transfers: flushes the base64 tail of the path, reads the medium with the buffer lock released (file
    /// I/O may block) and installs the content as if it had arrived as a direct payload: raw uncompressed formats
    /// get their destination (the content must be exactly <c>s*v*bpp</c> bytes), everything else is left in the
    /// accumulator for inflate / PNG decode. Every size rule of the direct path still applies.
    /// </summary>
    private KittyStatus ReadMedium(IKittyGraphicsHost host)
    {
        EnsureAccumulator(_accLen + 3);
        var tail = _base64.Finish(_acc!.AsSpan(_accLen), out int tailWritten);
        if (tail != KittyBase64Status.Ok)
            return MapBase64(tail);
        _accLen += tailWritten;
        if (_accLen == 0 || _accLen > MaxPathBytes)
            return KittyStatus.BadFile;

        Span<byte> path = stackalloc byte[MaxPathBytes];
        _acc!.AsSpan(0, _accLen).CopyTo(path);
        path = path.Slice(0, _accLen);
        ReleaseBuffers();

        KittyMediumReadResult result;
        bool released = false;
        _busy = true;
        try
        {
            released = host.ReleaseSyncRootForBlockingWork();
            result = _reader!.Read(_first.Medium, path, _first.DataOffset, _first.DataSize, ReadCap(), _options.Media);
        }
        catch (Exception)
        {
            result = KittyMediumReadResult.BadFile;
        }
        finally
        {
            if (released)
                host.ReacquireSyncRoot();
            _busy = false;
        }

        using (result)
        {
            // Cancelled or torn down while the lock was released: the caller drops the outcome silently.
            if (_cancelled || host.IsDisposed)
                return KittyStatus.DecodeFailed;
            if (!result.IsOk)
                return result.Status;
            var data = result.Data;
            if (result.ExceedsCap || data.Length > _options.MaxPayloadBytes)
                return KittyStatus.PayloadTooLarge;

            if (!_isPng && !_compressed)
            {
                if (data.Length < _expectedBytes)
                    return KittyStatus.InsufficientData;
                if (data.Length > _expectedBytes)
                    return KittyStatus.PayloadTooLarge;
                var dest = ImagePixels.Allocate((int)_first.Width, (int)_first.Height, _rawFormat);
                if (dest.ByteLength != _expectedBytes)
                {
                    dest.Dispose();
                    return KittyStatus.SizeMismatch;
                }
                data.CopyTo(dest.AsWritableSpan());
                _dest = dest;
                return KittyStatus.Ok;
            }

            EnsureAccumulator(data.Length + 3);
            data.CopyTo(_acc!.AsSpan());
            _accLen = data.Length;
            return KittyStatus.Ok;
        }
    }

    private KittyStatus CompleteCore(IKittyGraphicsHost host, out ImagePixels? pixels)
    {
        pixels = null;
        bool raw = !_isPng;

        // 1. Flush the base64 carry.
        if (_pathMode)
        {
            // t=f/t/s: the payload was the path; the medium's content replaces it (after the unlocked read).
            var mediumStatus = ReadMedium(host);
            if (mediumStatus != KittyStatus.Ok)
                return mediumStatus;
        }
        else if (raw && !_compressed)
        {
            if (_raw == null)
                return KittyStatus.InsufficientData;
            EnsureStaging(Math.Min((long)_destPos + 2, _expectedBytes));
            var st = _base64.Finish(StagingWindow(), out int written);
            if (st != KittyBase64Status.Ok)
                return MapBase64(st);
            _destPos += written;
            if (_destPos < _expectedBytes)
                return KittyStatus.InsufficientData;

            // The staging block is complete and exactly s*v*bpp bytes: it becomes the image without a copy.
            _dest = ImagePixels.Adopt(_raw, (int)_first.Width, (int)_first.Height, _rawFormat);
            GraphicsMemoryMetrics.ChangePendingTransferBytes(-_rawCap);
            _raw = null;
            _rawCap = 0;
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
            {
                if (!InflatePlausible(_expectedBytes))
                    return KittyStatus.SizeMismatch;
                _dest = ImagePixels.Allocate((int)_first.Width, (int)_first.Height, _rawFormat);
            }
            workBytes = _expectedBytes;
            hasWork = _compressed || _rawFormat == ImagePixelFormat.Rgba8Premultiplied;
        }
        else
        {
            hasWork = true;
            if (_compressed)
            {
                if (!InflatePlausible(_expectedBytes))
                    return KittyStatus.SizeMismatch;
                workBytes = _expectedBytes;
            }
            else
            {
                var headerStatus = CheckPngHeader(_acc == null ? default : _acc.AsSpan(0, _accLen), out var info);
                if (headerStatus != KittyStatus.Ok)
                    return headerStatus;
                if (!PngPlausible(info, _accLen))
                    return KittyStatus.BadPng;
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
                if (!PngPlausible(info, png.Length))
                    return KittyStatus.BadPng;
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

    /// <summary>
    /// True when <paramref name="expected"/> inflated bytes can come out of the compressed data received so far
    /// (deflate expands at most about 1032:1). Checked before the destination is allocated, so a tiny payload cannot
    /// reserve a large buffer by declaring large dimensions.
    /// </summary>
    private bool InflatePlausible(long expected) => expected <= _accLen * MaxInflateRatio + InflateSlackBytes;

    // A PNG file of this length cannot describe more pixels than this (see MaxPngPixelsPerByte).
    private static bool PngPlausible(in PngInfo info, long fileLength) =>
        (long)info.Width * info.Height <= (fileLength + PngSlackBytes) * MaxPngPixelsPerByte;

    // Grows the staging block to at least "need" bytes (geometric steps, never beyond the declared size).
    private void EnsureStaging(long need)
    {
        if (need <= _rawCap)
            return;
        long target = Math.Min(_expectedBytes, Math.Max(need, Math.Max(InitialStagingBytes, (long)_rawCap * 2)));
        _raw = (byte*)NativeMemory.Realloc(_raw, (nuint)target);
        GraphicsMemoryMetrics.ChangePendingTransferBytes(target - _rawCap);
        _rawCap = (int)target;
    }

    private Span<byte> StagingWindow() => new Span<byte>(_raw + _destPos, _rawCap - _destPos);

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
        if (_raw != null)
        {
            GraphicsMemoryMetrics.ChangePendingTransferBytes(-_rawCap);
            NativeMemory.Free(_raw);
            _raw = null;
            _rawCap = 0;
            _destPos = 0;
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
