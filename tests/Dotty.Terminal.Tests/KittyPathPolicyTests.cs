using System.Text;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class KittyPathPolicyTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sep => Path.DirectorySeparatorChar.ToString();

    // ---- TryDecodePath (pure) -----------------------------------------------------------------------------

    [Theory]
    [InlineData("/tmp/a.png")]
    [InlineData("/tmp/with space/\u00e9\u4e2d\U0001F600.png")]
    [InlineData("/")]
    [InlineData("relative/path")]   // decoding does not judge absoluteness
    public void Decode_AcceptsPlainUtf8(string path)
    {
        Assert.True(KittyPathPolicy.TryDecodePath(Utf8(path), out string decoded));
        Assert.Equal(path, decoded);
    }

    [Fact]
    public void Decode_RejectsEmpty()
    {
        Assert.False(KittyPathPolicy.TryDecodePath([], out string decoded));
        Assert.Equal(string.Empty, decoded);
    }

    [Theory]
    [InlineData("/tmp/a\0b")]
    [InlineData("\0")]
    [InlineData("/tmp/a\nb")]
    [InlineData("/tmp/a\rb")]
    [InlineData("/tmp/a\r\nb")]
    [InlineData("\n/tmp/a")]
    public void Decode_RejectsNulAndNewlines(string path)
    {
        Assert.False(KittyPathPolicy.TryDecodePath(Utf8(path), out _));
    }

    [Theory]
    [InlineData(new byte[] { 0x2F, 0xFF })]                    // 0xFF never appears in UTF-8
    [InlineData(new byte[] { 0x2F, 0x80 })]                    // lone continuation byte
    [InlineData(new byte[] { 0x2F, 0xC3 })]                    // truncated two-byte sequence
    [InlineData(new byte[] { 0x2F, 0xC0, 0x80 })]              // overlong NUL
    [InlineData(new byte[] { 0x2F, 0xED, 0xA0, 0x80 })]        // UTF-16 surrogate encoded in UTF-8
    [InlineData(new byte[] { 0x2F, 0xF4, 0x90, 0x80, 0x80 })]  // above U+10FFFF
    public void Decode_RejectsInvalidUtf8(byte[] bytes)
    {
        Assert.False(KittyPathPolicy.TryDecodePath(bytes, out _));
    }

    [Fact]
    public void Decode_LengthBoundaryIs2048Bytes()
    {
        Assert.Equal(2048, KittyPathPolicy.MaxPathBytes);
        string ok = "/" + new string('a', 2047);
        string tooLong = "/" + new string('a', 2048);
        Assert.True(KittyPathPolicy.TryDecodePath(Utf8(ok), out string decoded));
        Assert.Equal(2048, decoded.Length);
        Assert.False(KittyPathPolicy.TryDecodePath(Utf8(tooLong), out _));
    }

    [Fact]
    public void Decode_LengthIsMeasuredInBytesNotChars()
    {
        // 1 + 1023 * 2 + 1 = 2048 bytes (accepted), one more two-byte char = 2050 (rejected).
        string ok = "/" + new string('\u00e9', 1023) + "a";
        string tooLong = "/" + new string('\u00e9', 1024) + "a";
        Assert.Equal(2048, Utf8(ok).Length);
        Assert.True(KittyPathPolicy.TryDecodePath(Utf8(ok), out _));
        Assert.Equal(2050, Utf8(tooLong).Length);
        Assert.False(KittyPathPolicy.TryDecodePath(Utf8(tooLong), out _));
    }

    // ---- UNC and device strings (pure; identical everywhere) ----------------------------------------------

    [Theory]
    [InlineData(@"\\.\COM1", false, true)]
    [InlineData(@"\\.\COM1", true, true)]
    [InlineData(@"\\.\pipe\name", false, true)]
    [InlineData(@"\\?\C:\x.png", false, true)]
    [InlineData(@"\\?\C:\x.png", true, true)]
    [InlineData(@"\\?\UNC\server\share\x", true, true)]
    [InlineData(@"\\server\share\x.png", false, true)]
    [InlineData(@"\\server\share\x.png", true, true)]
    [InlineData("//server/share/x.png", true, true)]    // forward-slash UNC is UNC on Windows ...
    [InlineData("//server/share/x.png", false, false)]  // ... and just a normalised path on POSIX
    [InlineData(@"/\server\share", true, true)]
    [InlineData(@"\/server/share", true, true)]
    [InlineData(@"\??\C:\x", true, true)]               // NT object namespace
    [InlineData("/??/C:/x", true, true)]
    [InlineData(@"\??\C:\x", false, false)]
    [InlineData(@"C:\Users\a\x.png", true, false)]
    [InlineData(@"C:\Users\a\x.png", false, false)]
    [InlineData("/tmp/x.png", true, false)]
    [InlineData("/tmp/x.png", false, false)]
    [InlineData(@"\single", true, false)]
    [InlineData("", false, false)]
    [InlineData("/", true, false)]
    [InlineData("\\", true, false)]
    [InlineData(@"\??", true, false)]
    public void UncAndDevicePaths(string path, bool windows, bool expected)
    {
        Assert.Equal(expected, KittyPathPolicy.IsUncOrDevicePath(path, windows));
    }

    // ---- Denied prefixes (pure) ---------------------------------------------------------------------------

    [Theory]
    [InlineData("/proc", false, true)]
    [InlineData("/proc/self/mem", false, true)]
    [InlineData("/proc/self/root/etc/passwd", false, true)]
    [InlineData("/sys", false, true)]
    [InlineData("/sys/kernel/notes", false, true)]
    [InlineData("/dev", false, true)]
    [InlineData("/dev/zero", false, true)]
    [InlineData("/dev/null", false, true)]
    [InlineData("/dev/fd/0", false, true)]
    [InlineData("/dev/pts/0", false, true)]
    [InlineData("/DEV/zero", false, true)]               // case-insensitive file systems
    [InlineData("/Proc/self/status", false, true)]
    [InlineData("/dev/shm", false, true)]
    [InlineData("/dev/shm/x", false, true)]
    [InlineData("/dev/shm/x", true, false)]              // t=s / t=t exemption
    [InlineData("/dev/shm", true, false)]
    [InlineData("/dev/SHM/x", true, false)]
    [InlineData("/dev/shmx/y", true, true)]              // not below /dev/shm
    [InlineData("/dev/zero", true, true)]                // exemption is only for /dev/shm
    [InlineData("/dev/shm2", true, true)]
    [InlineData("/devices/x", false, false)]
    [InlineData("/processes/x", false, false)]
    [InlineData("/sysroot/x", false, false)]
    [InlineData("/tmp/x", false, false)]
    [InlineData("/home/u/dev/zero", false, false)]
    [InlineData("/home/u/proc", false, false)]
    [InlineData("/", false, false)]
    [InlineData("/de", false, false)]
    [InlineData("", false, false)]
    public void DeniedPrefixes(string fullPath, bool allowDevShm, bool expected)
    {
        Assert.Equal(expected, KittyPathPolicy.IsDeniedPrefix(fullPath, allowDevShm));
    }

    // ---- IsStrictlyInside (pure) --------------------------------------------------------------------------

    [Theory]
    [InlineData("/tmp", "/tmp/a", true)]
    [InlineData("/tmp/", "/tmp/a", true)]
    [InlineData("/tmp", "/tmp/a/b/c.bin", true)]
    [InlineData("/tmp", "/tmp", false)]           // the root itself is not "inside"
    [InlineData("/tmp", "/tmp/", false)]
    [InlineData("/tmp", "/tmpx/a", false)]        // component boundary
    [InlineData("/tmp", "/other/tmp/a", false)]
    [InlineData("/tmp/a", "/tmp/ab/c", false)]
    [InlineData("/tmp/a", "/tmp/a/b", true)]
    [InlineData("/", "/a", false)]                // a filesystem root never qualifies as a temp root
    [InlineData("", "/a", false)]
    [InlineData("/tmp", "", false)]
    public void StrictlyInside(string root, string path, bool expected)
    {
        Assert.Equal(expected, KittyPathPolicy.IsStrictlyInside(root, path));
    }

    [Fact]
    public void InsideAny_MatchesAnyRoot()
    {
        string[] roots = ["/a", "/b/c"];
        Assert.True(KittyPathPolicy.IsInsideAny(roots, "/b/c/x"));
        Assert.True(KittyPathPolicy.IsInsideAny(roots, "/a/x"));
        Assert.False(KittyPathPolicy.IsInsideAny(roots, "/b/x"));
        Assert.False(KittyPathPolicy.IsInsideAny([], "/a/x"));
    }

    // ---- TryResolve (real file system) --------------------------------------------------------------------

    private static string Real(string path)
    {
        Assert.True(KittyPathPolicy.TryResolve(Path.GetFullPath(path), out string real));
        return real;
    }

    [Fact]
    public void Resolve_PlainPathIsUnchangedExceptForRootLinks()
    {
        using var dir = new KittyMediaTempDir();
        string file = dir.Write("a.bin");
        Assert.Equal(Path.Combine(Real(dir.Root), "a.bin"), Real(file));
    }

    [Fact]
    public void Resolve_NonexistentComponentsAreKept()
    {
        using var dir = new KittyMediaTempDir();
        string missing = Path.Combine(dir.Root, "no", "such", "file.bin");
        Assert.Equal(Path.Combine(Real(dir.Root), "no", "such", "file.bin"), Real(missing));
    }

    [Fact]
    public void Resolve_FollowsFileLinkChainsWithRelativeAndAbsoluteTargets()
    {
        using var dir = new KittyMediaTempDir();
        string target = dir.Write("target.bin");
        dir.RequireLink("rel", "target.bin");                 // relative target, resolved against the link's directory
        dir.RequireLink("abs", target);                       // absolute target
        dir.RequireLink("chain", dir.Combine("rel"));         // link to a link
        string expected = Path.Combine(Real(dir.Root), "target.bin");
        Assert.Equal(expected, Real(dir.Combine("rel")));
        Assert.Equal(expected, Real(dir.Combine("abs")));
        Assert.Equal(expected, Real(dir.Combine("chain")));
    }

    [Fact]
    public void Resolve_FollowsIntermediateDirectoryLinks()
    {
        using var dir = new KittyMediaTempDir();
        dir.Write(Path.Combine("real", "inner", "file.bin"));
        dir.RequireLink("dirlink", dir.Combine("real"), directory: true);
        string expected = Path.Combine(Real(dir.Root), "real", "inner", "file.bin");
        Assert.Equal(expected, Real(Path.Combine(dir.Root, "dirlink", "inner", "file.bin")));
    }

    [Fact]
    public void Resolve_DotDotInsideLinkTargetUsesRealParent()
    {
        using var dir = new KittyMediaTempDir();
        dir.Write(Path.Combine("a", "b", "file.bin"));
        // a/up -> ../ is relative to a/, so it points at the temp root; up/a/b/file.bin is a/b/file.bin again.
        dir.Write(Path.Combine("a", "keep"));
        string link = Path.Combine(dir.Root, "a", "up");
        if (!KittyMediaTempDir.TryLink(link, "..", directory: true)) Assert.Skip("Symbolic links cannot be created here");
        string expected = Path.Combine(Real(dir.Root), "a", "b", "file.bin");
        Assert.Equal(expected, Real(Path.Combine(link, "a", "b", "file.bin")));
    }

    [Fact]
    public void Resolve_LoopsAndSelfLinksFail()
    {
        using var dir = new KittyMediaTempDir();
        string a = dir.Combine("a");
        string b = dir.Combine("b");
        if (!KittyMediaTempDir.TryLink(a, b) || !KittyMediaTempDir.TryLink(b, a)) Assert.Skip("Symbolic links cannot be created here");
        Assert.False(KittyPathPolicy.TryResolve(a, out string resolved));
        Assert.Equal(string.Empty, resolved);

        string self = dir.Combine("self");
        if (!KittyMediaTempDir.TryLink(self, "self")) Assert.Skip("Symbolic links cannot be created here");
        Assert.False(KittyPathPolicy.TryResolve(self, out _));

        // A loop in a middle component fails too.
        Assert.False(KittyPathPolicy.TryResolve(Path.Combine(a, "child", "x"), out _));
    }

    [Fact]
    public void Resolve_LongButFiniteChainsSucceedAndExcessiveOnesFail()
    {
        using var dir = new KittyMediaTempDir();
        string target = dir.Write("end.bin");
        string previous = target;
        for (int i = 0; i < 30; i++)
        {
            string link = dir.Combine("l" + i);
            if (!KittyMediaTempDir.TryLink(link, previous)) Assert.Skip("Symbolic links cannot be created here");
            previous = link;
        }
        Assert.Equal(Path.Combine(Real(dir.Root), "end.bin"), Real(previous));

        for (int i = 30; i < 60; i++)
        {
            string link = dir.Combine("l" + i);
            Assert.True(KittyMediaTempDir.TryLink(link, previous));
            previous = link;
        }
        Assert.False(KittyPathPolicy.TryResolve(previous, out _));
    }

    // ---- TryAuthorize (real file system) ------------------------------------------------------------------

    private static bool Authorize(char medium, string path, out string resolved, IReadOnlyList<string>? roots = null) =>
        KittyPathPolicy.TryAuthorize(medium, Utf8(path), roots, out resolved);

    [Fact]
    public void Authorize_AcceptsAbsoluteRegularPath()
    {
        using var dir = new KittyMediaTempDir();
        string file = dir.Write("a.bin");
        Assert.True(Authorize('f', file, out string resolved));
        Assert.Equal(Path.Combine(Real(dir.Root), "a.bin"), resolved);
    }

    [Fact]
    public void Authorize_NormalisesDotDot()
    {
        using var dir = new KittyMediaTempDir();
        dir.Write("a.bin");
        Directory.CreateDirectory(dir.Combine("sub"));
        Assert.True(Authorize('f', dir.Root + Sep + "sub" + Sep + ".." + Sep + "a.bin", out string resolved));
        Assert.Equal(Path.Combine(Real(dir.Root), "a.bin"), resolved);
    }

    [Theory]
    [InlineData("a.bin")]
    [InlineData("./a.bin")]
    [InlineData("../a.bin")]
    [InlineData("sub/a.bin")]
    [InlineData("~/a.bin")]
    public void Authorize_DeniesRelativePaths(string path)
    {
        Assert.False(Authorize('f', path, out string resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Authorize_DeniesTrailingSeparator()
    {
        using var dir = new KittyMediaTempDir();
        dir.Write("a.bin");
        Assert.False(Authorize('f', dir.Combine("a.bin") + Sep, out _));
        Assert.False(Authorize('f', dir.Root + Sep, out _));
    }

    [Theory]
    [InlineData("/dev/zero")]
    [InlineData("/dev/null")]
    [InlineData("/dev/random")]
    [InlineData("/dev/stdin")]
    [InlineData("/dev/fd/0")]
    [InlineData("/dev/../dev/zero")]
    [InlineData("/tmp/../dev/zero")]
    [InlineData("/proc/self/environ")]
    [InlineData("/proc/self/mem")]
    [InlineData("/proc/self/root/etc/hostname")]
    [InlineData("/sys/kernel/version")]
    [InlineData("/dev/shm/x")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\x.png")]
    [InlineData(@"\\server\share\x.png")]
    public void Authorize_DeniesDevicePseudoFsAndUncPaths(string path)
    {
        Assert.False(Authorize('f', path, out string resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Authorize_DevShmIsForTempAndSharedMemoryOnly()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) Assert.Skip("requires Linux /dev/shm");
        string path = "/dev/shm/tty-graphics-protocol-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("/dev/shm is not writable");
        }
        try
        {
            Assert.False(Authorize('f', path, out _));
            Assert.True(Authorize('t', path, out string resolved));
            Assert.Equal(Real(path), resolved);
            Assert.True(Authorize('t', path, out _, roots: [Real("/dev/shm")]));
            Assert.False(Authorize('t', path, out _, roots: ["/tmp"]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Authorize_DeniesSymlinkToDevice()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX device files");
        using var dir = new KittyMediaTempDir();
        Assert.False(Authorize('f', dir.RequireLink("zero", "/dev/zero"), out _));
        Assert.False(Authorize('f', dir.RequireLink("proc", "/proc/self/environ"), out _));
        Assert.False(Authorize('f', dir.RequireLink("sys", "/sys/kernel", directory: true), out _));
        // Link chain: allowed-looking link -> allowed-looking link -> /dev/zero.
        Assert.False(Authorize('f', dir.RequireLink("hop", dir.Combine("zero")), out _));
    }

    [Fact]
    public void Authorize_DeniesIntermediateDirectorySymlinkIntoDev()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX device files");
        using var dir = new KittyMediaTempDir();
        string link = dir.RequireLink("d", "/dev", directory: true);
        Assert.False(Authorize('f', Path.Combine(link, "zero"), out _));
        Assert.False(Authorize('f', Path.Combine(link, "null"), out _));
    }

    [Fact]
    public void Authorize_SymlinkToRegularFileResolvesToTarget()
    {
        using var dir = new KittyMediaTempDir();
        string target = dir.Write("sub" + Sep + "target.bin");
        string link = dir.RequireLink("link.bin", target);
        Assert.True(Authorize('f', link, out string resolved));
        Assert.Equal(Path.Combine(Real(dir.Root), "sub", "target.bin"), resolved);
    }

    [Fact]
    public void Authorize_SymlinkToDirectoryIsAPolicyMatterOnlyForTheOpenToReject()
    {
        // The policy checks locations; "regular files only" is enforced after the open.
        using var dir = new KittyMediaTempDir();
        string sub = dir.Combine("sub");
        Directory.CreateDirectory(sub);
        string link = dir.RequireLink("sublink", sub, directory: true);
        Assert.True(Authorize('f', link, out string resolved));
        Assert.Equal(Real(sub), resolved);
    }

    [Fact]
    public void Authorize_DeniesLoops()
    {
        using var dir = new KittyMediaTempDir();
        string a = dir.Combine("a");
        string b = dir.Combine("b");
        if (!KittyMediaTempDir.TryLink(a, b) || !KittyMediaTempDir.TryLink(b, a)) Assert.Skip("Symbolic links cannot be created here");
        Assert.False(Authorize('f', a, out _));
    }

    [Fact]
    public void Authorize_RejectsMalformedPayloadBytes()
    {
        Assert.False(KittyPathPolicy.TryAuthorize('f', [], null, out _));
        Assert.False(KittyPathPolicy.TryAuthorize('f', Utf8("/tmp/a\0b"), null, out _));
        Assert.False(KittyPathPolicy.TryAuthorize('f', [(byte)'/', 0xFF], null, out _));
        Assert.False(KittyPathPolicy.TryAuthorize('f', Utf8("/" + new string('a', 2048)), null, out _));
    }

    [Fact]
    public void Authorize_OverlongComponentNeverThrows()
    {
        // 2048 bytes pass the decoder; a 2047-char component is rejected by the file system layer. Whatever the
        // outcome, no exception may escape the policy.
        string path = "/" + new string('a', 2047);
        Assert.Equal(2048, Utf8(path).Length);
        _ = Authorize('f', path, out _);
    }

    // ---- t=t rules ----------------------------------------------------------------------------------------

    [Fact]
    public void TempAuthorize_RequiresMarkerAndTempRoot()
    {
        using var dir = new KittyMediaTempDir();
        string marked = dir.Write("tty-graphics-protocol-1.bin");
        string unmarked = dir.Write("plain.bin");
        string[] roots = [Real(dir.Root)];

        Assert.True(Authorize('t', marked, out string resolved, roots));
        Assert.Equal(Path.Combine(roots[0], "tty-graphics-protocol-1.bin"), resolved);
        Assert.False(Authorize('t', unmarked, out _, roots));
    }

    [Fact]
    public void TempAuthorize_MarkerInAnyPathComponentCounts()
    {
        using var dir = new KittyMediaTempDir();
        string file = dir.Write(Path.Combine("tty-graphics-protocol-dir", "frame.bin"));
        Assert.True(Authorize('t', file, out _, [Real(dir.Root)]));
    }

    [Fact]
    public void TempAuthorize_MarkerIsCaseSensitive()
    {
        using var dir = new KittyMediaTempDir();
        string file = dir.Write("TTY-GRAPHICS-PROTOCOL-1.bin");
        Assert.False(Authorize('t', file, out _, [Real(dir.Root)]));
    }

    [Fact]
    public void TempAuthorize_OutsideRootsIsDenied()
    {
        using var dir = new KittyMediaTempDir();
        using var other = new KittyMediaTempDir();
        string marked = dir.Write("tty-graphics-protocol-1.bin");
        Assert.False(Authorize('t', marked, out _, [Real(other.Root)]));
        Assert.False(Authorize('t', marked, out _, []));
        // Sibling directory sharing a name prefix with the root is not inside it.
        Assert.False(Authorize('t', marked, out _, [Real(dir.Root).Substring(0, Real(dir.Root).Length - 1)]));
    }

    [Fact]
    public void TempAuthorize_RootItselfAndFilesystemRootAreNotTempRoots()
    {
        using var dir = new KittyMediaTempDir();
        string marked = dir.Write("tty-graphics-protocol-1.bin");
        string fsRoot = Path.GetPathRoot(Real(dir.Root))!;
        Assert.False(Authorize('t', marked, out _, [fsRoot]));
    }

    [Fact]
    public void TempAuthorize_MarkedLinkToUnmarkedTargetIsDenied()
    {
        using var dir = new KittyMediaTempDir();
        string secret = dir.Write("secret.bin");
        string link = dir.RequireLink("tty-graphics-protocol-link.bin", secret);
        Assert.False(Authorize('t', link, out _, [Real(dir.Root)]));
    }

    [Fact]
    public void TempAuthorize_MarkedLinkToTargetOutsideRootsIsDenied()
    {
        using var dir = new KittyMediaTempDir();
        using var outside = new KittyMediaTempDir();
        string target = outside.Write("tty-graphics-protocol-target.bin");
        string link = dir.RequireLink("tty-graphics-protocol-link.bin", target);
        Assert.False(Authorize('t', link, out _, [Real(dir.Root)]));
        Assert.True(Authorize('t', link, out string resolved, [Real(outside.Root)]));
        Assert.Equal(Path.Combine(Real(outside.Root), "tty-graphics-protocol-target.bin"), resolved);
    }

    [Fact]
    public void TempAuthorize_DotDotCannotEscapeTheRoot()
    {
        using var dir = new KittyMediaTempDir();
        using var outside = new KittyMediaTempDir();
        string target = outside.Write("tty-graphics-protocol-x.bin");
        // <root>/tty-graphics-protocol-d/../../<outside>/file normalises to a marked file outside the only root.
        string sneaky = dir.Root + Sep + "tty-graphics-protocol-d" + Sep + ".." + Sep + ".." + Sep
                        + Path.GetFileName(outside.Root) + Sep + "tty-graphics-protocol-x.bin";
        Assert.True(File.Exists(target));
        // Resolves to the outside file, which has the marker but is outside the only root.
        Assert.False(Authorize('t', sneaky, out _, [Real(dir.Root)]));
    }

    [Fact]
    public void DefaultTempRoots_ContainTheResolvedSystemTempPath()
    {
        IReadOnlyList<string> roots = KittyPathPolicy.DefaultTempRoots();
        string expected = Real(Path.GetTempPath());
        Assert.Contains(expected, roots);
        foreach (string root in roots) Assert.False(string.IsNullOrEmpty(root));
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DefaultTempRoots_TempFileUnderSystemTempIsAuthorised()
    {
        using var dir = new KittyMediaTempDir();
        string marked = dir.Write("tty-graphics-protocol-sys.bin");
        Assert.True(Authorize('t', marked, out _));   // default roots
        string unmarked = dir.Write("sys.bin");
        Assert.False(Authorize('t', unmarked, out _));
    }
}
