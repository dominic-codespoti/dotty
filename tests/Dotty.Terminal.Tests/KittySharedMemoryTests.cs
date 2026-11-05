using System.Text;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class KittySharedMemoryTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static bool PosixShmAvailable => OperatingSystem.IsLinux() && Directory.Exists("/dev/shm");

    // ---- Name mapping (pure; identical on every OS) -------------------------------------------------------

    [Theory]
    [InlineData("/abc", "/dev/shm/abc")]
    [InlineData("abc", "/dev/shm/abc")]                    // glibc shm_open accepts names without the slash
    [InlineData("/kitty-1234-abcd", "/dev/shm/kitty-1234-abcd")]
    [InlineData("/a.b_c-d", "/dev/shm/a.b_c-d")]
    [InlineData("/...", "/dev/shm/...")]                   // only exactly "." and ".." are special
    [InlineData("/.hidden", "/dev/shm/.hidden")]
    [InlineData("/with space", "/dev/shm/with space")]
    [InlineData("/\u00e9", "/dev/shm/\u00e9")]
    public void MapName_Accepts(string name, string expected)
    {
        Assert.True(KittySharedMemory.TryMapPosixName(name, out string path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("//abc")]            // second slash
    [InlineData("/a/b")]
    [InlineData("a/b")]
    [InlineData("/abc/")]
    [InlineData("/..")]
    [InlineData("/.")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/../etc/passwd")]
    [InlineData("/./a")]
    [InlineData("/dev/shm/abc")]     // a path, not an object name
    public void MapName_Rejects(string name)
    {
        Assert.False(KittySharedMemory.TryMapPosixName(name, out string path));
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void MapName_LengthBoundaryIs255Bytes()
    {
        Assert.Equal(255, KittySharedMemory.MaxNameBytes);
        Assert.True(KittySharedMemory.TryMapPosixName("/" + new string('a', 255), out _));
        Assert.False(KittySharedMemory.TryMapPosixName("/" + new string('a', 256), out _));
        Assert.True(KittySharedMemory.TryMapPosixName(new string('a', 255), out _));
        Assert.False(KittySharedMemory.TryMapPosixName(new string('a', 256), out _));
        // 127 two-byte chars = 254 bytes accepted, 128 = 256 bytes rejected.
        Assert.True(KittySharedMemory.TryMapPosixName("/" + new string('\u00e9', 127), out _));
        Assert.False(KittySharedMemory.TryMapPosixName("/" + new string('\u00e9', 128), out _));
    }

    // ---- Support matrix -----------------------------------------------------------------------------------

    [Fact]
    public void IsSupported_MatchesThePlatform()
    {
        bool expected = OperatingSystem.IsWindows() || PosixShmAvailable;
        Assert.Equal(expected, KittySharedMemory.IsSupported);
        if (OperatingSystem.IsMacOS()) Assert.False(KittySharedMemory.IsSupported);
    }

    [Fact]
    public void ReadWindows_OnOtherPlatformsIsUnsupportedMedium()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("covered by the Windows mapping test");
        Assert.Same(KittyMediumReadResult.Unsupported, KittySharedMemory.ReadWindows(Utf8("/name"), 0, 0, 1 << 20));
    }

    [Fact]
    public void MacOs_SharedMemoryIsUnsupportedThroughTheReader()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("macOS only");
        var r = new KittyMediumReader().Read('s', Utf8("/name"), 0, 0, 1 << 20, Dotty.Abstractions.Graphics.KittyMediaFlags.SharedMemory);
        Assert.Same(KittyMediumReadResult.Unsupported, r);
    }

    // ---- Authorisation against the real /dev/shm (Linux) ------------------------------------------------

    [Fact]
    public void AuthorizePosix_RejectsMalformedPayloads()
    {
        Assert.False(KittySharedMemory.TryAuthorizePosix([], out string resolved));
        Assert.Equal(string.Empty, resolved);
        Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/a\0b"), out _));
        Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/a\nb"), out _));
        Assert.False(KittySharedMemory.TryAuthorizePosix([(byte)'/', 0xFF], out _));
        Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/a/b"), out _));
        Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/.."), out _));
        Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/" + new string('a', 2048)), out _));
    }

    [Fact]
    public void AuthorizePosix_AcceptsObjectInsideDevShmAndResolvesLinksOutOfIt()
    {
        if (!PosixShmAvailable) Assert.Skip("requires Linux /dev/shm");
        Assert.True(KittyPathPolicy.TryResolve("/dev/shm", out string root));
        string name = "dotty-kitty-auth-" + Guid.NewGuid().ToString("N");

        // A plain (even nonexistent) name maps inside the root.
        Assert.True(KittySharedMemory.TryAuthorizePosix(Utf8("/" + name), out string resolved));
        Assert.Equal(Path.Combine(root, name), resolved);

        // A link planted in /dev/shm that points elsewhere is refused.
        using var dir = new KittyMediaTempDir();
        string target = dir.Write("outside.bin");
        string link = "/dev/shm/" + name + "-link";
        if (!KittyMediaTempDir.TryLink(link, target)) Assert.Skip("cannot create a link in /dev/shm");
        try
        {
            Assert.False(KittySharedMemory.TryAuthorizePosix(Utf8("/" + name + "-link"), out _));
        }
        finally
        {
            File.Delete(link);
        }
    }
}
