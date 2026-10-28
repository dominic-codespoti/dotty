using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// The payload engine behind <c>a=t</c>: formats, compression, PNG, chunking, validation order and exact error
/// replies. Driven through <see cref="KittyGraphicsController"/> with a fake host unless a parser/adapter is needed.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyTransferTests
{
    private static byte[] StoredPixels(KittyRig rig, uint clientId)
    {
        var image = rig.Store.FindByClientId(clientId);
        Assert.NotNull(image);
        return image!.Root.AsSpan().ToArray();
    }

    // ------------------------------------------------------------------------------------------------ formats

    [Fact]
    public void F24_RoundTripKeepsRgbBytesAndFormat()
    {
        using var rig = new KittyRig();
        byte[] data = { 255, 0, 0, 0, 255, 0 };
        rig.Apc("i=5,s=2,v=1,f=24", B64(data));

        Assert.Equal(new[] { Ok("i=5") }, rig.Replies);
        var image = rig.Store.FindByClientId(5)!;
        Assert.Equal(ImagePixelFormat.Rgb8, image.Root.Format);
        Assert.Equal(2, image.Root.Width);
        Assert.Equal(1, image.Root.Height);
        Assert.Equal(6, image.Root.Stride);
        Assert.Equal(data, image.Root.AsSpan().ToArray());
        Assert.Equal(6, rig.Store.UsedBytes);
    }

    [Fact]
    public void F32_IsPremultipliedWithHandDerivedValues()
    {
        using var rig = new KittyRig();
        // c' = round(c * a / 255): 200*128/255 = 100.39, 100*128/255 = 50.2, 50*128/255 = 25.1; a=0 clears; a=255 keeps.
        byte[] data = { 200, 100, 50, 128, 10, 20, 30, 0, 1, 2, 3, 255 };
        rig.Apc("i=1,s=3,v=1,f=32", B64(data));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        var image = rig.Store.FindByClientId(1)!;
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Root.Format);
        Assert.Equal(new byte[] { 100, 50, 25, 128, 0, 0, 0, 0, 1, 2, 3, 255 }, image.Root.AsSpan().ToArray());
    }

    [Fact]
    public void F32_LongerRunsMatchTheRoundToNearestFormulaPerPixel()
    {
        using var rig = new KittyRig();
        byte[] data = RandomBytes(37 * 4, seed: 4242); // 37 pixels: whole vectors plus a scalar tail
        data[3] = 0; data[7] = 255; data[11] = 1; data[15] = 254;
        rig.Apc("i=1,s=37,v=1,f=32", B64(data));

        var expected = (byte[])data.Clone();
        for (int i = 0; i < expected.Length; i += 4)
        {
            int a = data[i + 3];
            for (int c = 0; c < 3; c++)
                expected[i + c] = (byte)((data[i + c] * a + 127) / 255);
        }
        Assert.Equal(expected, StoredPixels(rig, 1));
    }

    [Fact]
    public void F32_IsTheDefaultFormat()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1", B64(100, 100, 100, 255));
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, rig.Store.FindByClientId(1)!.Root.Format);
    }

    [Fact]
    public void F100_PngIsDecodedAndPremultiplied()
    {
        using var rig = new KittyRig();
        byte[] rgba = { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 255 };
        rig.Apc("i=2,f=100", B64(TestPng.Build(2, 2, rgba)));

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        var image = rig.Store.FindByClientId(2)!;
        Assert.Equal(2, image.Root.Width);
        Assert.Equal(2, image.Root.Height);
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Root.Format);
        // green at alpha 128: round(255*128/255) = 128; fully transparent blue becomes 0,0,0,0.
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 128, 0, 128, 0, 0, 0, 0, 10, 20, 30, 255 }, image.Root.AsSpan().ToArray());
        Assert.Equal(1, rig.Decoder.Calls);
    }

    [Fact]
    public void F100_OpaquePngKeepsAnRgbaBuffer()
    {
        using var rig = new KittyRig();
        rig.Apc("i=2,f=100", B64(TestPng.Build(2, 1, new byte[] { 1, 2, 3, 4, 5, 6 }, alpha: false)));

        var image = rig.Store.FindByClientId(2)!;
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Root.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, image.Root.AsSpan().ToArray());
    }

    [Fact]
    public void F100_ChunkedPngAccumulatesAcrossChunks()
    {
        using var rig = new KittyRig();
        string b64 = B64(TestPng.Build(2, 1, new byte[] { 9, 8, 7, 255, 6, 5, 4, 255 }));
        int a = 20, b = 47;
        rig.Apc("i=2,f=100,m=1", b64[..a]);
        rig.Apc("m=1", b64[a..b]);
        Assert.Empty(rig.Replies);
        Assert.True(rig.Controller.HasOpenTransfer);
        rig.Apc("m=0", b64[b..]);

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        Assert.Equal(new byte[] { 9, 8, 7, 255, 6, 5, 4, 255 }, StoredPixels(rig, 2));
    }

    [Fact]
    public void F100_WithoutADecoderIsReportedAsAnUnknownFormat()
    {
        using var rig = new KittyRig(withDecoder: false);
        rig.Apc("i=2,f=100", B64(TestPng.Build(1, 1, new byte[] { 1, 2, 3, 4 })));
        Assert.Equal(new[] { Err("i=2", "EINVAL:Unknown image format") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void F100_BadSignatureIsAPngError()
    {
        using var rig = new KittyRig();
        rig.Apc("i=2,f=100", B64(RandomBytes(60, 1)));
        Assert.Equal(new[] { Err("i=2", "EBADPNG:Invalid PNG data") }, rig.Replies);
        Assert.Equal(0, rig.Decoder.Calls);
    }

    [Fact]
    public void F100_PayloadShorterThanAHeaderIsAPngError()
    {
        using var rig = new KittyRig();
        rig.Apc("i=2,f=100", B64(TestPng.HeaderOnly(1, 1).AsSpan(0, 20)));
        rig.Apc("i=3,f=100"); // no payload at all
        Assert.Equal(new[] { Err("i=2", "EBADPNG:Invalid PNG data"), Err("i=3", "EBADPNG:Invalid PNG data") }, rig.Replies);
    }

    [Fact]
    public void F100_HeaderWithTooLargeDimensionsIsRejectedBeforeTheRestOfThePayloadArrives()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            byte[] header = TestPng.HeaderOnly(20000, 1);
            rig.Apc("i=2,f=100,m=1", B64(header));
            // Answered at the end of the first chunk already; the transfer is gone.
            Assert.Equal(new[] { Err("i=2", "EFBIG:Image too large") }, rig.Replies);
            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, baseline.PendingDelta);

            rig.Apc("m=1", B64(RandomBytes(300, 2)));
            rig.Apc("m=0", B64(RandomBytes(300, 3)));
            Assert.Single(rig.Replies);
        }
        Assert.Equal(0, baseline.AllocatedDelta);
        baseline.AssertRestored();
    }

    [Fact]
    public void F100_HeaderOverTheQuotaIsRejectedFromTheHeaderAlone()
    {
        var baseline = new GraphicsBaseline();
        using var rig = new KittyRig();
        rig.Apc("i=2,f=100", B64(TestPng.HeaderOnly(10000, 10000))); // 400 MB RGBA > 320 MiB
        Assert.Equal(new[] { Err("i=2", "ENOSPC:Storage quota exceeded") }, rig.Replies);
        Assert.Equal(0, baseline.AllocatedDelta);
    }

    [Fact]
    public void F100_CorruptPngDataIsAPngError()
    {
        using var rig = new KittyRig();
        byte[] png = TestPng.Build(2, 2, new byte[16]);
        png[^20] ^= 0xFF; // inside the IDAT data: its CRC no longer matches
        rig.Apc("i=2,f=100", B64(png));
        Assert.Equal(new[] { Err("i=2", "EBADPNG:Invalid PNG data") }, rig.Replies);
        Assert.Equal(1, rig.Decoder.Calls);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void F100_DecoderThatCannotHandleThePngReportsADecodeFailure()
    {
        using var rig = new KittyRig();
        rig.Decoder.Forced = ImageDecodeStatus.Unsupported;
        rig.Apc("i=2,f=100", B64(TestPng.Build(1, 1, new byte[4])));
        Assert.Equal(new[] { Err("i=2", "EILSEQ:Failed to decode image data") }, rig.Replies);
    }

    [Fact]
    public void F100_ThrowingDecoderIsContainedAndReported()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Decoder.ThrowOnDecode = new InvalidOperationException("native decoder blew up");
            rig.Apc("i=2,f=100", B64(TestPng.Build(1, 1, new byte[4])));
            Assert.Equal(new[] { Err("i=2", "EILSEQ:Failed to decode image data") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);

            // The controller still works afterwards.
            rig.Decoder.ThrowOnDecode = null;
            rig.Apc("i=3,f=100", B64(TestPng.Build(1, 1, new byte[4])));
            Assert.Equal(Ok("i=3"), rig.Replies[^1]);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void F100_CompressedPngNeedsSAsTheInflatedSize()
    {
        using var rig = new KittyRig();
        byte[] png = TestPng.Build(2, 1, new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 });
        string z = B64(TestPng.Zlib(png));

        rig.Apc($"i=1,f=100,o=z,S={png.Length}", z);
        rig.Apc("i=2,f=100,o=z", z);                       // S missing
        rig.Apc($"i=3,f=100,o=z,S={png.Length - 1}", z);   // too small: stream holds more
        rig.Apc($"i=4,f=100,o=z,S={png.Length + 1}", z);   // too large: stream ends early

        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=2", "EINVAL:Invalid key value"),
            Err("i=3", "EINVAL:Image data size does not match declared size"),
            Err("i=4", "EINVAL:Image data size does not match declared size"),
        }, rig.Replies);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, StoredPixels(rig, 1));
    }

    // ------------------------------------------------------------------------------------------- compression

    [Fact]
    public void Compressed_F32RoundTripsAndPremultiplies()
    {
        using var rig = new KittyRig();
        byte[] data = { 200, 100, 50, 128, 10, 20, 30, 0, 1, 2, 3, 255, 255, 255, 255, 255 };
        rig.Apc("i=3,s=2,v=2,f=32,o=z", B64(TestPng.Zlib(data)));

        Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
        Assert.Equal(new byte[] { 100, 50, 25, 128, 0, 0, 0, 0, 1, 2, 3, 255, 255, 255, 255, 255 }, StoredPixels(rig, 3));
    }

    [Fact]
    public void Compressed_F24RoundTripsWithoutAlpha()
    {
        using var rig = new KittyRig();
        byte[] data = RandomBytes(5 * 4 * 3, 7);
        rig.Apc("i=3,s=5,v=4,f=24,o=z", B64(TestPng.Zlib(data)));

        Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
        var image = rig.Store.FindByClientId(3)!;
        Assert.Equal(ImagePixelFormat.Rgb8, image.Root.Format);
        Assert.Equal(data, image.Root.AsSpan().ToArray());
    }

    [Fact]
    public void Compressed_ChunkedStreamRoundTrips()
    {
        using var rig = new KittyRig();
        byte[] data = RandomBytes(10 * 10 * 3, 8);
        string b64 = B64(TestPng.Zlib(data));
        rig.Apc("i=3,s=10,v=10,f=24,o=z,m=1", b64[..40]);
        rig.Apc("m=1", b64[40..100]);
        rig.Apc("m=0", b64[100..]);

        Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
        Assert.Equal(data, StoredPixels(rig, 3));
    }

    [Fact]
    public void Compressed_WrongInflatedSizeIsASizeMismatch()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=32,o=z", B64(TestPng.Zlib(new byte[8])));    // 8 of 16 bytes
        rig.Apc("i=2,s=2,v=2,f=32,o=z", B64(TestPng.Zlib(new byte[32])));   // 32 of 16 bytes
        rig.Apc("i=3,s=2,v=2,f=32,o=z", B64(TestPng.Zlib(new byte[17])));   // one byte too many

        string mismatch = "EINVAL:Image data size does not match declared size";
        Assert.Equal(new[] { Err("i=1", mismatch), Err("i=2", mismatch), Err("i=3", mismatch) }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void Compressed_ExactSizeStreamIsAccepted()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=32,o=z", B64(TestPng.Zlib(new byte[16])));
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void Compressed_InvalidZlibIsAnInflateError()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=32,o=z", B64(Enumerable.Repeat((byte)0xFF, 24).ToArray()));
        Assert.Equal(new[] { Err("i=1", "EILSEQ:Failed to inflate image data") }, rig.Replies);
    }

    [Fact]
    public void Compressed_TruncatedStreamIsRejected()
    {
        using var rig = new KittyRig();
        byte[] z = TestPng.Zlib(RandomBytes(300, 9), CompressionLevel.NoCompression);
        rig.Apc("i=1,s=100,v=1,f=24,o=z", B64(z.AsSpan(0, z.Length / 2)));
        Assert.Single(rig.Replies);
        Assert.StartsWith("\u001b_Gi=1;E", rig.Replies[0]);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void Compressed_WithoutAnyPayloadIsASizeMismatch()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24,o=z");
        Assert.Equal(new[] { Err("i=1", "EINVAL:Image data size does not match declared size") }, rig.Replies);
    }

    [Fact]
    public void Compressed_ZipBombNeverWritesOrAllocatesBeyondTheDeclaredSize()
    {
        byte[] bomb = TestPng.Zlib(new byte[48 * 1024 * 1024], CompressionLevel.SmallestSize);
        Assert.True(bomb.Length < 200_000); // sanity: it is a real bomb
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=1,s=100,v=100,f=24,o=z", B64(bomb));
            Assert.Equal(new[] { Err("i=1", "EINVAL:Image data size does not match declared size") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.True(baseline.AllocatedDelta <= 100 * 100 * 3, $"allocated {baseline.AllocatedDelta}");
        }
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------------------------ base64 / padding

    [Fact]
    public void Padding_IsOptionalAndAcceptedAtTheEndOfTheFinalChunk()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=32", "AAAAAA==");
        rig.Apc("i=2,s=1,v=1,f=32", "AAAAAA");
        rig.Apc("i=3,s=1,v=1,f=24", "AAAA");
        Assert.Equal(new[] { Ok("i=1"), Ok("i=2"), Ok("i=3") }, rig.Replies);
    }

    [Theory]
    [InlineData("AA!A")]      // byte outside the alphabet
    [InlineData("AA AA")]     // whitespace is not tolerated
    [InlineData("AA\nAA")]
    [InlineData("A")]         // lone character cannot form a byte
    [InlineData("AA=A")]      // data after padding
    [InlineData("AAAA====")]  // padding without a partial quartet
    [InlineData("AAA=AAAA")]  // padding in the middle
    public void InvalidBase64_IsOneInvalidBase64Reply(string payload)
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24", payload);
        Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid base64 data") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void Padding_InANonFinalChunkIsAnError()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24,m=1", "AA==");
        Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid base64 data") }, rig.Replies);
        rig.Apc("m=0", "AAAAAAAA");
        Assert.Single(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void Base64Error_InTheMiddleOfAChunkedLoadDiscardsTheRestSilently()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=9,p=4,s=3,v=1,f=24,o=z,m=1", "AAAA");
            rig.Apc("m=1", "A!AA");
            // Exactly one reply, carrying the ids of the FIRST chunk.
            Assert.Equal(new[] { Err("i=9,p=4", "EINVAL:Invalid base64 data") }, rig.Replies);
            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, baseline.PendingDelta);

            rig.Apc("m=1", "AAAA");
            rig.Apc("m=0", "AAAA");
            Assert.Single(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ErrorInTheFirstChunkOfAChunkedLoadIsAnsweredOnceAndTheRestIsDropped()
    {
        using var rig = new KittyRig();
        rig.Apc("i=9,s=1,v=1,f=7,m=1", "AAAA");
        rig.Apc("m=1", "AAAA");
        rig.Apc("m=0", "AAAA");
        Assert.Equal(new[] { Err("i=9", "EINVAL:Unknown image format") }, rig.Replies);
    }

    // ---------------------------------------------------------------------------------------------- chunking

    [Fact]
    public void ThreeChunksWithSplitsInsideBase64QuartetsRoundTrip()
    {
        using var rig = new KittyRig();
        byte[] data = Enumerable.Range(1, 21).Select(v => (byte)v).ToArray(); // 28 base64 chars, no padding
        string b64 = B64(data);
        rig.Apc("i=9,s=7,v=1,f=24,m=1", b64[..5]);   // 1 char into the second quartet
        rig.Apc("m=1", b64[5..13]);                    // 3 chars into the fourth quartet
        Assert.Empty(rig.Replies);                     // no OK for intermediate chunks
        Assert.True(rig.Controller.HasOpenTransfer);
        Assert.Equal(0, rig.Store.Count);
        rig.Apc("m=0", b64[13..]);

        Assert.Equal(new[] { Ok("i=9") }, rig.Replies);
        Assert.False(rig.Controller.HasOpenTransfer);
        Assert.Equal(data, StoredPixels(rig, 9));
    }

    [Fact]
    public void ALaterChunkWithoutMIsTheLastChunk()
    {
        using var rig = new KittyRig();
        string b64 = B64(Enumerable.Range(1, 6).Select(v => (byte)v).ToArray());
        rig.Apc("i=1,s=2,v=1,f=24,m=1", b64[..4]);
        rig.Apc("", b64[4..]); // empty control: only the payload; m absent = last
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void PayloadSlicedIntoSingleCharactersWithinOneApcRoundTrips()
    {
        using var rig = new KittyRig();
        byte[] data = RandomBytes(6 * 3 * 3, 11);
        rig.Apc("i=1,s=6,v=3,f=24", B64(data).Select(c => c.ToString()).ToArray());
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(data, StoredPixels(rig, 1));
    }

    [Fact]
    public void ChunksOfAPngWithEveryChunkSizeFromOneUpGiveTheSameImage()
    {
        byte[] png = TestPng.Build(3, 2, RandomBytes(3 * 2 * 4, 12));
        string b64 = B64(png).TrimEnd('='); // padding may not be split across chunks; it is optional at the very end
        byte[]? reference = null;
        foreach (int size in new[] { 1, 2, 3, 5, 7, 40, 64, b64.Length })
        {
            using var rig = new KittyRig();
            for (int pos = 0; pos < b64.Length; pos += size)
            {
                string part = b64.Substring(pos, Math.Min(size, b64.Length - pos));
                bool last = pos + size >= b64.Length;
                rig.Apc(pos == 0 ? $"i=1,f=100,m={(last ? 0 : 1)}" : $"m={(last ? 0 : 1)}", part);
            }
            Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
            var pixels = StoredPixels(rig, 1);
            reference ??= pixels;
            Assert.Equal(reference, pixels);
        }
    }

    [Fact]
    public void ApcSplitAtEveryByteOffsetBetweenFeedCallsGivesTheSameImageAndReply()
    {
        byte[] data = { 1, 2, 3, 4, 5, 6 };
        byte[] apc = ApcBytes("i=3,s=2,v=1,f=24", B64(data));
        for (int cut = 0; cut <= apc.Length; cut++)
        {
            var replies = new List<string>();
            using var adapter = NewAdapter(replies: replies);
            var parser = new BasicAnsiParser { Handler = adapter };
            parser.Feed(apc.AsSpan(0, cut));
            parser.Feed(apc.AsSpan(cut));

            Assert.Equal(new[] { Ok("i=3") }, replies);
            Assert.Equal(data, adapter.KittyGraphics!.Store.FindByClientId(3)!.Root.AsSpan().ToArray());
        }
    }

    [Fact]
    public void AChunkedLoadIsAbortedByADeleteCommand()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=4,s=2,v=1,f=24,o=z,m=1", "AAAA");
            Assert.True(baseline.PendingDelta > 0);
            rig.Apc("a=d,d=a");
            Assert.False(rig.Controller.HasOpenTransfer);
            Assert.Equal(0, baseline.PendingDelta);

            rig.Apc("m=0", "AAAA"); // the final chunk is now a stray
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void AChunkedLoadIsAbortedByAnotherTransmission()
    {
        using var rig = new KittyRig();
        rig.Apc("i=4,s=2,v=1,f=24,m=1", "AAAA");
        rig.Apc("i=5,s=1,v=1,f=24", "AAAA");
        rig.Apc("m=0", "AAAA");

        Assert.Equal(new[] { Ok("i=5") }, rig.Replies);
        Assert.Null(rig.Store.FindByClientId(4));
        Assert.NotNull(rig.Store.FindByClientId(5));
    }

    [Fact]
    public void AChunkedLoadIsAbortedByAQuery()
    {
        using var rig = new KittyRig();
        rig.Apc("i=4,s=2,v=1,f=24,m=1", "AAAA");
        rig.Apc("i=5,s=1,v=1,f=24,a=q", "AAAA");
        rig.Apc("m=0", "AAAA");
        Assert.Equal(new[] { Ok("i=5") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void ACancelledChunkKillsTheWholeLoad()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc("i=4,s=2,v=1,f=24,o=z,m=1", "AAAA");
            rig.CancelledApc("m=1", "AAAA");  // CAN/SUB inside the second chunk
            Assert.False(rig.Controller.HasOpenTransfer);
            rig.Apc("m=0", "AAAA");
            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.Equal(0, baseline.PendingDelta);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ACancelledFirstChunkAnswersNothingEvenWhenItAlreadyFailed()
    {
        using var rig = new KittyRig();
        rig.CancelledApc("i=4,f=7", "AAAA");
        rig.CancelledApc("i=5,s=1,v=1,f=24", "AAAA");
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Theory]
    [InlineData("\u001b[?1049h")]
    [InlineData("\u001b[?47h")]
    [InlineData("\u001b[?1047h")]
    public void AChunkedLoadIsAbortedByAnAlternateScreenSwitch(string switchSequence)
    {
        var replies = new List<string>();
        using var adapter = NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=2,v=1,f=24,m=1", "AAAA"));
        Assert.True(adapter.KittyGraphics!.HasOpenTransfer);
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(switchSequence));
        Assert.False(adapter.KittyGraphics.HasOpenTransfer);
        parser.Feed(ApcBytes("m=0", "AAAA"));

        Assert.Empty(replies);
        Assert.Equal(0, adapter.KittyGraphics.Store.Count);
    }

    [Fact]
    public void ARedundantAlternateScreenCommandDoesNotAbortTheLoad()
    {
        var replies = new List<string>();
        using var adapter = NewAdapter(replies: replies);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(ApcBytes("i=1,s=2,v=1,f=24,m=1", "AAAA"));
        parser.Feed(System.Text.Encoding.ASCII.GetBytes("\u001b[?1049l")); // already on the main screen
        parser.Feed(ApcBytes("m=0", "AAAA"));
        Assert.Equal(new[] { Ok("i=1") }, replies);
    }

    // ------------------------------------------------------------------------------- quiet and later-chunk keys

    [Fact]
    public void AnOkIsOnlySentAfterTheDataIsLoaded_NeverForIntermediateChunks()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=3,v=1,f=24,m=1", "AAAA");
        rig.Apc("m=1", "AAAA");
        Assert.Empty(rig.Replies);
        rig.Apc("m=0", "AAAA");
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void ALaterChunksQuietOverridesTheFirstOne()
    {
        using var rig = new KittyRig();
        // q=2 on the first chunk, q=0 on the last: the OK is sent.
        rig.Apc("i=1,s=2,v=1,f=24,q=2,m=1", "AAAA");
        rig.Apc("m=0,q=0", "AAAA");
        // q=0 first, q=1 last: the OK is suppressed.
        rig.Apc("i=2,s=2,v=1,f=24,m=1", "AAAA");
        rig.Apc("m=0,q=1", "AAAA");
        // q=2 on the first chunk, no q later: stays quiet even for errors.
        rig.Apc("i=3,s=2,v=1,f=24,q=2,m=1", "AAAA");
        rig.Apc("m=1", "A!AA");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.NotNull(rig.Store.FindByClientId(2));
        Assert.Null(rig.Store.FindByClientId(3));
    }

    // ----------------------------------------------------------------------------------- exact error replies

    [Theory]
    [InlineData("i=1,s=1,v=1,f=7", "AAAA", "EINVAL:Unknown image format")]
    [InlineData("i=1,s=1,v=1,f=0", "AAAA", "EINVAL:Unknown image format")]
    [InlineData("i=1,s=1,v=1,f=24,t=f", "AAAA", "EINVAL:Unsupported transmission type")]
    [InlineData("i=1,s=1,v=1,f=24,t=t", "AAAA", "EINVAL:Unsupported transmission type")]
    [InlineData("i=1,s=1,v=1,f=24,t=s", "AAAA", "EINVAL:Unsupported transmission type")]
    [InlineData("i=1,s=1,v=1,f=24,t=x", "AAAA", "EINVAL:Unsupported transmission type")]
    [InlineData("i=1,s=1,v=1,f=24,o=q", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=1,s=0,v=1,f=24", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=1,s=1,v=0,f=32", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=1,f=24", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=1,s=99999999999,v=1,f=24", "AAAA", "EINVAL:Invalid key value")]
    [InlineData("i=1,s=10001,v=1,f=24", "AAAA", "EFBIG:Image too large")]
    [InlineData("i=1,s=1,v=10001,f=32", "AAAA", "EFBIG:Image too large")]
    [InlineData("i=1,s=4294967295,v=4294967295,f=32", "AAAA", "EFBIG:Image too large")]
    [InlineData("i=1,s=10000,v=10000,f=32", "AAAA", "ENOSPC:Storage quota exceeded")]
    [InlineData("i=1,s=2,v=1,f=24", "AAAA", "ENODATA:Insufficient image data")]
    [InlineData("i=1,s=1,v=1,f=24", "", "ENODATA:Insufficient image data")]
    [InlineData("i=1,s=2,v=2,f=32", "AAAAAAAA", "ENODATA:Insufficient image data")]
    [InlineData("i=1,s=1,v=1,f=24", "AAAAAAAA", "EFBIG:Payload too large")]
    [InlineData("i=1,s=1,v=1,f=24", "AAAAAAAAAAAAAAAAAAAAAAAA", "EFBIG:Payload too large")]
    [InlineData("i=1,s=1,v=1,f=24", "AAA!", "EINVAL:Invalid base64 data")]
    public void ErrorRepliesAreExact_AndNothingIsStoredOrLeftBehind(string control, string payload, string message)
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            rig.Apc(control, payload);
            Assert.Equal(new[] { Err("i=1", message) }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ValidationFailuresPrecedeAnyAllocation_AHugeHeaderWithATinyPayloadAllocatesNothing()
    {
        var baseline = new GraphicsBaseline();
        using var rig = new KittyRig();
        rig.Apc("i=1,s=10000,v=10000,f=32", B64(new byte[75]));
        rig.Apc("i=2,s=10000,v=10000,f=32,o=z", B64(new byte[75]));
        Assert.Equal(new[]
        {
            Err("i=1", "ENOSPC:Storage quota exceeded"),
            Err("i=2", "ENOSPC:Storage quota exceeded"),
        }, rig.Replies);
        Assert.Equal(0, baseline.AllocatedDelta);
        Assert.Equal(0, baseline.PendingDelta);
    }

    [Fact]
    public void PixelMemoryIsOnlyAllocatedWhenPayloadBytesArrive()
    {
        var baseline = new GraphicsBaseline();
        using var rig = new KittyRig();
        rig.Controller.OnBegin("i=1,s=200,v=200,f=32"u8);
        Assert.Equal(0, baseline.AllocatedDelta);   // keys validated, nothing allocated yet
        rig.Controller.OnEnd(true);                 // no data at all
        Assert.Equal(new[] { Err("i=1", "ENODATA:Insufficient image data") }, rig.Replies);
        Assert.Equal(0, baseline.AllocatedDelta);
    }

    [Fact]
    public void RawDestinationIsAllocatedOnceFromTheDeclaredSize()
    {
        var baseline = new GraphicsBaseline();
        using var rig = new KittyRig();
        rig.Apc("i=1,s=40,v=30,f=32,m=1", B64(new byte[1000]));
        rig.Apc("m=1", B64(new byte[1000]));
        Assert.Equal(40 * 30 * 4, baseline.AllocatedDelta);
        Assert.Equal(40 * 30 * 4, baseline.LiveBytesDelta);
        Assert.Equal(0, baseline.PendingDelta); // raw loads need no accumulation buffer
    }

    [Fact]
    public void ErrorInOneApcDoesNotDisturbTheNext()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=7", "AAAA");
        rig.Apc("i=2,s=1,v=1,f=24", "AAAA");
        Assert.Equal(new[] { Err("i=1", "EINVAL:Unknown image format"), Ok("i=2") }, rig.Replies);
    }

    // -------------------------------------------------------------------------------------- limits boundaries

    [Fact]
    public void QuotaBoundary_ExactlyTheLimitIsStored_OneByteLessIsNoSpace()
    {
        string payload = B64(new byte[12]);
        using (var ok = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 12 }))
        {
            ok.Apc("i=1,s=3,v=1,f=32", payload);
            Assert.Equal(new[] { Ok("i=1") }, ok.Replies);
        }
        using (var tooSmall = new KittyRig(new KittyGraphicsOptions { Enabled = true, StorageLimitBytes = 11 }))
        {
            tooSmall.Apc("i=1,s=3,v=1,f=32", payload);
            Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, tooSmall.Replies);
            Assert.Equal(0, tooSmall.Store.Count);
        }
    }

    [Fact]
    public void PayloadCapBoundary_ForAccumulatedPayloads()
    {
        byte[] z = TestPng.Zlib(RandomBytes(16, 5), CompressionLevel.NoCompression);
        string payload = B64(z);

        using (var exact = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = z.Length }))
        {
            exact.Apc("i=1,s=2,v=2,f=32,o=z", payload);
            Assert.Equal(new[] { Ok("i=1") }, exact.Replies);
        }
        using (var over = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = z.Length - 1 }))
        {
            over.Apc("i=1,s=2,v=2,f=32,o=z", payload);
            Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, over.Replies);
            Assert.Equal(0, over.Store.Count);
        }
    }

    [Fact]
    public void PayloadCapBoundary_ForAccumulatedPayloadsWithUnpaddedFinalQuartet()
    {
        // With and without the optional trailing "=" padding the cap applies to the decoded size, exactly.
        byte[] png = TestPng.Build(1, 1, new byte[4]);
        string padded = B64(png);
        string unpadded = padded.TrimEnd('=');
        foreach (string payload in new[] { padded, unpadded })
        {
            using var exact = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = png.Length });
            exact.Apc("i=1,f=100", payload);
            Assert.Equal(new[] { Ok("i=1") }, exact.Replies);
            using var over = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = png.Length - 1 });
            over.Apc("i=1,f=100", payload);
            Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, over.Replies);
        }
    }

    [Fact]
    public void PayloadCapBoundary_ForUncompressedRawPayloads_IsCheckedUpFront()
    {
        string payload = B64(new byte[400]);
        using (var exact = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = 400 }))
        {
            exact.Apc("i=1,s=10,v=10,f=32", payload);
            Assert.Equal(new[] { Ok("i=1") }, exact.Replies);
        }
        using (var over = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxPayloadBytes = 399 }))
        {
            over.Apc("i=1,s=10,v=10,f=32"); // no payload needed: the declared 400 bytes already exceed the cap
            Assert.Equal(new[] { Err("i=1", "EFBIG:Payload too large") }, over.Replies);
        }
    }

    [Fact]
    public void ConfiguredMaxImageDimensionIsEnforcedInclusively()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true, MaxImageDimension = 4 });
        rig.Apc("i=1,s=4,v=1,f=24", B64(new byte[12]));
        rig.Apc("i=2,s=5,v=1,f=24", B64(new byte[15]));
        rig.Apc("i=3,f=100", B64(TestPng.Build(5, 1, new byte[20])));
        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=2", "EFBIG:Image too large"),
            Err("i=3", "EFBIG:Image too large"),
        }, rig.Replies);
    }

    // ------------------------------------------------------------------------------------------ heavy phase

    [Fact]
    public void LargeDecodeRunsWithTheBufferLockReleasedAndReacquired()
    {
        using var rig = new KittyRig();
        bool? lockHeldInsideDecoder = null;
        rig.Decoder.BeforeDecode = (_, _) => lockHeldInsideDecoder = Monitor.IsEntered(rig.Host.SyncRoot);
        byte[] png = TestPng.Build(256, 256, new byte[256 * 256 * 4]); // 262144 decoded bytes = the threshold

        lock (rig.Host.SyncRoot)
        {
            rig.Apc("i=1,f=100", B64(png));
            Assert.True(Monitor.IsEntered(rig.Host.SyncRoot));
        }

        Assert.False(lockHeldInsideDecoder);
        Assert.Equal(1, rig.Host.ReleaseAttempts);
        Assert.Equal(1, rig.Host.ReacquireCalls);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(256 * 256 * 4, rig.Store.FindByClientId(1)!.Root.ByteLength);
    }

    [Fact]
    public void DecodeJustBelowTheThresholdKeepsTheLock()
    {
        using var rig = new KittyRig();
        bool? lockHeldInsideDecoder = null;
        rig.Decoder.BeforeDecode = (_, _) => lockHeldInsideDecoder = Monitor.IsEntered(rig.Host.SyncRoot);
        byte[] png = TestPng.Build(255, 256, new byte[255 * 256 * 4]); // 261120 bytes

        lock (rig.Host.SyncRoot)
            rig.Apc("i=1,f=100", B64(png));

        Assert.True(lockHeldInsideDecoder);
        Assert.Equal(0, rig.Host.ReleaseAttempts);
        Assert.Equal(0, rig.Host.ReacquireCalls);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Theory]
    [InlineData("i=1,s=256,v=256,f=32", false, 1)]   // premultiply of exactly 256 KiB
    [InlineData("i=1,s=128,v=512,f=32", false, 1)]
    [InlineData("i=1,s=255,v=256,f=32", false, 0)]   // 261120 bytes
    [InlineData("i=1,s=600,v=600,f=24", false, 0)]   // f=24 raw needs no CPU work at all
    [InlineData("i=1,s=300,v=300,f=24,o=z", true, 1)] // inflate of 270000 bytes
    [InlineData("i=1,s=100,v=100,f=24,o=z", true, 0)]
    public void HeavyPhaseThresholdIs256KiBOfDecodedBytes(string control, bool compress, int expectedReleases)
    {
        using var rig = new KittyRig();
        int bpp = control.Contains("f=24") ? 3 : 4;
        int w = int.Parse(control.Split(',')[1][2..]);
        int h = int.Parse(control.Split(',')[2][2..]);
        byte[] raw = new byte[w * h * bpp];
        string payload = B64(compress ? TestPng.Zlib(raw) : raw);

        lock (rig.Host.SyncRoot)
            rig.Apc(control, payload);

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(expectedReleases, rig.Host.ReleaseAttempts);
        Assert.Equal(expectedReleases, rig.Host.ReacquireCalls);
    }

    [Fact]
    public void WithoutTheBufferLockHeldTheHeavyPhaseSimplyRuns()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=256,v=256,f=32", B64(new byte[256 * 256 * 4]));
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(1, rig.Host.ReleaseAttempts);
        Assert.Equal(0, rig.Host.ReacquireCalls); // nothing was released, so nothing is re-acquired
    }

    // ---------------------------------------------------------------------------------------------- helpers

    internal static TerminalAdapter NewAdapter(FakeImageDecoder? decoder = null, KittyGraphicsOptions? options = null, List<string>? replies = null)
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 20);
        adapter.ConfigureGraphics(options ?? new KittyGraphicsOptions { Enabled = true }, decoder ?? new FakeImageDecoder());
        if (replies != null)
            adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return adapter;
    }
}
