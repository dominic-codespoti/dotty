using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dotty.Abstractions.Graphics;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// Needs the native Skia library (SkiaSharp.NativeAssets.*); tests skip themselves when it cannot be loaded.
/// PNG fixtures are produced by a tiny managed writer (stored deflate blocks, CRC-32, Adler-32) so no binary
/// resources are checked in and every pixel value is known by construction.
/// </summary>
public sealed class SkiaImageDecoderTests
{
    private static readonly SkiaImageDecoder Decoder = new();

    private static void RequireSkia()
    {
        try
        {
            using var d = SKData.CreateCopy(new byte[] { 0 });
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            Assert.Skip("Native Skia library is not available: " + e.Message);
        }
    }

    // ---- managed PNG writer ---------------------------------------------------------------------------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void BigEndian(List<byte> o, uint v)
    {
        o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
    }

    private static void Chunk(List<byte> o, string type, ReadOnlySpan<byte> data)
    {
        BigEndian(o, (uint)data.Length);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body.AsSpan(4));
        o.AddRange(body);
        BigEndian(o, Crc32(body));
    }

    private static byte[] ZlibStored(ReadOnlySpan<byte> raw)
    {
        var o = new List<byte> { 0x78, 0x01 };
        int pos = 0;
        do
        {
            int n = Math.Min(65535, raw.Length - pos);
            bool last = pos + n >= raw.Length;
            o.Add((byte)(last ? 1 : 0));
            o.Add((byte)n); o.Add((byte)(n >> 8));
            o.Add((byte)~n); o.Add((byte)(~n >> 8));
            o.AddRange(raw.Slice(pos, n).ToArray());
            pos += n;
        } while (pos < raw.Length);
        BigEndian(o, Adler32(raw));
        return o.ToArray();
    }

    /// <param name="rows">Unfiltered scanline bytes, one array per row (the filter byte is added here).</param>
    private static byte[] Png(int w, int h, byte depth, byte colorType, byte[][] rows,
        byte[]? plte = null, byte[]? trns = null)
    {
        var o = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new List<byte>();
        BigEndian(ihdr, (uint)w); BigEndian(ihdr, (uint)h);
        ihdr.AddRange(new byte[] { depth, colorType, 0, 0, 0 });
        Chunk(o, "IHDR", ihdr.ToArray());
        if (plte != null) Chunk(o, "PLTE", plte);
        if (trns != null) Chunk(o, "tRNS", trns);
        var raw = new List<byte>();
        foreach (var row in rows) { raw.Add(0); raw.AddRange(row); }
        Chunk(o, "IDAT", ZlibStored(raw.ToArray()));
        Chunk(o, "IEND", ReadOnlySpan<byte>.Empty);
        return o.ToArray();
    }

    private static byte[] Rgba8Png(int w, int h, byte[] straightRgba)
    {
        var rows = new byte[h][];
        for (int y = 0; y < h; y++) rows[y] = straightRgba.AsSpan(y * w * 4, w * 4).ToArray();
        return Png(w, h, 8, 6, rows);
    }

    private static readonly byte[] Rgba2x2 =
    [
        255, 0, 0, 255,     0, 255, 0, 128,
        0, 0, 255, 0,       10, 20, 30, 200,
    ];

    // ---- tests ---------------------------------------------------------------------------------------------

    [Fact]
    public void DecodesRgba8ToStraightAlpha()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        var dst = new byte[16];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 2, 2, dst));
        // Fully transparent pixels may legitimately lose their colour in some decoders; everything else must be exact.
        Assert.Equal(Rgba2x2.AsSpan(0, 8).ToArray(), dst.AsSpan(0, 8).ToArray());
        Assert.Equal(0, dst[11]);
        Assert.Equal(Rgba2x2.AsSpan(12, 4).ToArray(), dst.AsSpan(12, 4).ToArray());
    }

    [Fact]
    public void DecodesOnePixelImage()
    {
        RequireSkia();
        var png = Rgba8Png(1, 1, [1, 2, 3, 255]);
        var dst = new byte[4];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 1, 1, dst));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, dst);
    }

    [Fact]
    public void DecodesRgb8AsOpaque()
    {
        RequireSkia();
        var png = Png(2, 1, 8, 2, [[10, 20, 30, 40, 50, 60]]);
        var dst = new byte[8];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 2, 1, dst));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dst);
    }

    [Fact]
    public void DecodesPaletteWithTrns()
    {
        RequireSkia();
        // index 0 = red with alpha 128 (tRNS entry), index 1 = green with no tRNS entry (opaque)
        var png = Png(2, 1, 8, 3, [[0, 1]], plte: [255, 0, 0, 0, 255, 0], trns: [128]);
        var dst = new byte[8];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 2, 1, dst));
        Assert.Equal(new byte[] { 255, 0, 0, 128, 0, 255, 0, 255 }, dst);
    }

    [Fact]
    public void DecodesPaletteWithoutTrnsAsOpaque()
    {
        RequireSkia();
        // 4-bit palette: two pixels packed in one byte (0x10 -> indices 1, 0)
        var png = Png(2, 1, 4, 3, [[0x10]], plte: [1, 2, 3, 4, 5, 6]);
        var dst = new byte[8];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 2, 1, dst));
        Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, dst);
    }

    [Fact]
    public void DecodesGrayAlpha()
    {
        RequireSkia();
        var png = Png(2, 1, 8, 4, [[100, 200, 50, 255]]);
        var dst = new byte[8];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 2, 1, dst));
        Assert.Equal(new byte[] { 100, 100, 100, 200, 50, 50, 50, 255 }, dst);
    }

    [Fact]
    public void Decodes16BitRgbaToEightBitWithinRoundingError()
    {
        RequireSkia();
        // one pixel: R=0xFFFF, G=0x8000, B=0x0000, A=0xFFFF
        var png = Png(1, 1, 16, 6, [[0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00, 0xFF, 0xFF]]);
        var dst = new byte[4];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(png, 1, 1, dst));
        Assert.Equal(255, dst[0]);
        Assert.InRange((int)dst[1], 127, 128);
        Assert.Equal(0, dst[2]);
        Assert.Equal(255, dst[3]);
    }

    [Fact]
    public void DecodesImageLargerThanOneStoredBlock()
    {
        RequireSkia();
        // 200x100 RGBA = 80 KB of scanlines: forces a second stored deflate block (65535-byte limit).
        int w = 200, h = 100;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int p = i / 4;
            rgba[i] = (byte)p; rgba[i + 1] = (byte)(p >> 4); rgba[i + 2] = (byte)(p * 3); rgba[i + 3] = 255;
        }
        var dst = new byte[rgba.Length];
        Assert.Equal(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(Rgba8Png(w, h, rgba), w, h, dst));
        Assert.Equal(rgba, dst);
    }

    [Fact]
    public void DimensionMismatchIsInvalid()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 3, 2, new byte[24]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 2, 1, new byte[8]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 4, 4, new byte[64]));
    }

    [Fact]
    public void TooSmallDestinationAndBadArgumentsAreInvalidNotExceptions()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 2, 2, new byte[15]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 0, 2, new byte[16]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 2, -2, new byte[16]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(ReadOnlySpan<byte>.Empty, 2, 2, new byte[16]));
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, int.MaxValue, int.MaxValue, new byte[16]));
    }

    [Fact]
    public void TruncatedPngIsInvalid()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        foreach (int cut in new[] { 4, 8, 20, 33, png.Length - 30, 60 })
        {
            var dst = new byte[16];
            Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png.AsSpan(0, cut), 2, 2, dst));
        }
    }

    [Fact]
    public void CorruptIdatCrcIsInvalid()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        // IDAT's CRC is the 16 bytes before IEND's 12 (4 len + 4 type + 0 data + 4 crc): last 12 = IEND; 4 before = IDAT CRC.
        png[png.Length - 12 - 1] ^= 0xFF;
        Assert.Equal(ImageDecodeStatus.Invalid, Decoder.DecodePngRgba(png, 2, 2, new byte[16]));
    }

    [Fact]
    public void NonPngDataIsRejected()
    {
        RequireSkia();
        var junk = new byte[200];
        new Random(5).NextBytes(junk);
        Assert.NotEqual(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(junk, 2, 2, new byte[16]));
        // A GIF header is a real image format but not a PNG: it must not be handed back as Ok.
        byte[] gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0, 0, 0];
        Assert.NotEqual(ImageDecodeStatus.Ok, Decoder.DecodePngRgba(gif, 1, 1, new byte[4]));
    }

    [Fact]
    public void ConcurrentDecodesAreIndependent()
    {
        RequireSkia();
        var png = Rgba8Png(2, 2, Rgba2x2);
        var results = new ImageDecodeStatus[64];
        Parallel.For(0, results.Length, i =>
        {
            var dst = new byte[16];
            results[i] = Decoder.DecodePngRgba(png, 2, 2, dst);
            if (dst[0] != 255 || dst[5] != 255) results[i] = ImageDecodeStatus.Invalid;
        });
        Assert.All(results, r => Assert.Equal(ImageDecodeStatus.Ok, r));
    }
}
