using System.Runtime.InteropServices;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

[CollectionDefinition("VirtualArena RSS", DisableParallelization = true)]
public sealed class VirtualArenaRssCollection { }

/// <summary>
/// <see cref="VirtualArena"/> contract tests. Every behavioural test runs against the calloc fallback AND the
/// backend that was selected for this platform, so the degraded path is exercised wherever the OS path works.
/// </summary>
[Collection("VirtualArena RSS")]
public sealed unsafe class VirtualArenaTests
{
    private const int MiB = 1024 * 1024;

    // Not an iterator: iterator blocks cannot live in an unsafe context.
    public static IEnumerable<object[]> Backends()
    {
        var backends = new List<object[]> { new object[] { (byte)VirtualArena.Backend.AllocZeroed } };
        if (VirtualArena.ActiveBackend != VirtualArena.Backend.AllocZeroed)
            backends.Add(new object[] { (byte)VirtualArena.ActiveBackend });
        return backends;
    }

    private static byte* At(byte* block, nuint offset) => block + (long)offset;

    private static bool IsZero(byte* start, nuint length) =>
        new ReadOnlySpan<byte>(start, checked((int)length)).IndexOfAnyExcept((byte)0) < 0;

    private static bool IsAll(byte* start, nuint length, byte value) =>
        new ReadOnlySpan<byte>(start, checked((int)length)).IndexOfAnyExcept(value) < 0;

