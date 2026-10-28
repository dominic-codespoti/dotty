using System.Collections.Concurrent;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Graphics;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>A uniquely named directory under the system temp path that removes itself (and links, without following them).</summary>
internal sealed class KittyMediaTempDir : IDisposable
{
    public KittyMediaTempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "dotty-kitty-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(string name) => Path.Combine(Root, name);

    /// <summary>Creates (or overwrites) a file below the root, creating directories as needed.</summary>
    public string Write(string name, byte[]? data = null)
    {
        string path = Combine(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data ?? [1, 2, 3]);
        return path;
    }

    /// <summary>Creates a symbolic link; false when the platform or privileges do not allow it.</summary>
    public static bool TryLink(string link, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    public string RequireLink(string name, string target, bool directory = false)
    {
        string link = Combine(name);
        if (!TryLink(link, target, directory)) Assert.Skip("Symbolic links cannot be created here");
        return link;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class KittyFileMediumTests
{
    private const KittyMediaFlags AllMedia = KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory;
    private const long Cap = 1 << 20;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 7 + 3);
        return data;
    }

    private static KittyMediumReadResult ReadFile(KittyMediumReader reader, char medium, string path, long offset = 0, long size = 0, long cap = Cap, KittyMediaFlags allowed = AllMedia) =>
        reader.Read(medium, Utf8(path), offset, size, cap, allowed);

    private static void AssertBadFile(KittyMediumReadResult result)
    {
        Assert.Same(KittyMediumReadResult.BadFile, result);
        Assert.Equal(KittyStatus.BadFile, result.Status);
        Assert.False(result.IsOk);
        Assert.Equal(0, result.Length);
        Assert.True(result.Data.IsEmpty);
    }

    // ---- ReadRange (stream core) -------------------------------------------------------------------------

    /// <summary>A stream whose reported Length differs from its content: smaller = file that grew, larger = file that shrank.</summary>
    private sealed class LyingLengthStream(byte[] content, long claimedLength) : MemoryStream(content)
    {
        public override long Length => claimedLength;
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class ThrowingStream(byte[] content) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
    }

    [Fact]
    public void ReadRange_ExactBytesForSizeAndOffset()
    {
        byte[] data = Pattern(100);
        using var r = KittyFileMedium.ReadRange(new MemoryStream(data), offset: 10, size: 20, Cap);
        Assert.True(r.IsOk);
        Assert.False(r.ExceedsCap);
        Assert.Equal(20, r.Length);
        Assert.Equal(data.AsSpan(10, 20).ToArray(), r.Data.ToArray());
    }

    [Fact]
    public void ReadRange_SizeZeroReadsWholeFileFromOffset()
    {
        byte[] data = Pattern(100);
        using (var whole = KittyFileMedium.ReadRange(new MemoryStream(data), 0, 0, Cap))
        {
            Assert.True(whole.IsOk);
            Assert.Equal(data, whole.Data.ToArray());
        }
        using var tail = KittyFileMedium.ReadRange(new MemoryStream(data), 90, 0, Cap);
        Assert.True(tail.IsOk);
        Assert.Equal(data.AsSpan(90).ToArray(), tail.Data.ToArray());
    }

    [Fact]
    public void ReadRange_LastByteIsReadable()
    {
        byte[] data = Pattern(100);
        using var r = KittyFileMedium.ReadRange(new MemoryStream(data), 99, 1, Cap);
        Assert.True(r.IsOk);
        Assert.Equal(new[] { data[99] }, r.Data.ToArray());
    }

    [Theory]
    [InlineData(100, 0)]   // offset exactly at EOF, whole-file read
    [InlineData(100, 1)]   // offset exactly at EOF, sized read
    [InlineData(101, 0)]   // offset beyond EOF
    [InlineData(5000, 4)]
    public void ReadRange_OffsetAtOrBeyondEndFails(long offset, long size)
    {
        AssertBadFile(KittyFileMedium.ReadRange(new MemoryStream(Pattern(100)), offset, size, Cap));
    }

    [Theory]
    [InlineData(0, 101)]    // size larger than the file
    [InlineData(50, 51)]    // offset + size one beyond the end
    [InlineData(0, long.MaxValue)]
    public void ReadRange_FileSmallerThanDeclaredSizeFails(long offset, long size)
    {
        AssertBadFile(KittyFileMedium.ReadRange(new MemoryStream(Pattern(100)), offset, size, Cap));
    }

    [Fact]
    public void ReadRange_EmptySourceFails()
    {
        AssertBadFile(KittyFileMedium.ReadRange(new MemoryStream(), 0, 0, Cap));
        AssertBadFile(KittyFileMedium.ReadRange(new MemoryStream(), 0, 1, Cap));
    }

    [Theory]
    [InlineData(-1, 0, 10)]
    [InlineData(0, -1, 10)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, -5)]
    public void ReadRange_InvalidArgumentsFail(long offset, long size, long maxBytes)
    {
        AssertBadFile(KittyFileMedium.ReadRange(new MemoryStream(Pattern(10)), offset, size, maxBytes));
    }

