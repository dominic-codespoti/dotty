using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Builds and decodes tiny 8-bit RGBA/RGB PNGs (filter type 0 only) so no native decoder is needed.</summary>
internal static class TestPng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> be = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length);
        s.Write(be);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, Crc(t, data));
        s.Write(be);
    }

    private static byte[] Ihdr(int w, int h, bool alpha)
    {
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8;
        ihdr[9] = (byte)(alpha ? 6 : 2);
        return ihdr;
    }

    /// <summary>Signature + IHDR only (33 bytes): enough for the header peek, not decodable.</summary>
    public static byte[] HeaderOnly(int w, int h, bool alpha = true)
    {
        var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(w, h, alpha));
        return ms.ToArray();
    }

    /// <param name="pixels">Tightly packed rows, RGBA (<paramref name="alpha"/>) or RGB.</param>
    public static byte[] Build(int w, int h, ReadOnlySpan<byte> pixels, bool alpha = true)
    {
        int bpp = alpha ? 4 : 3;
        if (pixels.Length != w * h * bpp) throw new ArgumentException("pixel data length");
        var raw = new byte[h * (1 + w * bpp)];
        for (int y = 0; y < h; y++)
            pixels.Slice(y * w * bpp, w * bpp).CopyTo(raw.AsSpan(y * (1 + w * bpp) + 1));

        var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(w, h, alpha));
        WriteChunk(ms, "IDAT", Zlib(raw));
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    public static byte[] Zlib(ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Fastest)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Strict decoder for what <see cref="Build"/> writes; verifies CRCs.</summary>
    public static ImageDecodeStatus Decode(ReadOnlySpan<byte> png, int width, int height, Span<byte> rgba)
    {
        if (png.Length < 8 || !png.Slice(0, 8).SequenceEqual(Signature)) return ImageDecodeStatus.Invalid;
        int pos = 8;
        int iw = -1, ih = -1, color = -1, depth = -1;
        var idat = new MemoryStream();
        while (pos + 12 <= png.Length)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos));
            if (pos + 12L + len > png.Length) return ImageDecodeStatus.Invalid;
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, (int)len);
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos + 8 + (int)len));
            if (crc != Crc(type, data)) return ImageDecodeStatus.Invalid;
            string name = Encoding.ASCII.GetString(type);
            if (name == "IHDR")
            {
                if (len != 13) return ImageDecodeStatus.Invalid;
                iw = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
                ih = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
                depth = data[8];
                color = data[9];
            }
            else if (name == "IDAT")
            {
                idat.Write(data);
            }
            else if (name == "IEND")
            {
                break;
            }
            pos += 12 + (int)len;
        }
        if (iw != width || ih != height) return ImageDecodeStatus.Invalid;
        if (depth != 8 || (color != 6 && color != 2)) return ImageDecodeStatus.Unsupported;
        int bpp = color == 6 ? 4 : 3;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var outMs = new MemoryStream();
            z.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        catch (InvalidDataException)
        {
            return ImageDecodeStatus.Invalid;
        }
        if (raw.Length != height * (1 + width * bpp) || rgba.Length < width * height * 4) return ImageDecodeStatus.Invalid;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (1 + width * bpp);
            if (raw[rowStart] != 0) return ImageDecodeStatus.Unsupported;
            for (int x = 0; x < width; x++)
            {
                int s = rowStart + 1 + x * bpp;
                int d = (y * width + x) * 4;
                rgba[d] = raw[s];
                rgba[d + 1] = raw[s + 1];
                rgba[d + 2] = raw[s + 2];
                rgba[d + 3] = bpp == 4 ? raw[s + 3] : (byte)255;
            }
        }
        return ImageDecodeStatus.Ok;
    }
}

/// <summary>Deterministic <see cref="IImageDecoder"/> for <see cref="TestPng"/> files with test hooks.</summary>
internal sealed class FakeImageDecoder : IImageDecoder
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Runs inside the decode call (with the buffer lock released for large images) before decoding.</summary>
    public Action<int, int>? BeforeDecode { get; set; }

    public Exception? ThrowOnDecode { get; set; }

    /// <summary>When set, returned instead of decoding (after the hooks ran).</summary>
    public ImageDecodeStatus? Forced { get; set; }

    public ImageDecodeStatus DecodePngRgba(ReadOnlySpan<byte> png, int width, int height, Span<byte> straightRgba)
    {
        Interlocked.Increment(ref _calls);
        BeforeDecode?.Invoke(width, height);
        if (ThrowOnDecode != null) throw ThrowOnDecode;
        if (Forced is { } forced) return forced;
        return TestPng.Decode(png, width, height, straightRgba);
    }
}

