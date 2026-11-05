using System.Buffers.Binary;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PngHeaderTests
{
    private static readonly byte[] Sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static void Chunk(List<byte> o, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        o.AddRange(len.ToArray());
        o.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        o.AddRange(data);
        o.AddRange(new byte[4]); // CRC is intentionally not validated by the header peek
    }

    private static byte[] Ihdr(uint w, uint h, byte depth, byte color, byte comp = 0, byte filter = 0, byte interlace = 0)
    {
        var d = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0), w);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), h);
        d[8] = depth; d[9] = color; d[10] = comp; d[11] = filter; d[12] = interlace;
        return d;
    }

    private static byte[] Png(byte[] ihdr, params (string Type, byte[] Data)[] more)
    {
        var o = new List<byte>(Sig);
        Chunk(o, "IHDR", ihdr);
        foreach (var (t, d) in more) Chunk(o, t, d);
        return o.ToArray();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    [InlineData(4, 16)]
    [InlineData(6, 8)]
    [InlineData(6, 16)]
    public void AcceptsEveryLegalColourTypeDepthPair(int color, int depth)
    {
        var png = Png(Ihdr(640, 480, (byte)depth, (byte)color));
        Assert.True(PngHeader.TryRead(png, out var info));
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(depth, info.BitDepth);
        Assert.Equal(color, info.ColorType);
        Assert.Equal(0, info.Interlace);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    [InlineData(255, 8)] // colour types
    [InlineData(2, 1)]
    [InlineData(2, 4)]
    [InlineData(6, 4)]
    [InlineData(4, 1)]    // depth too small for channels
    [InlineData(3, 16)]
    [InlineData(0, 3)]
    [InlineData(0, 0)]
    [InlineData(6, 32)]
    public void RejectsIllegalColourTypeDepthPair(int color, int depth)
    {
        Assert.False(PngHeader.TryRead(Png(Ihdr(4, 4, (byte)depth, (byte)color)), out var info));
        Assert.Equal(default(PngInfo), info);
    }

    [Fact]
    public void ReportsInterlaceAndRejectsUnknownMethods()
    {
        Assert.True(PngHeader.TryRead(Png(Ihdr(4, 4, 8, 6, interlace: 1)), out var info));
        Assert.Equal(1, info.Interlace);
        Assert.False(PngHeader.TryRead(Png(Ihdr(4, 4, 8, 6, interlace: 2)), out _));
        Assert.False(PngHeader.TryRead(Png(Ihdr(4, 4, 8, 6, comp: 1)), out _));
        Assert.False(PngHeader.TryRead(Png(Ihdr(4, 4, 8, 6, filter: 1)), out _));
    }

    [Fact]
    public void RejectsEverySingleByteSignatureCorruption()
    {
        var good = Png(Ihdr(2, 2, 8, 6));
        Assert.True(PngHeader.TryRead(good, out _));
        for (int i = 0; i < 8; i++)
        {
            var bad = (byte[])good.Clone();
            bad[i] ^= 0x01;
            Assert.False(PngHeader.TryRead(bad, out _), $"signature byte {i}");
        }
    }

    [Fact]
    public void RejectsEveryTruncationOfTheHeader()
    {
        var good = Png(Ihdr(2, 2, 8, 6));
        for (int len = 0; len < 33; len++)
            Assert.False(PngHeader.TryRead(good.AsSpan(0, len), out _), $"length {len}");
        Assert.True(PngHeader.TryRead(good.AsSpan(0, 33), out _));
    }

    [Fact]
    public void RejectsWrongFirstChunk()
    {
        var good = Png(Ihdr(2, 2, 8, 6));
        var badType = (byte[])good.Clone();
        badType[12] = (byte)'P'; badType[13] = (byte)'L'; badType[14] = (byte)'T'; badType[15] = (byte)'E';
        Assert.False(PngHeader.TryRead(badType, out _));

        var badLen = (byte[])good.Clone();
        badLen[11] = 12;
        Assert.False(PngHeader.TryRead(badLen, out _));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(0u, 0u)]
    [InlineData(0x80000000u, 1u)]
    [InlineData(1u, 0x80000000u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void RejectsZeroAndOverflowingDimensions(uint w, uint h)
    {
        Assert.False(PngHeader.TryRead(Png(Ihdr(w, h, 8, 6)), out _));
    }

    [Fact]
    public void AcceptsLargestRepresentableDimensions()
    {
        Assert.True(PngHeader.TryRead(Png(Ihdr(int.MaxValue, 1, 8, 6)), out var info));
        Assert.Equal(int.MaxValue, info.Width);
        Assert.True(PngHeader.TryRead(Png(Ihdr(1, int.MaxValue, 8, 6)), out info));
        Assert.Equal(int.MaxValue, info.Height);
    }

    [Fact]
    public void DetectsTrnsBeforeFirstIdatOnly()
    {
        byte[] none = [];
        var palette = Png(Ihdr(2, 2, 8, 3), ("PLTE", new byte[6]), ("tRNS", new byte[] { 0 }), ("IDAT", none));
        Assert.True(PngHeader.TryRead(palette, out var info));
        Assert.True(info.HasTrns);
        Assert.True(info.MayBeTransparent);

        var noTrns = Png(Ihdr(2, 2, 8, 3), ("PLTE", new byte[6]), ("IDAT", none), ("tRNS", new byte[] { 0 }));
        Assert.True(PngHeader.TryRead(noTrns, out info));
        Assert.False(info.HasTrns); // a tRNS after IDAT is not legal and is not reported
        Assert.False(info.MayBeTransparent);

        var gray = Png(Ihdr(2, 2, 8, 0), ("tRNS", new byte[2]), ("IDAT", none));
        Assert.True(PngHeader.TryRead(gray, out info));
        Assert.True(info.HasTrns);

        var rgb = Png(Ihdr(2, 2, 8, 2), ("gAMA", new byte[4]), ("sRGB", new byte[1]), ("tRNS", new byte[6]), ("IDAT", none));
        Assert.True(PngHeader.TryRead(rgb, out info));
        Assert.True(info.HasTrns);
    }

    [Fact]
    public void AlphaColourTypesAreTransparentWithoutTrns()
    {
        Assert.True(PngHeader.TryRead(Png(Ihdr(2, 2, 8, 6)), out var rgba));
        Assert.False(rgba.HasTrns);
        Assert.True(rgba.MayBeTransparent);
        Assert.True(PngHeader.TryRead(Png(Ihdr(2, 2, 8, 4)), out var ga));
        Assert.True(ga.MayBeTransparent);
        Assert.True(PngHeader.TryRead(Png(Ihdr(2, 2, 8, 6), ("tRNS", new byte[6])), out var odd));
        Assert.False(odd.HasTrns); // meaningless for colour type 6
        Assert.True(PngHeader.TryRead(Png(Ihdr(2, 2, 8, 2)), out var opaque));
        Assert.False(opaque.MayBeTransparent);
    }

    [Fact]
    public void TrnsScanToleratesTruncatedOrHostileChunkLengths()
    {
        var o = new List<byte>(Sig);
        Chunk(o, "IHDR", Ihdr(2, 2, 8, 3));
        // chunk claiming 4 GiB - 1 of data, then a would-be tRNS that must not be reached
        o.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        o.AddRange("PLTE"u8.ToArray());
        o.AddRange(new byte[10]);
        Assert.True(PngHeader.TryRead(o.ToArray(), out var info));
        Assert.False(info.HasTrns);

        // truncated mid-chunk-header
        var cut = Png(Ihdr(2, 2, 8, 3)).Concat(new byte[] { 0, 0, 0 }).ToArray();
        Assert.True(PngHeader.TryRead(cut, out info));
        Assert.False(info.HasTrns);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class PngHeaderAllocationTests
{
    [Fact]
    public void TryReadDoesNotAllocate()
    {
        var o = new List<byte>(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        o.AddRange(new byte[] { 0, 0, 0, 13 });
        o.AddRange("IHDR"u8.ToArray());
        o.AddRange(new byte[] { 0, 0, 1, 0, 0, 0, 1, 0, 8, 3, 0, 0, 0, 0, 0, 0, 0 });
        o.AddRange(new byte[] { 0, 0, 0, 1 });
        o.AddRange("tRNS"u8.ToArray());
        o.AddRange(new byte[5]);
        var png = o.ToArray();
        AllocationAssert.NoAllocations(() => PngHeader.TryRead(png, out _));
    }
}