    [Theory]
    [InlineData(100, 0, 10, 11, true)]    // whole file longer than the cap: cap + 1 probe byte
    [InlineData(100, 50, 10, 11, true)]   // size larger than the cap: still only cap + 1
    [InlineData(100, 11, 10, 11, true)]   // one over
    [InlineData(100, 10, 10, 10, false)]  // exactly the cap
    [InlineData(100, 9, 10, 9, false)]
    [InlineData(10, 0, 10, 10, false)]    // file exactly the cap
    [InlineData(11, 0, 10, 11, true)]     // file one over the cap
    [InlineData(1, 0, 1, 1, false)]
    public void ReadRange_CapsAtMaxBytesPlusOneProbe(int fileLength, long size, long maxBytes, int expectedLength, bool exceeds)
    {
        byte[] data = Pattern(fileLength);
        using var r = KittyFileMedium.ReadRange(new MemoryStream(data), 0, size, maxBytes);
        Assert.True(r.IsOk);
        Assert.Equal(expectedLength, r.Length);
        Assert.Equal(exceeds, r.ExceedsCap);
        Assert.Equal(data.AsSpan(0, expectedLength).ToArray(), r.Data.ToArray());
    }

    [Fact]
    public void ReadRange_DeclaredSizeSmallerThanFileButAboveCapStillProbes()
    {
        // S=60 on a 100 byte file with cap 10: the declared size is available, the cap decides.
        using var r = KittyFileMedium.ReadRange(new MemoryStream(Pattern(100)), 20, 60, 10);
        Assert.True(r.IsOk);
        Assert.True(r.ExceedsCap);
        Assert.Equal(11, r.Length);
    }

    [Fact]
    public void ReadRange_FileThatGrewReadsExactlyTheRequestedSize()
    {
        byte[] content = Pattern(120);            // 20 bytes appended after the length was sampled
        using var sized = KittyFileMedium.ReadRange(new LyingLengthStream(content, 100), 0, 7, Cap);
        Assert.True(sized.IsOk);
        Assert.Equal(content.AsSpan(0, 7).ToArray(), sized.Data.ToArray());

        using var whole = KittyFileMedium.ReadRange(new LyingLengthStream(content, 100), 0, 0, Cap);
        Assert.True(whole.IsOk);
        Assert.Equal(100, whole.Length);          // the sampled length, not the grown one
        Assert.Equal(content.AsSpan(0, 100).ToArray(), whole.Data.ToArray());
    }

    [Fact]
    public void ReadRange_FileThatShrankFails()
    {
        AssertBadFile(KittyFileMedium.ReadRange(new LyingLengthStream(Pattern(50), 100), 0, 100, Cap));
        AssertBadFile(KittyFileMedium.ReadRange(new LyingLengthStream(Pattern(50), 100), 0, 0, Cap));
    }

    [Fact]
    public void ReadRange_NonSeekableSourceFails()
    {
        AssertBadFile(KittyFileMedium.ReadRange(new NonSeekableStream(Pattern(10)), 0, 0, Cap));
    }

    [Fact]
    public void ReadRange_IoErrorFails()
    {
        AssertBadFile(KittyFileMedium.ReadRange(new ThrowingStream(Pattern(10)), 0, 0, Cap));
    }