internal sealed class FakeKittyHost : IKittyGraphicsHost
{
    private long _now = 1000;

    public object SyncRoot { get; } = new();
    public List<string> Replies { get; } = new();
    public int ReleaseAttempts { get; private set; }
    public int ReacquireCalls { get; private set; }
    public int GraphicsChangedCalls { get; private set; }
    public bool Disposed { get; set; }
    public bool ThrowOnReply { get; set; }
    public bool ThrowOnNow { get; set; }

    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public int Rows { get; set; } = 24;
    public int Columns { get; set; } = 80;
    public int CellPixelWidth { get; set; } = 8;
    public int CellPixelHeight { get; set; } = 16;

    /// <summary>Strictly increasing clock: every read is later than the previous one.</summary>
    public long NowTicks
    {
        get
        {
            if (ThrowOnNow) throw new InvalidOperationException("clock failure");
            return ++_now;
        }
    }

    public bool IsDisposed => Disposed;

    public void Reply(ReadOnlySpan<char> reply)
    {
        if (ThrowOnReply) throw new InvalidOperationException("reply sink failure");
        Replies.Add(reply.ToString());
    }

    public bool ReleaseSyncRootForBlockingWork()
    {
        ReleaseAttempts++;
        if (!Monitor.IsEntered(SyncRoot)) return false;
        Monitor.Exit(SyncRoot);
        return true;
    }

    public void ReacquireSyncRoot()
    {
        ReacquireCalls++;
        Monitor.Enter(SyncRoot);
    }

    public void GraphicsChanged() => GraphicsChangedCalls++;

    // ---- per-screen stores and the cursor move, as the terminal buffer provides them ----

    private KittyImageStore? _mainStore;
    private KittyImageStore? _altStore;
    private long _storageLimit = DottyDefaults.KittyImageStorageLimitBytes;
    private int _maxPlacements = DottyDefaults.KittyMaxPlacementsPerScreen;
    private int? _scrollBottom;

    /// <summary>Which screen's store <see cref="ActiveImageStore"/> resolves to.</summary>
    public bool AlternateActive { get; set; }

    /// <summary>Bottom margin row (0-based); the last row unless set.</summary>
    public int ScrollBottom
    {
        get => _scrollBottom ?? Rows - 1;
        set => _scrollBottom = value;
    }

    /// <summary>Every <see cref="MoveCursorAfterImage"/> call as (effCols, effRows).</summary>
    public List<(int Cols, int Rows)> CursorMoves { get; } = new();

    /// <summary>Rows the scroll region was asked to scroll by the cursor moves, in order.</summary>
    public List<int> RegionScrolls { get; } = new();

    public KittyImageStore MainStore => _mainStore ??= NewStore();
    public KittyImageStore AltStore => _altStore ??= NewStore();

    public KittyImageStore ActiveImageStore => AlternateActive ? AltStore : MainStore;

    private KittyImageStore NewStore() => new(_storageLimit) { MaxPlacements = _maxPlacements };

    public bool ApplyImageLimits(long storageLimitBytes, int maxPlacementsPerScreen)
    {
        _storageLimit = storageLimitBytes;
        _maxPlacements = maxPlacementsPerScreen;
        bool changed = false;
        foreach (var store in new[] { _mainStore, _altStore })
        {
            if (store == null) continue;
            int images = store.Count, placements = store.PlacementCount;
            store.MaxPlacements = maxPlacementsPerScreen;
            store.SetStorageLimit(storageLimitBytes);
            changed |= store.Count != images || store.PlacementCount != placements;
        }
        return changed;
    }

    public bool ClearImageStores()
    {
        bool had = _mainStore is { Count: > 0 } || _altStore is { Count: > 0 };
        _mainStore?.Clear();
        _altStore?.Clear();
        return had;
    }

    public void DisposeImageStores()
    {
        _mainStore?.Dispose();
        _altStore?.Dispose();
    }

    public void MoveCursorAfterImage(int effCols, int effRows)
    {
        CursorMoves.Add((effCols, effRows));
        var advance = KittyCursorPolicy.Advance(CursorRow, CursorCol, Columns, Rows, ScrollBottom, effCols, effRows);
        if (advance.ScrollRows > 0)
            RegionScrolls.Add(advance.ScrollRows);
        CursorRow = advance.Row;
        CursorCol = advance.Col;
    }
}

/// <summary>Restores-to-baseline check for the process-wide graphics counters (tests run non-parallel).</summary>
internal sealed class GraphicsBaseline
{
    private readonly GraphicsMemorySnapshot _start = GraphicsMemoryMetrics.GetSnapshot();