    [Theory]
    [MemberData(nameof(Backends))]
    public void ReserveReturnsZeroedMemory(byte backendValue)
    {
        var backend = (VirtualArena.Backend)backendValue;
        nuint size = VirtualArena.PageSize * 3 + 17;

        byte* block = (byte*)VirtualArena.Reserve(size, backend);
        try
        {
            Assert.True(block != null);
            Assert.True(IsZero(block, size));
            // Memory is writable end to end.
            block[0] = 1;
            block[(int)size - 1] = 2;
        }
        finally
        {
            VirtualArena.Release(block, size, backend);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ReserveOfZeroBytesYieldsAUsableBlock(byte backendValue)
    {
        var backend = (VirtualArena.Backend)backendValue;

        byte* block = (byte*)VirtualArena.Reserve(0, backend);
        try
        {
            Assert.True(block != null);
            Assert.Equal(0, block[0]);
            block[0] = 9;
        }
        finally
        {
            VirtualArena.Release(block, 0, backend);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void DiscardZeroesExactlyTheRequestedRange(byte backendValue)
    {
        var backend = (VirtualArena.Backend)backendValue;
        nuint page = VirtualArena.PageSize;
        nuint size = Math.Max((nuint)MiB, page * 24);
        // (offset, length): aligned, unaligned head, unaligned tail, both unaligned, sub-page, below the
        // memset threshold, the whole reservation, and an empty range.
        (nuint Offset, nuint Length)[] cases =
        {
            (page * 4, 256 * 1024),
            (page * 4 + 123, 200_000),
            (page * 4, 200_077),
            (page * 2 + 1, 300_001),
            (page * 5 + 10, 100),
            (page * 3, page * 3),
            (0, size),
            (page, 0),
        };

        byte* block = (byte*)VirtualArena.Reserve(size, backend);
        try
        {
            foreach (var (offset, length) in cases)
            {
                new Span<byte>(block, (int)size).Fill(0xFF);

                VirtualArena.Discard(At(block, offset), length, backend);

                string label = $"offset {offset}, length {length}, backend {backend}";
                Assert.True(IsAll(block, offset, 0xFF), $"bytes before the range changed: {label}");
                Assert.True(IsZero(At(block, offset), length), $"range not zeroed: {label}");
                Assert.True(IsAll(At(block, offset + length), size - offset - length, 0xFF), $"bytes after the range changed: {label}");
            }
        }
        finally
        {
            VirtualArena.Release(block, size, backend);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void DiscardOfNeverWrittenPagesReadsZero(byte backendValue)
    {
        var backend = (VirtualArena.Backend)backendValue;
        nuint size = (nuint)(4 * MiB);

        byte* block = (byte*)VirtualArena.Reserve(size, backend);
        try
        {
            VirtualArena.Discard(block + MiB, (nuint)(2 * MiB), backend);

            Assert.True(IsZero(block, size));
        }
        finally
        {
            VirtualArena.Release(block, size, backend);
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ReleaseOfNullIsANoOp(byte backendValue)
    {
        VirtualArena.Release(null, 0, (VirtualArena.Backend)backendValue);
        VirtualArena.Release(null, 4096, (VirtualArena.Backend)backendValue);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ReservedMemoryCanBeReusedAfterDiscard(byte backendValue)
    {
        var backend = (VirtualArena.Backend)backendValue;
        nuint size = (nuint)(2 * MiB);

        byte* block = (byte*)VirtualArena.Reserve(size, backend);
        try
        {
            for (int round = 0; round < 3; round++)
            {
                new Span<byte>(block, (int)size).Fill((byte)(round + 1));
                VirtualArena.Discard(block, size, backend);
                Assert.True(IsZero(block, size));
            }
        }
        finally
        {
            VirtualArena.Release(block, size, backend);
        }
    }

    [Fact]
    public void LargeReservationSucceedsWithoutBeingTouched()
    {
        if (!VirtualArena.IsLazy)
            Assert.Skip("The OS virtual-memory shim is not active; a 256 MiB calloc is not a lazy reservation.");

        nuint size = (nuint)(256 * MiB);
        byte* block = (byte*)VirtualArena.Reserve(size);
        try
        {
            Assert.True(IsZero(block, VirtualArena.PageSize));
            Assert.True(IsZero(At(block, size - VirtualArena.PageSize), VirtualArena.PageSize));
            block[128 * MiB] = 7;
            VirtualArena.Discard(block, size);
            Assert.Equal(0, block[128 * MiB]);
        }
        finally
        {
            VirtualArena.Release(block, size);
        }
    }

    [Fact]
    public void OsShimIsSelectedOnSupportedPlatforms()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        bool supported = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            && (architecture == Architecture.X64 || architecture == Architecture.Arm64);
        if (!supported)
            Assert.Skip($"No OS shim is expected on {architecture}.");

        if (!VirtualArena.IsLazy)
        {
            // The calloc fallback is correct, just not lazy, and legitimately engages in restricted environments
            // (seccomp-filtered madvise, unusual libc layouts). Only fail when the environment opts in to
            // requiring the OS shim; otherwise surface the reason as a skip diagnostic.
            string reason = $"VirtualArena fell back to calloc: {VirtualArena.FallbackReason}";
            if (Environment.GetEnvironmentVariable("DOTTY_REQUIRE_OS_SHIM") == "1")
                Assert.Fail(reason);
            Assert.Skip(reason);
        }

        Assert.Equal(string.Empty, VirtualArena.FallbackReason);
    }

    [Fact]
    public void BackendNameMatchesTheActiveBackend()
    {
        string expected = VirtualArena.ActiveBackend switch
        {
            VirtualArena.Backend.PosixMadvise => "mmap-madvise",
            VirtualArena.Backend.PosixRemap => "mmap-remap",
            VirtualArena.Backend.WindowsVirtual => "virtualalloc",
            _ => "alloc-zeroed",
        };

        Assert.Equal(expected, VirtualArena.BackendName);
        Assert.Equal(expected, BufferMemoryMetrics.ArenaBackend);
    }

    private static long ResidentBytes()
    {
        // statm: size resident shared text lib data dt (in pages)
        string[] fields = File.ReadAllText("/proc/self/statm").Split(' ');
        return long.Parse(fields[1]) * Environment.SystemPageSize;
    }

    [Fact]
    public void LinuxResidentMemoryTracksTouchedAndDiscardedPages()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Resident-memory accounting uses /proc/self/statm (Linux only).");
        if (VirtualArena.ActiveBackend != VirtualArena.Backend.PosixMadvise)
            Assert.Skip($"The madvise shim is not active ({VirtualArena.BackendName}: {VirtualArena.FallbackReason}).");

        // Generous tolerances: the runtime and the GC move RSS by a few MiB on their own.
        const long reserved = 64L * MiB;
        const long touched = 8L * MiB;
        GC.Collect();
        long baseline = ResidentBytes();

        byte* block = (byte*)VirtualArena.Reserve((nuint)reserved);
        try
        {
            long afterReserve = ResidentBytes();
            Assert.InRange(afterReserve - baseline, -4L * MiB, 4L * MiB);

            long page = (long)VirtualArena.PageSize;
            for (long offset = 0; offset < touched; offset += page)
                block[offset] = 1;
            long afterTouch = ResidentBytes();
            Assert.InRange(afterTouch - afterReserve, touched - 2L * MiB, touched + 3L * MiB);

            VirtualArena.Discard(block, (nuint)touched);
            long afterDiscard = ResidentBytes();
            Assert.InRange(afterTouch - afterDiscard, touched - 3L * MiB, touched + 3L * MiB);
            Assert.InRange(afterDiscard - afterReserve, -3L * MiB, 3L * MiB);
        }
        finally
        {
            VirtualArena.Release(block, (nuint)reserved);
        }
    }
}
