using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>A scripted <see cref="IKittyMediumReader"/> that records every call.</summary>
internal sealed class FakeMediumReader : IKittyMediumReader
{
    public List<(char Medium, byte[] Path, long Offset, long Size, long MaxBytes, KittyMediaFlags Allowed)> Calls { get; } = new();

    /// <summary>Produces the result; the default is the single failure outcome.</summary>
    public Func<KittyMediumReadResult>? Handler { get; set; }

    public KittyMediumReadResult Read(char medium, ReadOnlySpan<byte> pathUtf8, long offset, long size, long maxBytes, KittyMediaFlags allowed)
    {
        Calls.Add((medium, pathUtf8.ToArray(), offset, size, maxBytes, allowed));
        return Handler?.Invoke() ?? KittyMediumReadResult.BadFile;
    }

    /// <summary>A successful result owning a pooled copy of <paramref name="data"/>.</summary>
    public static KittyMediumReadResult Ok(byte[] data, bool exceedsCap = false)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(rented, 0);
        return KittyMediumReadResult.Success(rented, data.Length, exceedsCap);
    }
}

/// <summary>Controller + fake host + decoder with a chosen medium reader and options (all media enabled by default).</summary>
internal sealed class KittyMediaRig : IDisposable
{
    public const KittyMediaFlags AllMedia = KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory;

    public FakeKittyHost Host { get; } = new();
    public FakeImageDecoder Decoder { get; } = new();
    public KittyGraphicsController Controller { get; }

    public KittyMediaRig(IKittyMediumReader? reader = null, KittyGraphicsOptions? options = null)
    {
        Controller = new KittyGraphicsController(Host, reader);
        Controller.Configure(options ?? new KittyGraphicsOptions { Enabled = true, Media = AllMedia }, Decoder);
    }

    public KittyImageStore Store => Controller.Store;
    public List<string> Replies => Host.Replies;

    public void Apc(string control, params string[] payloadSlices)
    {
        Controller.OnBegin(Encoding.ASCII.GetBytes(control));
        foreach (string slice in payloadSlices)
            Controller.OnPayload(Encoding.ASCII.GetBytes(slice));
        Controller.OnEnd(true);
    }

    public void CancelledApc(string control, params string[] payloadSlices)
    {
        Controller.OnBegin(Encoding.ASCII.GetBytes(control));
        foreach (string slice in payloadSlices)
            Controller.OnPayload(Encoding.ASCII.GetBytes(slice));
        Controller.OnEnd(false);
    }

    public byte[] Pixels(uint clientId)
    {
        var image = Store.FindByClientId(clientId);
        Assert.NotNull(image);
        return image!.Root.AsSpan().ToArray();
    }

    public void Dispose() => Controller.Dispose();
}

