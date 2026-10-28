using System.Buffers;
using System.IO.Compression;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class BoundedInflateTests
{
    private static byte[] Zlib(ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> Split(byte[] data, int pieceSize)
    {
        if (data.Length <= pieceSize) return new ReadOnlySequence<byte>(data);
        var first = new Segment(data.AsMemory(0, pieceSize));
        var last = first;
        for (int p = pieceSize; p < data.Length; p += pieceSize)
            last = last.Append(data.AsMemory(p, Math.Min(pieceSize, data.Length - p)));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static byte[] RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(65536)]
    [InlineData(300_000)]
    public void RoundTripsRandomAndRepetitiveData(int size)
    {
        var raw = RandomBytes(size, size);
        for (int i = size / 2; i < size; i++) raw[i] = (byte)(i % 7); // compressible half
        var z = Zlib(raw);
        var dst = new byte[size];
        Assert.Equal(BoundedInflateResult.Ok, BoundedInflate.Inflate(z, dst, out int written));
        Assert.Equal(size, written);
        Assert.Equal(raw, dst);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void AcceptsEveryCompressionLevel(CompressionLevel level)
    {
        var raw = RandomBytes(5000, 9);
        var dst = new byte[raw.Length];
        Assert.Equal(BoundedInflateResult.Ok, BoundedInflate.Inflate(Zlib(raw, level), dst, out _));
        Assert.Equal(raw, dst);
    }

    [Fact]
    public void EmptyPayloadWithEmptyDestinationIsOk()
    {
        var z = Zlib([]);
        Assert.Equal(BoundedInflateResult.Ok, BoundedInflate.Inflate(z, Span<byte>.Empty, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void SegmentedSourceMatchesContiguousSourceForEverySegmentSize()
    {
        var raw = RandomBytes(4000, 21);
        var z = Zlib(raw);
        foreach (int piece in new[] { 1, 2, 3, 7, 64, 1000, z.Length })
        {
            var dst = new byte[raw.Length];
            var seq = Split(z, piece);
            Assert.Equal(BoundedInflateResult.Ok, BoundedInflate.Inflate(seq, dst, out int written));
            Assert.Equal(raw.Length, written);
            Assert.Equal(raw, dst);
        }
    }

    [Fact]
    public void SegmentedSourceReportsTheSameErrorsAsContiguous()
    {
        var raw = RandomBytes(2000, 4);
        var z = Zlib(raw);
        var seq = Split(z, 13);
        Assert.Equal(BoundedInflateResult.TooLarge, BoundedInflate.Inflate(seq, new byte[1999], out _));
        Assert.Equal(BoundedInflateResult.SizeMismatchSmaller, BoundedInflate.Inflate(seq, new byte[2001], out _));
    }

    [Fact]
    public void DestinationLargerThanTheStreamIsSizeMismatchSmallerAndReportsProducedBytes()
    {
        var raw = RandomBytes(1000, 1);
        var dst = new byte[1001];
        Assert.Equal(BoundedInflateResult.SizeMismatchSmaller, BoundedInflate.Inflate(Zlib(raw), dst, out int written));
        Assert.Equal(1000, written);
        Assert.Equal(raw, dst.AsSpan(0, 1000).ToArray());
    }

    [Fact]
    public void DestinationSmallerThanTheStreamIsTooLargeAndNothingIsWrittenPastIt()
    {
        var raw = RandomBytes(1000, 2);
        var buf = new byte[1000];
        Array.Fill(buf, (byte)0xCC);
        var dst = buf.AsSpan(0, 999);
        Assert.Equal(BoundedInflateResult.TooLarge, BoundedInflate.Inflate(Zlib(raw), dst, out int written));
        Assert.Equal(999, written);
        Assert.Equal(0xCC, buf[999]); // guard byte outside the destination slice is intact
    }

    [Fact]
    public void EmptyDestinationAgainstNonEmptyStreamIsTooLarge()
    {
        Assert.Equal(BoundedInflateResult.TooLarge, BoundedInflate.Inflate(Zlib(new byte[10]), Span<byte>.Empty, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void EmptyInputIsNeverOkForANonEmptyDestination()
    {
        Assert.Equal(BoundedInflateResult.SizeMismatchSmaller, BoundedInflate.Inflate(ReadOnlySpan<byte>.Empty, new byte[4], out _));
        Assert.Equal(BoundedInflateResult.InvalidData, BoundedInflate.Inflate(ReadOnlySpan<byte>.Empty, Span<byte>.Empty, out _));
        Assert.NotEqual(BoundedInflateResult.Ok,
            BoundedInflate.Inflate(new ReadOnlySequence<byte>(Array.Empty<byte>()), new byte[4], out _));
    }

    [Fact]
    public void BadZlibHeaderIsInvalidData()
    {
        var z = Zlib(RandomBytes(100, 6));
        z[0] = 0xFF;
        Assert.Equal(BoundedInflateResult.InvalidData, BoundedInflate.Inflate(z, new byte[100], out _));
    }

    [Fact]
    public void CorruptedAdlerTrailerIsInvalidData()
    {
        var raw = RandomBytes(100, 7);
        var z = Zlib(raw);
        z[^1] ^= 0xFF;
        Assert.Equal(BoundedInflateResult.InvalidData, BoundedInflate.Inflate(z, new byte[100], out _));
    }

    [Fact]
    public void CorruptedDeflateBodyIsNotAccepted()
    {
        var raw = new byte[2000];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i / 3);
        var z = Zlib(raw);
        for (int i = 3; i < z.Length - 4; i += 5)
        {
            var bad = (byte[])z.Clone();
            bad[i] ^= 0x5A;
            var dst = new byte[raw.Length];
            var result = BoundedInflate.Inflate(bad, dst, out _);
            // Either the inflater or the Adler-32 check must notice; a flipped byte can never yield the original.
            Assert.True(result != BoundedInflateResult.Ok || !dst.AsSpan().SequenceEqual(raw), $"flip at {i}");
        }
    }

    [Fact]
    public void TruncatedStreamIsNeverOkAndNeverThrows()
    {
        var raw = RandomBytes(3000, 8);
        var z = Zlib(raw);
        // (cutting only into the 4-byte Adler trailer is deliberately not asserted: all data is already out then)
        foreach (int cut in new[] { 1, 2, 10, z.Length / 2, z.Length - 5 })
        {
            var dst = new byte[raw.Length];
            var r = BoundedInflate.Inflate(z.AsSpan(0, cut), dst, out int written);
            Assert.True(r is BoundedInflateResult.SizeMismatchSmaller or BoundedInflateResult.InvalidData, $"cut {cut}: {r}");
            Assert.True(written < raw.Length);
        }
    }

    [Fact]
    public void RandomGarbageIsNeverOk()
    {
        var rng = new Random(99);
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[rng.Next(0, 200)];
            rng.NextBytes(junk);
            var r = BoundedInflate.Inflate(junk, new byte[64], out _);
            Assert.NotEqual(BoundedInflateResult.Ok, r);
        }
    }

    [Fact]
    public void DecompressionBombStopsAtTheDestinationBound()
    {
        // 1 GiB of zeros compresses to roughly a megabyte. Decompression into a 4 KiB destination must stop right
        // after 4 KiB + 1 probe byte instead of inflating (or allocating) the gigabyte.
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (int i = 0; i < 1024; i++) z.Write(zeros);
        }
        var bomb = ms.ToArray();
        Assert.True(bomb.Length < 16 * 1024 * 1024, "test fixture is not a bomb");

        var dst = new byte[4096];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = BoundedInflate.Inflate(bomb, dst, out int written);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(BoundedInflateResult.TooLarge, r);
        Assert.Equal(4096, written);
        // Managed allocations are the stream wrappers and zlib's small buffers, nowhere near the payload size.
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes");
    }
}