    [Fact]
    public void Result_DisposeIsIdempotentAndEmptiesData()
    {
        var r = KittyFileMedium.ReadRange(new MemoryStream(Pattern(10)), 0, 0, Cap);
        Assert.Equal(10, r.Length);
        r.Dispose();
        r.Dispose();
        Assert.Equal(0, r.Length);
        Assert.True(r.Data.IsEmpty);
        // The shared failure singletons tolerate disposal.
        KittyMediumReadResult.BadFile.Dispose();
        KittyMediumReadResult.Unsupported.Dispose();
        Assert.Equal(KittyStatus.UnsupportedMedium, KittyMediumReadResult.Unsupported.Status);
    }

    // ---- Reader: t=f --------------------------------------------------------------------------------------

    [Fact]
    public void File_ReadsWholeFile()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(1000);
        string path = dir.Write("image.bin", data);
        using var r = ReadFile(new KittyMediumReader(), 'f', path);
        Assert.True(r.IsOk);
        Assert.Equal(data, r.Data.ToArray());
        Assert.True(File.Exists(path), "t=f must never delete the file");
    }

    [Fact]
    public void File_HonoursSizeAndOffset()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(1000);
        string path = dir.Write("image.bin", data);
        var reader = new KittyMediumReader();

        using (var mid = ReadFile(reader, 'f', path, offset: 100, size: 50))
        {
            Assert.True(mid.IsOk);
            Assert.Equal(data.AsSpan(100, 50).ToArray(), mid.Data.ToArray());
        }
        using (var tail = ReadFile(reader, 'f', path, offset: 990, size: 0))
        {
            Assert.True(tail.IsOk);
            Assert.Equal(data.AsSpan(990).ToArray(), tail.Data.ToArray());
        }
        using (var exact = ReadFile(reader, 'f', path, offset: 0, size: 1000))
        {
            Assert.True(exact.IsOk);
            Assert.Equal(data, exact.Data.ToArray());
        }
    }

    [Fact]
    public void File_OffsetAtOrBeyondEofAndOversizedSizeFail()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("image.bin", Pattern(100));
        var reader = new KittyMediumReader();
        AssertBadFile(ReadFile(reader, 'f', path, offset: 100));
        AssertBadFile(ReadFile(reader, 'f', path, offset: 100, size: 1));
        AssertBadFile(ReadFile(reader, 'f', path, offset: 101));
        AssertBadFile(ReadFile(reader, 'f', path, offset: 0, size: 101));
        AssertBadFile(ReadFile(reader, 'f', path, offset: 90, size: 11));
    }

    [Fact]
    public void File_CappedAtMaxBytesPlusOne()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(100);
        string path = dir.Write("image.bin", data);
        using var r = ReadFile(new KittyMediumReader(), 'f', path, cap: 10);
        Assert.True(r.IsOk);
        Assert.True(r.ExceedsCap);
        Assert.Equal(11, r.Length);
        Assert.Equal(data.AsSpan(0, 11).ToArray(), r.Data.ToArray());
    }

    [Fact]
    public void File_ZeroLengthFileFails()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("empty.bin", []);
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', path));
    }

    [Fact]
    public void File_MissingFileFails()
    {
        using var dir = new KittyMediaTempDir();
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', dir.Combine("nope.bin")));
    }

    [Fact]
    public void File_DirectoryFails()
    {
        using var dir = new KittyMediaTempDir();
        string sub = dir.Combine("sub");
        Directory.CreateDirectory(sub);
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', sub));
    }

    [Fact]
    public void File_PathWithUnicodeIsReadable()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(16);
        string path = dir.Write("bild-\u00e9\u4e2d.bin", data);
        using var r = ReadFile(new KittyMediumReader(), 'f', path);
        Assert.True(r.IsOk);
        Assert.Equal(data, r.Data.ToArray());
    }

    [Fact]
    public void File_ThroughSymlinkReadsTargetAndOpensResolvedPath()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(64);
        string target = dir.Write("real.bin", data);
        string link = dir.RequireLink("link.bin", target);
        var opener = new RecordingOpener();
        var reader = new KittyMediumReader(opener, TimeSpan.FromSeconds(30), 2, null);
        using var r = ReadFile(reader, 'f', link);
        Assert.True(r.IsOk);
        Assert.Equal(data, r.Data.ToArray());
        string opened = Assert.Single(opener.Paths);
        Assert.EndsWith("real.bin", opened);   // the link itself is never opened
        Assert.DoesNotContain("link.bin", opened);
    }

    [Fact]
    public void File_SymlinkToDeviceIsDenied()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX device files");
        using var dir = new KittyMediaTempDir();
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', dir.RequireLink("zero", "/dev/zero"), size: 16));
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', dir.RequireLink("null", "/dev/null")));
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', dir.RequireLink("status", "/proc/self/status")));
    }

    [Fact]
    public void File_IntermediateDirectorySymlinkToDevIsDenied()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX device files");
        using var dir = new KittyMediaTempDir();
        string link = dir.RequireLink("devlink", "/dev", directory: true);
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', Path.Combine(link, "zero"), size: 16));
    }

    [Fact]
    public void File_SymlinkToDirectoryFails()
    {
        using var dir = new KittyMediaTempDir();
        string sub = dir.Combine("sub");
        Directory.CreateDirectory(sub);
        string link = dir.RequireLink("sublink", sub, directory: true);
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', link));
    }

    [Fact]
    public void File_SymlinkLoopFails()
    {
        using var dir = new KittyMediaTempDir();
        string a = dir.Combine("a");
        string b = dir.Combine("b");
        if (!KittyMediaTempDir.TryLink(a, b) || !KittyMediaTempDir.TryLink(b, a)) Assert.Skip("Symbolic links cannot be created here");
        AssertBadFile(ReadFile(new KittyMediumReader(), 'f', a));
    }

    [Fact]
    public void File_DeviceAndPseudoFilesystemPathsAreDenied()
    {
        var reader = new KittyMediumReader();
        foreach (string path in new[] { "/dev/zero", "/dev/null", "/dev/urandom", "/dev/stdin", "/proc/self/environ", "/proc/cpuinfo", "/sys/kernel/version", "/dev/shm/x" })
        {
            AssertBadFile(ReadFile(reader, 'f', path, size: 8));
        }
    }

    [Fact]
    public void File_RelativeAndMalformedPathsAreDenied()
    {
        using var dir = new KittyMediaTempDir();
        dir.Write("image.bin", Pattern(8));
        var reader = new KittyMediumReader();
        AssertBadFile(ReadFile(reader, 'f', "image.bin"));
        AssertBadFile(ReadFile(reader, 'f', "./image.bin"));
        AssertBadFile(ReadFile(reader, 'f', "../image.bin"));
        AssertBadFile(ReadFile(reader, 'f', ""));
        AssertBadFile(ReadFile(reader, 'f', dir.Root + Path.DirectorySeparatorChar));
        AssertBadFile(reader.Read('f', [], 0, 0, Cap, AllMedia));
        AssertBadFile(reader.Read('f', Utf8("/tmp/a\0b"), 0, 0, Cap, AllMedia));
        AssertBadFile(reader.Read('f', Utf8("/tmp/a\nb"), 0, 0, Cap, AllMedia));
        AssertBadFile(reader.Read('f', [(byte)'/', 0xFF, 0xFE], 0, 0, Cap, AllMedia));
        AssertBadFile(reader.Read('f', Utf8("/" + new string('a', 2048)), 0, 0, Cap, AllMedia));
    }

    [Fact]
    public void File_UncAndDevicePathStringsAreDeniedOnEveryOs()
    {
        var reader = new KittyMediumReader();
        foreach (string path in new[] { @"\\.\PhysicalDrive0", @"\\?\C:\x.png", @"\\server\share\x.png", @"\\.\pipe\x" })
        {
            AssertBadFile(ReadFile(reader, 'f', path));
        }
    }

    [Fact]
    public void File_DotDotInAbsolutePathIsNormalisedBeforeTheChecks()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(32);
        dir.Write("image.bin", data);
        Directory.CreateDirectory(dir.Combine("sub"));
        string sep = Path.DirectorySeparatorChar.ToString();
        string viaDotDot = dir.Root + sep + "sub" + sep + ".." + sep + "image.bin";
        using (var r = ReadFile(new KittyMediumReader(), 'f', viaDotDot))
        {
            Assert.True(r.IsOk);
            Assert.Equal(data, r.Data.ToArray());
        }

        if (!OperatingSystem.IsWindows())
        {
            // Walking out of the directory into /dev lands on a denied path.
            string depth = string.Concat(Enumerable.Repeat(".." + sep, 40));
            AssertBadFile(ReadFile(new KittyMediumReader(), 'f', dir.Root + sep + depth + "dev" + sep + "zero", size: 4));
        }
    }

    // ---- Flags --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData('f', KittyMediaFlags.Direct)]
    [InlineData('f', KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory)]
    [InlineData('t', KittyMediaFlags.Direct | KittyMediaFlags.File)]
    [InlineData('s', KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile)]
    [InlineData('f', KittyMediaFlags.None)]
    [InlineData('d', AllMedia)]
    [InlineData('x', AllMedia)]
    public void DisallowedOrUnknownMediumIsUnsupported(char medium, KittyMediaFlags allowed)
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("tty-graphics-protocol-img.bin", Pattern(8));
        var opener = new RecordingOpener();
        var reader = new KittyMediumReader(opener, TimeSpan.FromSeconds(30), 2, null);
        var r = reader.Read(medium, Utf8(path), 0, 0, Cap, allowed);
        Assert.Same(KittyMediumReadResult.Unsupported, r);
        Assert.Equal(KittyStatus.UnsupportedMedium, r.Status);
        Assert.Empty(opener.Paths);                      // never touched the file system
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData('f', KittyMediaFlags.File)]
    [InlineData('f', KittyMediaFlags.File | KittyMediaFlags.Direct)]
    public void AllowedMediumReads(char medium, KittyMediaFlags allowed)
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));
        using var r = new KittyMediumReader().Read(medium, Utf8(path), 0, 0, Cap, allowed);
        Assert.True(r.IsOk);
    }

    // ---- t=t ----------------------------------------------------------------------------------------------

    [Fact]
    public void TempFile_MarkedFileInTempRootIsReadThenDeleted()
    {
        using var dir = new KittyMediaTempDir();
        byte[] data = Pattern(40);
        string path = dir.Write("tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".bin", data);
        using var r = ReadFile(new KittyMediumReader(), 't', path);
        Assert.True(r.IsOk);
        Assert.Equal(data, r.Data.ToArray());
        Assert.False(File.Exists(path), "a successfully read temp file must be deleted");
    }

    [Fact]
    public void TempFile_MarkerInDirectoryNameCounts()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write(Path.Combine("tty-graphics-protocol-dir", "frame.bin"), Pattern(8));
        using var r = ReadFile(new KittyMediumReader(), 't', path);
        Assert.True(r.IsOk);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TempFile_DeletedOnlyAfterSuccess()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("tty-graphics-protocol-keep.bin", Pattern(40));
        var reader = new KittyMediumReader();

        AssertBadFile(ReadFile(reader, 't', path, offset: 40));          // offset at EOF
        AssertBadFile(ReadFile(reader, 't', path, offset: 0, size: 41)); // file smaller than declared
        AssertBadFile(ReadFile(reader, 't', path, offset: 99));
        Assert.True(File.Exists(path), "failed reads must not delete the file");

        using var ok = ReadFile(reader, 't', path, offset: 4, size: 8);
        Assert.True(ok.IsOk);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TempFile_PartialReadStillDeletesAfterSuccess()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("tty-graphics-protocol-partial.bin", Pattern(40));
        using var r = ReadFile(new KittyMediumReader(), 't', path, offset: 0, size: 8);
        Assert.True(r.IsOk);
        Assert.Equal(8, r.Length);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TempFile_WithoutMarkerIsNeitherReadNorDeleted()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("plain-image.bin", Pattern(40));
        AssertBadFile(ReadFile(new KittyMediumReader(), 't', path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TempFile_MarkedFileOutsideTempRootsIsNeitherReadNorDeleted()
    {
        using var dir = new KittyMediaTempDir();
        using var elsewhere = new KittyMediaTempDir();
        Assert.True(KittyPathPolicy.TryResolve(elsewhere.Root, out string elsewhereRoot));
        string path = dir.Write("tty-graphics-protocol-x.bin", Pattern(40));
        var reader = new KittyMediumReader(KittyFileOpener.Instance, TimeSpan.FromSeconds(30), 2, [elsewhereRoot]);
        AssertBadFile(ReadFile(reader, 't', path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TempFile_SymlinkWithMarkerToUnmarkedTargetIsDenied()
    {
        using var dir = new KittyMediaTempDir();
        string secret = dir.Write("secret.bin", Pattern(40));
        string link = dir.RequireLink("tty-graphics-protocol-link.bin", secret);
        AssertBadFile(ReadFile(new KittyMediumReader(), 't', link));
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public void TempFile_SymlinkToOutsideTempRootIsDenied()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX paths");
        using var dir = new KittyMediaTempDir();
        string link = dir.RequireLink("tty-graphics-protocol-dev.bin", "/dev/zero");
        AssertBadFile(ReadFile(new KittyMediumReader(), 't', link, size: 8));
    }

    [Fact]
    public void FileMedium_DoesNotDeleteAMarkedFile()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("tty-graphics-protocol-f.bin", Pattern(8));
        using var r = ReadFile(new KittyMediumReader(), 'f', path);
        Assert.True(r.IsOk);
        Assert.True(File.Exists(path));
    }

    // ---- One outcome for every failure --------------------------------------------------------------------

    [Fact]
    public void EveryFailureClassYieldsTheSameSingleOutcome()
    {
        using var dir = new KittyMediaTempDir();
        string good = dir.Write("good.bin", Pattern(16));
        string unmarked = dir.Write("unmarked.bin", Pattern(16));
        string sub = dir.Combine("sub");
        Directory.CreateDirectory(sub);
        var reader = new KittyMediumReader();

        var failures = new List<KittyMediumReadResult>
        {
            ReadFile(reader, 'f', dir.Combine("missing.bin")),                 // no such file
            ReadFile(reader, 'f', sub),                                        // directory
            ReadFile(reader, 'f', good, offset: 16),                           // offset at EOF
            ReadFile(reader, 'f', good, offset: 17),                           // offset beyond EOF
            ReadFile(reader, 'f', good, size: 17),                             // smaller than declared
            ReadFile(reader, 'f', "relative.bin"),                             // relative
            ReadFile(reader, 'f', "/dev/zero", size: 4),                       // device
            ReadFile(reader, 'f', "/proc/self/status"),                        // pseudo filesystem
            ReadFile(reader, 'f', @"\\server\share\a.png"),                    // UNC
            ReadFile(reader, 't', unmarked),                                   // temp file without marker
            reader.Read('f', Utf8("/a\0b"), 0, 0, Cap, AllMedia),              // NUL
            reader.Read('f', Utf8("/a\nb"), 0, 0, Cap, AllMedia),              // newline
            reader.Read('f', [0x2F, 0xFF], 0, 0, Cap, AllMedia),               // invalid UTF-8
            reader.Read('f', Utf8("/" + new string('a', 2048)), 0, 0, Cap, AllMedia), // 2049 bytes
            reader.Read('f', [], 0, 0, Cap, AllMedia),                         // empty
            reader.Read('f', Utf8(good), -1, 0, Cap, AllMedia),                // negative offset
        };
        foreach (var failure in failures) AssertBadFile(failure);
        Assert.True(File.Exists(unmarked));
    }

    // ---- Bounded open and the abandoned-attempt budget ----------------------------------------------------

    private sealed class RecordingOpener : IKittyFileOpener
    {
        private readonly ConcurrentQueue<string> _paths = new();
        public IReadOnlyCollection<string> Paths => _paths;

        public SafeFileHandle Open(string path)
        {
            _paths.Enqueue(path);
            return KittyFileOpener.Instance.Open(path);
        }
    }

    private sealed class ThrowingOpener : IKittyFileOpener
    {
        public int Calls;

        public SafeFileHandle Open(string path)
        {
            Interlocked.Increment(ref Calls);
            throw new IOException("denied");
        }
    }

    /// <summary>Blocks every open until <see cref="Gate"/> is set (a FIFO without a writer), then really opens.</summary>
    private sealed class BlockingOpener : IKittyFileOpener
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public readonly ConcurrentQueue<SafeFileHandle> Handles = new();
        public int Calls;

        public SafeFileHandle Open(string path)
        {
            Interlocked.Increment(ref Calls);
            Gate.Wait();
            var handle = KittyFileOpener.Instance.Open(path);
            Handles.Enqueue(handle);
            return handle;
        }
    }

    [Fact]
    public void OpenFailureIsBadFileAndDoesNotSpendTheBudget()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));
        var opener = new ThrowingOpener();
        var reader = new KittyMediumReader(opener, TimeSpan.FromSeconds(30), 2, null);
        for (int i = 0; i < 5; i++) AssertBadFile(ReadFile(reader, 'f', path));
        Assert.Equal(5, opener.Calls);
        Assert.Equal(0, reader.AbandonedOpens);
        Assert.False(reader.FileMediaDisabled);
    }

    [Fact]
    public void HungOpensAreAbandonedAndDisableFileMediaAfterTheBudget()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));
        string marked = dir.Write("tty-graphics-protocol-img.bin", Pattern(8));
        var opener = new BlockingOpener();
        var reader = new KittyMediumReader(opener, TimeSpan.FromMilliseconds(50), 2, null);
        try
        {
            AssertBadFile(ReadFile(reader, 'f', path));
            Assert.Equal(1, reader.AbandonedOpens);
            Assert.False(reader.FileMediaDisabled);

            AssertBadFile(ReadFile(reader, 'f', path));
            Assert.Equal(2, reader.AbandonedOpens);
            Assert.True(reader.FileMediaDisabled);

            // Budget spent: every file medium now answers "unsupported" without starting another open.
            Assert.Same(KittyMediumReadResult.Unsupported, ReadFile(reader, 'f', path));
            Assert.Same(KittyMediumReadResult.Unsupported, ReadFile(reader, 't', marked));
            Assert.Same(KittyMediumReadResult.Unsupported, ReadFile(reader, 'f', dir.Combine("missing.bin")));
            Assert.Equal(2, reader.AbandonedOpens);
            Assert.True(File.Exists(marked));

            // Direct-only configuration still reports the same unsupported outcome, not a different one.
            Assert.Same(KittyMediumReadResult.Unsupported, ReadFile(reader, 'f', path, allowed: KittyMediaFlags.Direct));
        }
        finally
        {
            opener.Gate.Set();
        }

        // The blocked helper threads finish their opens late and must close the handles themselves.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref opener.Calls) == 2 && opener.Handles.Count == 2, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => opener.Handles.All(h => h.IsClosed), TimeSpan.FromSeconds(10)), "late handles must be disposed");
        Assert.Equal(2, opener.Calls);
    }

    [Fact]
    public void OpenThatCompletesInTimeDoesNotSpendTheBudget()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));
        var opener = new BlockingOpener();
        opener.Gate.Set();
        var reader = new KittyMediumReader(opener, TimeSpan.FromSeconds(30), 2, null);
        for (int i = 0; i < 4; i++)
        {
            using var r = ReadFile(reader, 'f', path);
            Assert.True(r.IsOk);
        }
        Assert.Equal(0, reader.AbandonedOpens);
        Assert.All(opener.Handles, h => Assert.True(h.IsClosed));
    }

    [Fact]
    public void ZeroBudgetDisablesFileMediaImmediately()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));
        var reader = new KittyMediumReader(KittyFileOpener.Instance, TimeSpan.FromSeconds(30), 0, null);
        Assert.True(reader.FileMediaDisabled);
        Assert.Same(KittyMediumReadResult.Unsupported, ReadFile(reader, 'f', path));
    }

    [Fact]
    public void TryOpenBounded_ReturnsHandleOrNullAndReportsAbandonment()
    {
        using var dir = new KittyMediaTempDir();
        string path = dir.Write("img.bin", Pattern(8));

        using (var handle = KittyFileMedium.TryOpenBounded(KittyFileOpener.Instance, path, TimeSpan.FromSeconds(30), out bool abandoned))
        {
            Assert.NotNull(handle);
            Assert.False(abandoned);
        }

        var thrower = new ThrowingOpener();
        Assert.Null(KittyFileMedium.TryOpenBounded(thrower, path, TimeSpan.FromSeconds(30), out bool abandonedAfterError));
        Assert.False(abandonedAfterError);

        var blocker = new BlockingOpener();
        try
        {
            Assert.Null(KittyFileMedium.TryOpenBounded(blocker, path, TimeSpan.FromMilliseconds(30), out bool abandonedAfterTimeout));
            Assert.True(abandonedAfterTimeout);
        }
        finally
        {
            blocker.Gate.Set();
        }
        Assert.True(SpinWait.SpinUntil(() => blocker.Handles.Count == 1 && blocker.Handles.All(h => h.IsClosed), TimeSpan.FromSeconds(10)));
    }

    // ---- t=s through the reader ---------------------------------------------------------------------------

    private static bool PosixShmAvailable => OperatingSystem.IsLinux() && Directory.Exists("/dev/shm");

    private static string CreateShmObject(byte[] data, out string name)
    {
        name = "dotty-kitty-test-" + Guid.NewGuid().ToString("N");
        string path = "/dev/shm/" + name;
        try
        {
            File.WriteAllBytes(path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("/dev/shm is not writable");
        }
        return path;
    }

    [Fact]
    public void SharedMemory_LinuxReadsObjectThenUnlinks()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        byte[] data = Pattern(200);
        string path = CreateShmObject(data, out string name);
        try
        {
            using var r = new KittyMediumReader().Read('s', Utf8("/" + name), offset: 8, size: 64, Cap, AllMedia);
            Assert.True(r.IsOk);
            Assert.Equal(data.AsSpan(8, 64).ToArray(), r.Data.ToArray());
            Assert.False(File.Exists(path), "shm object must be unlinked after a successful read");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SharedMemory_SizeZeroReadsWholeObject_NameWithoutLeadingSlashAccepted()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        byte[] data = Pattern(50);
        string path = CreateShmObject(data, out string name);
        try
        {
            using var r = new KittyMediumReader().Read('s', Utf8(name), 0, 0, Cap, AllMedia);
            Assert.True(r.IsOk);
            Assert.Equal(data, r.Data.ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SharedMemory_FailedReadDoesNotUnlink()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        string path = CreateShmObject(Pattern(50), out string name);
        try
        {
            var reader = new KittyMediumReader();
            AssertBadFile(reader.Read('s', Utf8("/" + name), 0, 51, Cap, AllMedia));
            AssertBadFile(reader.Read('s', Utf8("/" + name), 50, 0, Cap, AllMedia));
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SharedMemory_InvalidNamesAreBadFile()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        var reader = new KittyMediumReader();
        foreach (string name in new[] { "/", "", "/a/b", "//a", "/..", "/.", "/../etc/passwd", "/missing-" + Guid.NewGuid().ToString("N") })
        {
            AssertBadFile(reader.Read('s', Utf8(name), 0, 0, Cap, AllMedia));
        }
    }

    [Fact]
    public void SharedMemory_LinkEscapingDevShmIsDenied()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        using var dir = new KittyMediaTempDir();
        string target = dir.Write("outside.bin", Pattern(16));
        string name = "dotty-kitty-link-" + Guid.NewGuid().ToString("N");
        string link = "/dev/shm/" + name;
        if (!KittyMediaTempDir.TryLink(link, target)) Assert.Skip("cannot create a link in /dev/shm");
        try
        {
            AssertBadFile(new KittyMediumReader().Read('s', Utf8("/" + name), 0, 0, Cap, AllMedia));
            Assert.True(File.Exists(target));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void SharedMemory_UnsupportedPlatformsAnswerUnsupportedMedium()
    {
        if (OperatingSystem.IsWindows() || PosixShmAvailable) Assert.Skip("shared memory is supported here");
        var r = new KittyMediumReader().Read('s', Utf8("/name"), 0, 0, Cap, AllMedia);
        Assert.Same(KittyMediumReadResult.Unsupported, r);
    }

    [Fact]
    public void SharedMemory_DisabledByFlagsIsUnsupportedOnEveryPlatform()
    {
        var r = new KittyMediumReader().Read('s', Utf8("/name"), 0, 0, Cap, KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile);
        Assert.Same(KittyMediumReadResult.Unsupported, r);
    }

    [Fact]
    public void SharedMemory_WindowsNamedMappingIsReadable()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("requires Windows");
        string name = "dotty-kitty-test-" + Guid.NewGuid().ToString("N");
        byte[] data = Pattern(128);
        using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(name, 4096);
        using (var view = mapping.CreateViewAccessor())
        {
            view.WriteArray(0, data, 0, data.Length);
        }
        var reader = new KittyMediumReader();
        using (var r = reader.Read('s', Utf8(name), offset: 16, size: 64, Cap, AllMedia))
        {
            Assert.True(r.IsOk);
            Assert.Equal(data.AsSpan(16, 64).ToArray(), r.Data.ToArray());
        }
        AssertBadFile(reader.Read('s', Utf8(name + "-missing"), 0, 64, Cap, AllMedia));
        AssertBadFile(reader.Read('s', Utf8(name), 0, 5000, Cap, AllMedia));
    }
}