/// <summary>
/// <c>t=f</c>, <c>t=t</c> and <c>t=s</c> through the controller: the path payload, the medium read, and the same
/// size / compression / PNG rules as a direct payload. Real files live in per-test temp directories.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyTransferMediumTests
{
    private const string BadFileMessage = "EBADF:Failed to read image file";
    private const string UnsupportedMessage = "EINVAL:Unsupported transmission type";

    private static string PathB64(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

    private static byte[] Pattern(int length, int seed = 3)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 7 + seed);
        return data;
    }

    // ------------------------------------------------------------------------------------------ t=f round trips

    [Fact]
    public void FileF24_RoundTripKeepsExactBytesAndLeavesTheFile()
    {
        var baseline = new GraphicsBaseline();
        using (var dir = new KittyMediaTempDir())
        using (var rig = new KittyMediaRig())
        {
            byte[] data = { 255, 0, 0, 0, 255, 0 };
            string path = dir.Write("a.rgb", data);
            rig.Apc("i=5,s=2,v=1,f=24,t=f", PathB64(path));

            Assert.Equal(new[] { Ok("i=5") }, rig.Replies);
            var image = rig.Store.FindByClientId(5)!;
            Assert.Equal(ImagePixelFormat.Rgb8, image.Root.Format);
            Assert.Equal(data, image.Root.AsSpan().ToArray());
            Assert.True(File.Exists(path), "t=f never deletes");
            Assert.Equal(6, rig.Store.UsedBytes);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void FileF32_IsPremultipliedLikeADirectPayload()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("a.rgba", new byte[] { 200, 100, 50, 128 });
        rig.Apc("i=1,s=1,v=1,f=32,t=f", PathB64(path));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, rig.Pixels(1));
    }

    [Fact]
    public void FilePng_GoesThroughTheDecoder()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        byte[] png = TestPng.Build(2, 1, new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 });
        string path = dir.Write("a.png", png);
        rig.Apc("i=2,f=100,t=f", PathB64(path));

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        Assert.Equal(1, rig.Decoder.Calls);
        var image = rig.Store.FindByClientId(2)!;
        Assert.Equal(2, image.Root.Width);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, image.Root.AsSpan().ToArray());
    }

    [Fact]
    public void FilePngWithGarbageContentFailsAsBadPng()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("a.png", Pattern(200));
        rig.Apc("i=2,f=100,t=f", PathB64(path));

        Assert.Equal(new[] { Err("i=2", "EBADPNG:Invalid PNG data") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void FileCompressedRaw_IsInflatedLikeADirectPayload()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("a.z", TestPng.Zlib(new byte[] { 1, 2, 3 }));
        rig.Apc("i=3,s=1,v=1,f=24,o=z,t=f", PathB64(path));

        Assert.Equal(new[] { Ok("i=3") }, rig.Replies);
        Assert.Equal(new byte[] { 1, 2, 3 }, rig.Pixels(3));
    }

    [Fact]
    public void FileCompressedRaw_WrongInflatedSizeAndNonZlibContentAreDistinguished()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string good = dir.Write("a.z", TestPng.Zlib(new byte[] { 1, 2, 3 }));
        string garbage = dir.Write("b.z", Pattern(64));
        rig.Apc("i=1,s=2,v=1,f=24,o=z,t=f", PathB64(good));       // inflates to 3 bytes, 6 declared
        rig.Apc("i=2,s=1,v=1,f=24,o=z,t=f", PathB64(garbage));

        Assert.Equal(new[]
        {
            Err("i=1", "EINVAL:Image data size does not match declared size"),
            Err("i=2", "EILSEQ:Failed to inflate image data"),
        }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void FileCompressedPng_IsRefusedBecauseSIsTheReadLength()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("a.z", TestPng.Zlib(TestPng.Build(1, 1, new byte[] { 1, 2, 3, 255 })));
        rig.Apc("i=1,f=100,o=z,S=40,t=f", PathB64(path));

        Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid key value") }, rig.Replies);
    }

    [Fact]
    public void FileLargeEnoughToRunUnlocked_RoundTripsAndReleasesTheLockForTheReadAndTheWork()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        byte[] data = Pattern(256 * 256 * 4);
        for (int i = 3; i < data.Length; i += 4) data[i] = 255; // opaque: premultiplication is the identity
        string path = dir.Write("big.rgba", data);
        rig.Apc("i=9,s=256,v=256,f=32,t=f", PathB64(path));

        Assert.Equal(new[] { Ok("i=9") }, rig.Replies);
        Assert.Equal(data, rig.Pixels(9));
        Assert.True(rig.Host.ReleaseAttempts >= 2, "read and premultiply both ask for the lock to be released");
    }

    [Fact]
    public void FileQuery_AnswersWithoutStoringAndKeepsTheFile()
    {
        var baseline = new GraphicsBaseline();
        using (var dir = new KittyMediaTempDir())
        using (var rig = new KittyMediaRig())
        {
            string path = dir.Write("q.rgb", new byte[] { 1, 2, 3 });
            rig.Apc("i=4,a=q,s=1,v=1,f=24,t=f", PathB64(path));

            Assert.Equal(new[] { Ok("i=4") }, rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.True(File.Exists(path));
        }
        baseline.AssertRestored();
    }

    // -------------------------------------------------------------------------------------------- S / O handling

    [Fact]
    public void OffsetAndSizeSelectTheRangeExactly()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        byte[] file = new byte[15];
        new byte[] { 90, 91, 92, 93 }.CopyTo(file, 0);
        new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(file, 4);
        new byte[] { 99, 98, 97, 96, 95 }.CopyTo(file, 10);
        string path = dir.Write("r.bin", file);
        rig.Apc("i=1,s=2,v=1,f=24,t=f,O=4,S=6", PathB64(path));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, rig.Pixels(1));
    }

    [Fact]
    public void SizeZeroReadsToTheEndOfTheFile()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("r.bin", new byte[] { 9, 9, 9, 1, 2, 3, 4, 5, 6 });
        rig.Apc("i=1,s=2,v=1,f=24,t=f,O=3", PathB64(path));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, rig.Pixels(1));
    }

    [Fact]
    public void FileLongerThanTheImageIsTooLarge_AndShorterIsInsufficient()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        rig.Apc("i=1,s=2,v=1,f=24,t=f", PathB64(dir.Write("long.bin", Pattern(7))));
        rig.Apc("i=2,s=2,v=1,f=24,t=f", PathB64(dir.Write("short.bin", Pattern(5))));
        rig.Apc("i=3,s=2,v=1,f=24,t=f", PathB64(dir.Write("exact.bin", Pattern(6))));

        Assert.Equal(new[]
        {
            Err("i=1", "EFBIG:Payload too large"),
            Err("i=2", "ENODATA:Insufficient image data"),
            Ok("i=3"),
        }, rig.Replies);
        Assert.Equal(1, rig.Store.Count);
    }

    [Fact]
    public void OffsetAtOrBeyondTheEndAndSizeBeyondTheFileAreTheSingleFileError()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("r.bin", Pattern(6));
        rig.Apc("i=1,s=2,v=1,f=24,t=f,O=6", PathB64(path));
        rig.Apc("i=2,s=2,v=1,f=24,t=f,O=100", PathB64(path));
        rig.Apc("i=3,s=2,v=1,f=24,t=f,S=7", PathB64(path));
        rig.Apc("i=4,s=2,v=1,f=24,t=f,O=3,S=4", PathB64(path));

        Assert.Equal(new[]
        {
            Err("i=1", BadFileMessage), Err("i=2", BadFileMessage), Err("i=3", BadFileMessage), Err("i=4", BadFileMessage),
        }, rig.Replies);
    }

    [Fact]
    public void FourGibOffsetAndSizeReachTheReaderUnchanged()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader, new KittyGraphicsOptions { Enabled = true, Media = KittyMediaRig.AllMedia, MaxPayloadBytes = 1000 });
        rig.Apc("i=1,s=1,v=1,f=24,t=f,O=4294967295,S=4294967295", PathB64("/x"));

        var call = Assert.Single(reader.Calls);
        Assert.Equal('f', call.Medium);
        Assert.Equal(4294967295L, call.Offset);
        Assert.Equal(4294967295L, call.Size);
        Assert.Equal(1000, call.MaxBytes);
        Assert.Equal(KittyMediaRig.AllMedia, call.Allowed);
        Assert.Equal(new[] { Err("i=1", BadFileMessage) }, rig.Replies);
    }

    [Fact]
    public void FourGibSizeWithARealFileIsTheSingleFileError()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("r.bin", Pattern(6));
        rig.Apc("i=1,s=2,v=1,f=24,t=f,S=4294967295", PathB64(path));
        rig.Apc("i=2,s=2,v=1,f=24,t=f,O=4294967295", PathB64(path));

        Assert.Equal(new[] { Err("i=1", BadFileMessage), Err("i=2", BadFileMessage) }, rig.Replies);
    }

    // ------------------------------------------------------------------------------------ single error / policy

    [Fact]
    public void EveryFileFailureClassGivesTheIdenticalReply()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string ok = dir.Write("ok.bin", Pattern(6));
        string[] bad =
        {
            dir.Combine("missing.bin"),            // does not exist
            dir.Root,                              // a directory
            "relative/file.bin",                   // not absolute
            "/dev/zero",                           // denied location
            ok + "/",                              // trailing separator
        };
        for (int i = 0; i < bad.Length; i++)
            rig.Apc("i=" + (i + 1) + ",s=2,v=1,f=24,t=f", PathB64(bad[i]));
        rig.Apc("i=20,s=2,v=1,f=24,t=f");                                       // no path at all
        rig.Apc("i=21,s=2,v=1,f=24,t=f", Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x41 })); // not UTF-8

        var expected = new List<string>();
        for (int i = 0; i < bad.Length; i++)
            expected.Add(Err("i=" + (i + 1), BadFileMessage));
        expected.Add(Err("i=20", BadFileMessage));
        expected.Add(Err("i=21", BadFileMessage));
        Assert.Equal(expected, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void ReaderExceptionAndOversizedReadAreReportedWithoutLeakingDetail()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);

        reader.Handler = () => throw new IOException("secret detail");
        rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));
        reader.Handler = () => FakeMediumReader.Ok(new byte[10], exceedsCap: true);
        rig.Apc("i=2,s=1,v=1,f=24,t=f", PathB64("/x"));

        Assert.Equal(new[] { Err("i=1", BadFileMessage), Err("i=2", "EFBIG:Payload too large") }, rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void PathLengthBoundary_2048BytesReachTheReader_2049DoNot()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);

        rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/" + new string('a', KittyTransfer.MaxPathBytes - 1)));
        Assert.Single(reader.Calls);
        Assert.Equal(KittyTransfer.MaxPathBytes, reader.Calls[0].Path.Length);

        rig.Apc("i=2,s=1,v=1,f=24,t=f", PathB64("/" + new string('a', KittyTransfer.MaxPathBytes)));
        rig.Apc("i=3,s=1,v=1,f=24,t=f", PathB64("/" + new string('a', 200_000)));
        Assert.Single(reader.Calls);
        Assert.Equal(new[]
        {
            Err("i=1", BadFileMessage), Err("i=2", BadFileMessage), Err("i=3", BadFileMessage),
        }, rig.Replies);
    }

    [Fact]
    public void AnOversizedPathNeverBuffersMoreThanTheCap()
    {
        var baseline = new GraphicsBaseline();
        long peak = 0;
        var reader = new FakeMediumReader();
        using (var rig = new KittyMediaRig(reader))
        {
            rig.Controller.OnBegin(Encoding.ASCII.GetBytes("i=1,s=1,v=1,f=24,t=f"));
            string slice = new string('A', 4096);
            for (int i = 0; i < 2000; i++)
            {
                rig.Controller.OnPayload(Encoding.ASCII.GetBytes(slice));
                peak = Math.Max(peak, baseline.PendingDelta);
            }
            rig.Controller.OnEnd(true);
        }
        Assert.True(peak <= 64 * 1024, "pending transfer bytes stay small: " + peak);
        Assert.Empty(reader.Calls);
        baseline.AssertRestored();
    }

    [Fact]
    public void EmptyPathNeverCallsTheReader()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);
        rig.Apc("i=1,s=1,v=1,f=24,t=f");
        Assert.Empty(reader.Calls);
        Assert.Equal(new[] { Err("i=1", BadFileMessage) }, rig.Replies);
    }

    // ---------------------------------------------------------------------------------------------- media flags

    [Theory]
    [InlineData('f', KittyMediaFlags.Direct | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory)]
    [InlineData('t', KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.SharedMemory)]
    [InlineData('s', KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile)]
    [InlineData('f', KittyMediaFlags.Direct)]
    [InlineData('s', KittyMediaFlags.None)]
    public void DisabledMedium_AnswersUnsupportedWithExactBytes_AndNeverTouchesTheReader(char medium, KittyMediaFlags flags)
    {
        var reader = new FakeMediumReader { Handler = () => FakeMediumReader.Ok(new byte[3]) };
        using var rig = new KittyMediaRig(reader, new KittyGraphicsOptions { Enabled = true, Media = flags });
        rig.Apc("i=7,s=1,v=1,f=24,t=" + medium, PathB64("/tmp/x"));

        Assert.Equal(new[] { "\u001b_Gi=7;" + UnsupportedMessage + "\u001b\\" }, rig.Replies);
        Assert.Empty(reader.Calls);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Theory]
    [InlineData('f')]
    [InlineData('t')]
    [InlineData('s')]
    public void EnabledMediumThatThePlatformCannotServe_AnswersUnsupported(char medium)
    {
        var reader = new FakeMediumReader { Handler = () => KittyMediumReadResult.Unsupported };
        using var rig = new KittyMediaRig(reader);
        rig.Apc("i=7,s=1,v=1,f=24,t=" + medium, PathB64("/tmp/x"));

        Assert.Equal(new[] { Err("i=7", UnsupportedMessage) }, rig.Replies);
        var call = Assert.Single(reader.Calls);
        Assert.Equal(medium, call.Medium);
    }

    [Fact]
    public void UnknownMediumIsUnsupportedEvenWithEveryFlagOn()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);
        rig.Apc("i=1,s=1,v=1,f=24,t=x", PathB64("/x"));
        Assert.Equal(new[] { Err("i=1", UnsupportedMessage) }, rig.Replies);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public void DirectPayloadIsUnaffectedByMediaFlags()
    {
        using var rig = new KittyMediaRig(null, new KittyGraphicsOptions { Enabled = true, Media = KittyMediaFlags.None });
        rig.Apc("i=1,s=1,v=1,f=24,t=d", B64(new byte[] { 1, 2, 3 }));
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Theory]
    [InlineData('d', KittyMediaFlags.Direct, true)]
    [InlineData('f', KittyMediaFlags.Direct, false)]
    [InlineData('f', KittyMediaFlags.Direct | KittyMediaFlags.File, true)]
    [InlineData('t', KittyMediaFlags.Direct | KittyMediaFlags.File, false)]
    [InlineData('t', KittyMediaFlags.Direct | KittyMediaFlags.TempFile, true)]
    [InlineData('s', KittyMediaFlags.Direct | KittyMediaFlags.TempFile, false)]
    [InlineData('s', KittyMediaFlags.Direct | KittyMediaFlags.SharedMemory, true)]
    [InlineData('z', KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory, false)]
    public void MediumStatusFollowsTheFlags(char medium, KittyMediaFlags flags, bool accepted)
    {
        var status = KittyTransfer.MediumStatus(medium, new KittyGraphicsOptions { Media = flags });
        Assert.Equal(accepted ? KittyStatus.Ok : KittyStatus.UnsupportedMedium, status);
    }

    // ------------------------------------------------------------------------------------------------ t=t rules

    [Fact]
    public void TempFile_IsDeletedAfterASuccessfulRead_AndTheImageIsStored()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".rgb", new byte[] { 1, 2, 3, 4, 5, 6 });
        rig.Apc("i=1,s=2,v=1,f=24,t=t", PathB64(path));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, rig.Pixels(1));
    }

    [Fact]
    public void TempFile_FailedReadsKeepTheFile()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string unmarked = dir.Write("plain.rgb", new byte[] { 1, 2, 3, 4, 5, 6 });
        string marked = dir.Write("tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".rgb", new byte[] { 1, 2, 3, 4, 5, 6 });
        rig.Apc("i=1,s=2,v=1,f=24,t=t", PathB64(unmarked));        // no marker: denied
        rig.Apc("i=2,s=2,v=1,f=24,t=t,S=7", PathB64(marked));      // declared size beyond the file: read fails

        Assert.Equal(new[] { Err("i=1", BadFileMessage), Err("i=2", BadFileMessage) }, rig.Replies);
        Assert.True(File.Exists(unmarked));
        Assert.True(File.Exists(marked));
    }

    [Fact]
    public void TempFile_IsDeletedByTheReadEvenWhenTheContentFailsTheImageChecks()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".rgb", new byte[] { 1, 2, 3 });
        rig.Apc("i=1,s=2,v=1,f=24,t=t", PathB64(path));          // 3 bytes for 2x1 RGB

        Assert.Equal(new[] { Err("i=1", "ENODATA:Insufficient image data") }, rig.Replies);
        Assert.False(File.Exists(path), "the reader deletes after a successful read, whatever happens to the pixels");
    }

    // ------------------------------------------------------------------------------------------ chunked paths

    [Fact]
    public void PathMayArriveInChunks_OnlyTheFinalChunkIsAnswered()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("a-fairly-long-file-name-for-chunking.rgb", new byte[] { 7, 8, 9 });
        string b64 = PathB64(path);
        Assert.True(b64.Length > 80);
        string c1 = b64.Substring(0, 28), c2 = b64.Substring(28, 28), c3 = b64.Substring(56);

        rig.Apc("i=7,s=1,v=1,f=24,t=f,m=1", c1);
        Assert.Empty(rig.Replies);
        Assert.True(rig.Controller.HasOpenTransfer);
        rig.Apc("m=1", c2);
        Assert.Empty(rig.Replies);
        rig.Apc("m=0", c3);

        Assert.Equal(new[] { Ok("i=7") }, rig.Replies);
        Assert.Equal(new byte[] { 7, 8, 9 }, rig.Pixels(7));
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void PathMayBeSlicedAnywhereInsideOneApc()
    {
        using var dir = new KittyMediaTempDir();
        using var rig = new KittyMediaRig();
        string path = dir.Write("sliced.rgb", new byte[] { 7, 8, 9 });
        string b64 = PathB64(path);
        var slices = new List<string>();
        for (int i = 0; i < b64.Length; i += 5)
            slices.Add(b64.Substring(i, Math.Min(5, b64.Length - i)));
        rig.Apc("i=1,s=1,v=1,f=24,t=f", slices.ToArray());

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(new byte[] { 7, 8, 9 }, rig.Pixels(1));
    }

    [Fact]
    public void PathChunkAfterAnOverlongPathIsSwallowedSilently()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);
        rig.Apc("i=1,s=1,v=1,f=24,t=f,m=1", PathB64("/" + new string('a', 3000)));
        rig.Apc("m=1", "AAAA");
        rig.Apc("m=0", "AAAA");

        Assert.Equal(new[] { Err("i=1", BadFileMessage) }, rig.Replies);
        Assert.Empty(reader.Calls);
    }

    // ------------------------------------------------------------------- up-front checks and cancellation

    [Fact]
    public void DeclaredSizeIsCheckedBeforeAnyRead()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader, new KittyGraphicsOptions { Enabled = true, Media = KittyMediaRig.AllMedia, MaxPayloadBytes = 11 });
        rig.Apc("i=1,s=2,v=2,f=24,t=f", PathB64("/x"));   // 12 bytes > 11
        rig.Apc("i=2,s=0,v=2,f=24,t=f", PathB64("/x"));
        rig.Apc("i=3,s=100001,v=1,f=24,t=f", PathB64("/x"));

        Assert.Equal(new[]
        {
            Err("i=1", "EFBIG:Payload too large"),
            Err("i=2", "EINVAL:Invalid key value"),
            Err("i=3", "EFBIG:Image too large"),
        }, rig.Replies);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public void TheReadRunsWithTheBufferLockReleased()
    {
        var reader = new FakeMediumReader();
        using var rig = new KittyMediaRig(reader);
        bool heldDuringRead = true;
        reader.Handler = () =>
        {
            heldDuringRead = Monitor.IsEntered(rig.Host.SyncRoot);
            return FakeMediumReader.Ok(new byte[] { 1, 2, 3 });
        };
        lock (rig.Host.SyncRoot)
        {
            rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));
            Assert.True(Monitor.IsEntered(rig.Host.SyncRoot), "the lock is back after the read");
        }

        Assert.False(heldDuringRead);
        Assert.True(rig.Host.ReleaseAttempts >= 1);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void AbortDuringTheReadDropsTheResultSilently()
    {
        var baseline = new GraphicsBaseline();
        var reader = new FakeMediumReader();
        using (var rig = new KittyMediaRig(reader))
        {
            reader.Handler = () =>
            {
                rig.Controller.AbortTransfer();      // what another thread does under the buffer lock
                return FakeMediumReader.Ok(new byte[] { 1, 2, 3 });
            };
            rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));

            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
            Assert.False(rig.Controller.HasOpenTransfer);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void ControllerDisposedDuringTheReadStoresNothing()
    {
        var baseline = new GraphicsBaseline();
        var reader = new FakeMediumReader();
        using (var rig = new KittyMediaRig(reader))
        {
            reader.Handler = () =>
            {
                rig.Controller.Dispose();
                return FakeMediumReader.Ok(new byte[] { 1, 2, 3 });
            };
            rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));
            Assert.Empty(rig.Replies);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void HostDisposedDuringTheReadStoresNothing()
    {
        var baseline = new GraphicsBaseline();
        var reader = new FakeMediumReader();
        using (var rig = new KittyMediaRig(reader))
        {
            reader.Handler = () =>
            {
                rig.Host.Disposed = true;
                return FakeMediumReader.Ok(new byte[] { 1, 2, 3 });
            };
            rig.Apc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));

            Assert.Empty(rig.Replies);
            Assert.Equal(0, rig.Store.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void CancelledPathApcReadsNothing()
    {
        var reader = new FakeMediumReader { Handler = () => FakeMediumReader.Ok(new byte[] { 1, 2, 3 }) };
        using var rig = new KittyMediaRig(reader);
        rig.CancelledApc("i=1,s=1,v=1,f=24,t=f", PathB64("/x"));

        Assert.Empty(reader.Calls);
        Assert.Empty(rig.Replies);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void AdapterOwnsOneReaderForItsWholeLifetime()
    {
        using var adapter = new Dotty.Terminal.Adapter.TerminalAdapter(5, 20);
        Assert.Same(adapter.MediumReader, adapter.MediumReader);
    }
}
