using System.Text;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class KittyBase64DecoderTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Feeds the pieces (each a slice of one chunk-sequence) and finishes; returns status and decoded bytes.</summary>
    private static (KittyBase64Status Status, byte[] Data) Run(IEnumerable<(string Text, bool FinalChunk)> pieces, int capacity = 1 << 16, bool finish = true)
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[capacity];
        int pos = 0;
        var status = KittyBase64Status.Ok;
        foreach (var (text, final) in pieces)
        {
            status = dec.Decode(Ascii(text), dst.AsSpan(pos), out int w, final);
            if (status != KittyBase64Status.Ok) return (status, Array.Empty<byte>());
            pos += w;
        }
        if (finish)
        {
            status = dec.Finish(dst.AsSpan(pos), out int w);
            if (status != KittyBase64Status.Ok) return (status, Array.Empty<byte>());
            pos += w;
        }
        Assert.Equal(pos, dec.TotalDecoded);
        return (status, dst.AsSpan(0, pos).ToArray());
    }

    private static (KittyBase64Status, byte[]) RunSingle(string text) => Run([(text, true)]);

    [Theory]
    [InlineData("TWFu", "Man")]
    [InlineData("TWE=", "Ma")]
    [InlineData("TQ==", "M")]
    [InlineData("TWE", "Ma")]   // unpadded final
    [InlineData("TQ", "M")]     // unpadded final
    [InlineData("", "")]
    [InlineData("TWFuTWFu", "ManMan")]
    [InlineData("TWFuTQ==", "ManM")]
    [InlineData("+/+/", "\u00fb\u00ff\u00bf")]
    public void DecodesKnownVectors(string b64, string expected)
    {
        var (status, data) = RunSingle(b64);
        Assert.Equal(KittyBase64Status.Ok, status);
        Assert.Equal(Encoding.Latin1.GetBytes(expected), data);
    }

    [Fact]
    public void EveryTwoWaySplitGivesTheSameBytes()
    {
        var raw = Enumerable.Range(0, 37).Select(i => (byte)(i * 7 + 3)).ToArray();
        foreach (var text in new[] { Convert.ToBase64String(raw), Convert.ToBase64String(raw).TrimEnd('=') })
        {
            for (int cut = 0; cut <= text.Length; cut++)
            {
                var (status, data) = Run([(text[..cut], true), (text[cut..], true)]);
                Assert.Equal(KittyBase64Status.Ok, status);
                Assert.Equal(raw, data);
            }
        }
    }

    [Fact]
    public void EveryThreeWaySplitGivesTheSameBytes()
    {
        var raw = Enumerable.Range(0, 11).Select(i => (byte)(255 - i * 19)).ToArray();
        var text = Convert.ToBase64String(raw); // 16 chars, padded
        for (int a = 0; a <= text.Length; a++)
            for (int b = a; b <= text.Length; b++)
            {
                var (status, data) = Run([(text[..a], true), (text[a..b], true), (text[b..], true)]);
                Assert.Equal(KittyBase64Status.Ok, status);
                Assert.Equal(raw, data);
            }
    }

    [Fact]
    public void ByteAtATimeFeedingWorksForAllTailLengths()
    {
        for (int n = 0; n <= 9; n++)
        {
            var raw = Enumerable.Range(1, n).Select(i => (byte)(i * 31)).ToArray();
            var text = Convert.ToBase64String(raw);
            var (status, data) = Run(text.Select(c => (c.ToString(), true)));
            Assert.Equal(KittyBase64Status.Ok, status);
            Assert.Equal(raw, data);
        }
    }

    [Fact]
    public void QuartetsMaySpanProtocolChunks()
    {
        // m=1 chunks normally hold multiples of four characters, but a quartet split across chunks must still work.
        var (status, data) = Run([("TWF", false), ("uTQ", false), ("==", true)]);
        Assert.Equal(KittyBase64Status.Ok, status);
        Assert.Equal(Encoding.ASCII.GetBytes("ManM"), data);
    }

    [Fact]
    public void CarriesAtMostThreeCharacters()
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[16];
        for (int i = 1; i <= 7; i++)
        {
            Assert.Equal(KittyBase64Status.Ok, dec.Decode("A"u8, dst, out _, false));
            Assert.Equal(i % 4, dec.PendingChars);
        }
    }

    [Theory]
    [InlineData("TQ=")]        // one pad missing (no further input)
    [InlineData("T")]          // lone character
    [InlineData("TWFuT")]      // lone trailing character
    [InlineData("TQ=" + "=A")] // data after padding
    [InlineData("T===")]       // pad count > 2 / too few data chars
    [InlineData("====")]
    [InlineData("=")]
    [InlineData("TWFu=")]      // pad after a complete quartet
    [InlineData("TWFu==")]
    [InlineData("TQ===")]      // too many pads
    [InlineData("TQ==TQ==")]   // padding mid-stream
    [InlineData("TQ==TWFu")]
    [InlineData("=TQ=")]
    public void RejectsMisplacedOrIncompletePadding(string text)
    {
        Assert.Equal(KittyBase64Status.Error, RunSingle(text).Item1);
    }

    [Fact]
    public void PaddingMaySplitAcrossSlicesOfTheFinalChunk()
    {
        var (status, data) = Run([("TQ", true), ("=", true), ("=", true)]);
        Assert.Equal(KittyBase64Status.Ok, status);
        Assert.Equal("M"u8.ToArray(), data);
    }

    [Fact]
    public void PaddingInANonFinalChunkIsAnError()
    {
        Assert.Equal(KittyBase64Status.Error, Run([("TQ==", false), ("TWFu", true)]).Status);
        Assert.Equal(KittyBase64Status.Error, Run([("TWE=", false)], finish: false).Status);
    }

    [Fact]
    public void PaddingOnlyCountsWhenCompletingTheQuartetOfTheLastData()
    {
        // data after a fully padded quartet, in a later slice of the same chunk
        Assert.Equal(KittyBase64Status.Error, Run([("TWE=", true), ("A", true)]).Status);
        // padded quartet, then an extra '=' in a later slice
        Assert.Equal(KittyBase64Status.Error, Run([("TWE=", true), ("=", true)]).Status);
    }

    [Fact]
    public void UnpaddedFinalChunkIsFlushedByFinishOnly()
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[8];
        // "TQ" is the canonical unpadded encoding of the final "M" ("TW" carries non-zero unused bits, which the BCL
        // decoder rejects; see the decoder's rules).
        Assert.Equal(KittyBase64Status.Ok, dec.Decode("TWFuTQ"u8, dst, out int w, true));
        Assert.Equal(3, w);
        Assert.Equal(2, dec.PendingChars);
        Assert.Equal(KittyBase64Status.Ok, dec.Finish(dst.AsSpan(w), out int f));
        Assert.Equal(1, f);
        Assert.Equal(4, dec.TotalDecoded);
        Assert.Equal("ManM"u8.ToArray(), dst.AsSpan(0, 4).ToArray());
    }

    [Theory]
    [InlineData("TW Fu")]
    [InlineData("TWFu\n")]
    [InlineData("TWFu\r\nTWFu")]
    [InlineData("TWF\t")]
    [InlineData("TW-u")]   // url-safe alphabet is not accepted
    [InlineData("TW_u")]
    [InlineData("TWF\u0000")]
    [InlineData("TWFu\u007f")]
    public void NonAlphabetBytesAreErrors(string text)
    {
        Assert.Equal(KittyBase64Status.Error, RunSingle(text).Item1);
    }

    [Fact]
    public void HighBitBytesAreErrors()
    {
        var dec = new KittyBase64Decoder();
        Assert.Equal(KittyBase64Status.Error, dec.Decode(new byte[] { (byte)'T', 0xC3, 0xA9, (byte)'u' }, new byte[8], out _, true));
    }

    [Fact]
    public void ErrorsAreStickyAndLaterInputIsIgnored()
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[16];
        Assert.Equal(KittyBase64Status.Error, dec.Decode("TW!u"u8, dst, out _, true));
        long total = dec.TotalDecoded;
        Assert.Equal(KittyBase64Status.Error, dec.Decode("TWFu"u8, dst, out int w, true));
        Assert.Equal(0, w);
        Assert.Equal(KittyBase64Status.Error, dec.Finish(dst, out _));
        Assert.Equal(total, dec.TotalDecoded);
        Assert.Equal(KittyBase64Status.Error, dec.Status);
        Assert.Equal(0, dst[0]);
    }

    [Fact]
    public void ResetClearsStickyStateAndCarry()
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[8];
        dec.Decode("TW"u8, dst, out _, true);
        dec.Decode("!"u8, dst, out _, true);
        Assert.Equal(KittyBase64Status.Error, dec.Status);
        dec.Reset();
        Assert.Equal(KittyBase64Status.Ok, dec.Status);
        Assert.Equal(0, dec.PendingChars);
        Assert.Equal(0, dec.TotalDecoded);
        Assert.Equal(KittyBase64Status.Ok, dec.Decode("TWFu"u8, dst, out int w, true));
        Assert.Equal(3, w);
    }

    [Fact]
    public void ExactlyFittingDestinationSucceedsOneByteShortOverflows()
    {
        var raw = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        var text = Convert.ToBase64String(raw);
        Assert.Equal(KittyBase64Status.Ok, Run([(text, true)], capacity: 10).Status);
        Assert.Equal(KittyBase64Status.Overflow, Run([(text, true)], capacity: 9).Status);
        // Unpadded tail flushed by Finish must be bounds-checked too.
        Assert.Equal(KittyBase64Status.Ok, Run([(text.TrimEnd('='), true)], capacity: 10).Status);
        Assert.Equal(KittyBase64Status.Overflow, Run([(text.TrimEnd('='), true)], capacity: 9).Status);
        Assert.Equal(KittyBase64Status.Overflow, Run([("TWFu", true)], capacity: 2).Status);
        Assert.Equal(KittyBase64Status.Overflow, Run([("TQ==", true)], capacity: 0).Status);
    }

    [Fact]
    public void OverflowOfACarriedQuartetIsDetectedAndSticky()
    {
        var dec = new KittyBase64Decoder();
        var dst = new byte[2];
        Assert.Equal(KittyBase64Status.Ok, dec.Decode("TW"u8, dst, out _, false));
        Assert.Equal(KittyBase64Status.Overflow, dec.Decode("Fu"u8, dst, out _, false));
        Assert.Equal(KittyBase64Status.Overflow, dec.Decode("TQ"u8, new byte[100], out _, false));
        Assert.Equal(KittyBase64Status.Overflow, dec.Finish(new byte[100], out _));
    }

    [Fact]
    public void RandomDataWithRandomSlicingMatchesConvert()
    {
        var rng = new Random(0x4B1774);
        for (int iter = 0; iter < 400; iter++)
        {
            var raw = new byte[rng.Next(0, 301)];
            rng.NextBytes(raw);
            string text = Convert.ToBase64String(raw);
            if (rng.Next(2) == 0) text = text.TrimEnd('='); // clients may omit padding
            var pieces = new List<(string, bool)>();
            for (int p = 0; p < text.Length;)
            {
                int n = Math.Min(text.Length - p, rng.Next(1, 40));
                pieces.Add((text.Substring(p, n), true));
                p += n;
            }
            var (status, data) = Run(pieces, capacity: raw.Length);
            Assert.Equal(KittyBase64Status.Ok, status);
            Assert.Equal(Convert.FromBase64String(Convert.ToBase64String(raw)), data);
        }
    }

    [Fact]
    public void RandomMultiChunkTransfersMatchConvert()
    {
        // Typical client: unpadded-quartet-aligned 4096-char chunks; here also misaligned ones.
        var rng = new Random(77);
        var raw = new byte[100_000];
        rng.NextBytes(raw);
        string text = Convert.ToBase64String(raw);
        var pieces = new List<(string, bool)>();
        for (int p = 0; p < text.Length;)
        {
            int n = Math.Min(text.Length - p, rng.Next(1, 9000));
            if (text.Length - p - n is > 0 and < 4) n = text.Length - p; // keep '=' padding inside the final chunk
            p += n;
            pieces.Add((text.Substring(p - n, n), p == text.Length));
        }
        var (status, data) = Run(pieces, capacity: raw.Length);
        Assert.Equal(KittyBase64Status.Ok, status);
        Assert.Equal(raw, data);
    }

    [Fact]
    public void LargeAlignedAndUnalignedSlicesAgree()
    {
        var rng = new Random(5);
        var raw = new byte[1 << 20];
        rng.NextBytes(raw);
        var text = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
        foreach (int slice in new[] { 4096, 4093, 65537 })
        {
            var dec = new KittyBase64Decoder();
            var dst = new byte[raw.Length];
            int pos = 0;
            for (int p = 0; p < text.Length; p += slice)
            {
                int n = Math.Min(slice, text.Length - p);
                Assert.Equal(KittyBase64Status.Ok, dec.Decode(text.AsSpan(p, n), dst.AsSpan(pos), out int w, p + n == text.Length));
                pos += w;
            }
            Assert.Equal(KittyBase64Status.Ok, dec.Finish(dst.AsSpan(pos), out int f));
            Assert.Equal(raw.Length, pos + f);
            Assert.Equal(raw.Length, dec.TotalDecoded);
            Assert.True(raw.AsSpan().SequenceEqual(dst), $"slice {slice}");
        }
    }

    [Fact]
    public void EmptyInputsAreHarmless()
    {
        var dec = new KittyBase64Decoder();
        Assert.Equal(KittyBase64Status.Ok, dec.Decode(default, default, out int w, true));
        Assert.Equal(0, w);
        Assert.Equal(KittyBase64Status.Ok, dec.Finish(default, out w));
        Assert.Equal(0, w);
        Assert.Equal(0, dec.TotalDecoded);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class KittyBase64DecoderAllocationTests
{
    [Fact]
    public void SteadyStateDecodeDoesNotAllocate()
    {
        var dec = new KittyBase64Decoder();
        var input = Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[3000]));
        var dst = new byte[4096];
        AllocationAssert.NoAllocations(() =>
        {
            dec.Reset();
            // odd split so the carry path is exercised on every iteration
            dec.Decode(input.AsSpan(0, 1001), dst, out int w, false);
            dec.Decode(input.AsSpan(1001), dst.AsSpan(w), out int w2, true);
            dec.Finish(dst.AsSpan(w + w2), out _);
        });
    }
}