    public long LiveBytesDelta => GraphicsMemoryMetrics.GetSnapshot().LiveImagePixelBytes - _start.LiveImagePixelBytes;
    public long LiveCountDelta => GraphicsMemoryMetrics.GetSnapshot().LiveImageCount - _start.LiveImageCount;
    public long PendingDelta => GraphicsMemoryMetrics.GetSnapshot().PendingTransferBytes - _start.PendingTransferBytes;
    public long AllocatedDelta => GraphicsMemoryMetrics.GetSnapshot().ImagePixelBytesAllocated - _start.ImagePixelBytesAllocated;

    public void AssertRestored()
    {
        Assert.Equal(0, LiveBytesDelta);
        Assert.Equal(0, LiveCountDelta);
        Assert.Equal(0, PendingDelta);
    }
}

/// <summary>Controller + fake host + decoder, with helpers that speak the parser's callback protocol.</summary>
internal sealed class KittyRig : IDisposable
{
    public FakeKittyHost Host { get; } = new();
    public FakeImageDecoder Decoder { get; } = new();
    public KittyGraphicsController Controller { get; }

    public KittyRig(KittyGraphicsOptions? options = null, bool withDecoder = true)
    {
        Controller = new KittyGraphicsController(Host);
        Controller.Configure(options ?? new KittyGraphicsOptions { Enabled = true }, withDecoder ? Decoder : null);
    }

    public KittyImageStore Store => Controller.Store;

    public List<string> Replies => Host.Replies;

    /// <summary>One complete APC: control, optional payload slices, terminator.</summary>
    public void Apc(string control, params string[] payloadSlices)
    {
        Controller.OnBegin(Encoding.ASCII.GetBytes(control));
        foreach (string slice in payloadSlices)
            Controller.OnPayload(Encoding.ASCII.GetBytes(slice));
        Controller.OnEnd(true);
    }

    /// <summary>An APC cut off by CAN/SUB or another control: everything received so far is cancelled.</summary>
    public void CancelledApc(string control, params string[] payloadSlices)
    {
        Controller.OnBegin(Encoding.ASCII.GetBytes(control));
        foreach (string slice in payloadSlices)
            Controller.OnPayload(Encoding.ASCII.GetBytes(slice));
        Controller.OnEnd(false);
    }

    public void Dispose() => Controller.Dispose();
}

internal static class KittyTestData
{
    public const string Esc = "\u001b";
    public const string St = "\u001b\\";

    public static string B64(params byte[] bytes) => Convert.ToBase64String(bytes);

    public static string B64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>Exact reply bytes <c>ESC _ G &lt;ids&gt; ; OK ESC \</c>.</summary>
    public static string Ok(string ids) => Esc + "_G" + ids + ";OK" + St;

    public static string Err(string ids, string message) => Esc + "_G" + ids + ";" + message + St;

    public static byte[] ApcBytes(string control, string payload = "") =>
        Encoding.ASCII.GetBytes(Esc + "_G" + control + (payload.Length > 0 ? ";" + payload : "") + St);

    /// <summary><c>count</c> pseudo-random bytes from a fixed seed.</summary>
    public static byte[] RandomBytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }
}

/// <summary>Builds commands from control strings and places images straight into a store.</summary>
internal static class KittyCommands
{
    public static KittyGraphicsCommand Parse(string control)
    {
        var result = KittyControlParser.TryParse(Encoding.ASCII.GetBytes(control), out var cmd);
        Assert.Equal(KittyParseResult.Ok, result);
        return cmd;
    }

    /// <summary><see cref="KittyImageStore.TryPlace"/> with placement keys given as a control string; asserts success.</summary>
    public static KittyPlacement Place(this KittyImageStore store, KittyImage image, string keys = "",
        long anchorRow = 0, int anchorCol = 0, int cellW = 8, int cellH = 16)
    {
        var status = store.TryPlace(image, Parse(keys), anchorRow, anchorCol, cellW, cellH, out var placement);
        Assert.Equal(KittyStatus.Ok, status);
        Assert.NotNull(placement);
        return placement!;
    }

    /// <summary>Adds a <paramref name="width"/>x<paramref name="height"/> RGBA image (not retained by the test).</summary>
    public static KittyImage AddImage(this KittyImageStore store, int width, int height, uint clientId = 0, uint number = 0, long now = 0)
    {
        Assert.True(store.TryAdd(ImagePixels.Allocate(width, height, ImagePixelFormat.Rgba8Premultiplied), clientId, number, false, now, out var image));
        Assert.NotNull(image);
        return image!;
    }
}
