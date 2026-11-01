using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Zero-filled, lazily committed native memory for the screen cell arenas.
/// <list type="bullet">
/// <item><c>Reserve</c> returns page-rounded memory that reads as zero. Pages are not resident until
/// they are first written (Linux/macOS anonymous <c>mmap</c>; Windows commits charge up front but backs pages on
/// first touch).</item>
/// <item><c>Discard</c> makes a byte range read as zero again and hands whole inner pages back
/// to the OS (<c>madvise(MADV_DONTNEED)</c>; macOS remaps the range; Windows decommits and recommits it). Ranges
/// below <see cref="MemsetThreshold"/> are simply zeroed.</item>
/// <item><c>Release</c> returns the whole reservation.</item>
/// </list>
/// The OS shim is selected exactly once, at first use, by a self-test (reserve, dirty, discard, verify zero). Any
/// failure of symbol resolution, of a P/Invoke, or of the zero-fill guarantee selects <see cref="Backend.AllocZeroed"/>
/// for the life of the process: <see cref="NativeMemory.AllocZeroed"/> plus explicit zeroing in
/// <c>Discard</c> — correct, just not lazy. <c>Discard</c> and
/// <c>Release</c> never throw; a single failed OS discard degrades to zeroing that range.
/// <c>Reserve</c> throws <see cref="OutOfMemoryException"/> only when the memory cannot be obtained
/// (the same failure <c>Marshal.AllocHGlobal</c> reported before).
/// <para>
/// libc symbols are bound through static <c>[DllImport]</c> declarations against the logical library
/// <see cref="LibC"/>, which a <see cref="NativeLibrary.SetDllImportResolver"/> callback maps to the main program
/// handle (<c>dlopen(NULL)</c>; libc is always in its global scope). That avoids the "libc" / "libc.so.6" probing
/// differences between glibc, musl, bionic and macOS and uses no reflection, so it is NativeAOT-safe.
/// <c>SetLastError</c> is on only so a failed self-test can report the errno in <see cref="FallbackReason"/>; no
/// code path depends on it.
/// </para>
/// </summary>
internal static unsafe class VirtualArena
{
    internal enum Backend : byte
    {
        /// <summary>calloc-backed; <c>Discard</c> zeroes explicitly.</summary>
        AllocZeroed = 0,
        /// <summary>Linux/Android: <c>mmap</c> + <c>madvise(MADV_DONTNEED)</c>.</summary>
        PosixMadvise = 1,
        /// <summary>macOS: <c>mmap</c>, discard by remapping the range with <c>MAP_FIXED</c>.</summary>
        PosixRemap = 2,
        /// <summary>Windows: <c>VirtualAlloc</c>, discard by decommit + recommit.</summary>
        WindowsVirtual = 3,
    }

    /// <summary>Ranges smaller than this are zeroed with memset instead of a system call.</summary>
    internal const int MemsetThreshold = 64 * 1024;

    private const string LibC = "dotty-libc";

    private const int ProtReadWrite = 0x3;
    private const int MapPrivate = 0x2;
    private const int MapFixed = 0x10;
    private const int MapAnonymousLinux = 0x20;
    private const int MapNoReserveLinux = 0x4000;
    private const int MapAnonymousDarwin = 0x1000;
    private const int MadvDontNeed = 4;
    private const int MadvNoHugePage = 15;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemDecommit = 0x4000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    // Static initialisers run in textual order: everything Select() touches must be declared before s_backend.
    private static readonly nuint s_pageSize = (nuint)Environment.SystemPageSize;
    private static readonly string[] s_libcNames =
    {
        "libc.so.6",
        "libc",
        "libc.so",
        "/usr/lib/libSystem.B.dylib",
        "libSystem.B.dylib",
    };
    private static string? s_fallbackReason;
    private static readonly Backend s_backend = Select();

    [DllImport(LibC, EntryPoint = "mmap", SetLastError = true)]
    private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport(LibC, EntryPoint = "munmap", SetLastError = true)]
    private static extern int munmap(void* addr, nuint length);

    [DllImport(LibC, EntryPoint = "madvise", SetLastError = true)]
    private static extern int madvise(void* addr, nuint length, int advice);

    [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", EntryPoint = "VirtualFree", SetLastError = true)]
    private static extern int VirtualFree(void* address, nuint size, uint freeType);

    internal static nuint PageSize => s_pageSize;

    internal static Backend ActiveBackend => s_backend;

    /// <summary>True when memory is OS-backed and lazily committed (not the calloc fallback).</summary>
    internal static bool IsLazy => s_backend != Backend.AllocZeroed;

    /// <summary>Why the calloc fallback was selected; empty when the OS shim is active.</summary>
    internal static string FallbackReason => s_fallbackReason ?? string.Empty;

    internal static string BackendName => s_backend switch
    {
        Backend.PosixMadvise => "mmap-madvise",
        Backend.PosixRemap => "mmap-remap",
        Backend.WindowsVirtual => "virtualalloc",
        _ => "alloc-zeroed",
    };

    /// <summary>Reserves <paramref name="bytes"/> (rounded up to whole pages) of zero-filled memory.</summary>
    /// <exception cref="OutOfMemoryException">The memory could not be obtained.</exception>
    internal static void* Reserve(nuint bytes) => Reserve(bytes, s_backend);

    internal static void* Reserve(nuint bytes, Backend backend)
    {
        if (bytes == 0) bytes = 1;
        switch (backend)
        {
            case Backend.AllocZeroed:
                return NativeMemory.AllocZeroed(bytes);

            case Backend.WindowsVirtual:
                {
                    void* block = VirtualAlloc(null, RoundUpToPage(bytes), MemReserve | MemCommit, PageReadWrite);
                    if (block == null)
                        throw new OutOfMemoryException($"VirtualAlloc failed for {bytes} bytes (error {Marshal.GetLastPInvokeError()}).");
                    return block;
                }

            default:
                {
                    nuint length = RoundUpToPage(bytes);
                    bool linux = backend == Backend.PosixMadvise;
                    int flags = linux
                        ? MapPrivate | MapAnonymousLinux | MapNoReserveLinux
                        : MapPrivate | MapAnonymousDarwin;
                    void* block = mmap(null, length, ProtReadWrite, flags, -1, 0);
                    if (block == null || (nint)block == -1)
                        throw new OutOfMemoryException($"mmap failed for {bytes} bytes (errno {Marshal.GetLastPInvokeError()}).");
                    // Transparent huge pages would make a lone touched row commit 2 MiB. Best effort; failure is fine.
                    if (linux)
                        madvise(block, length, MadvNoHugePage);
                    return block;
                }
        }
    }

    /// <summary>Returns a reservation made by <c>Reserve</c>. Never throws.</summary>
    internal static void Release(void* block, nuint bytes) => Release(block, bytes, s_backend);

    internal static void Release(void* block, nuint bytes, Backend backend)
    {
        if (block == null) return;
        try
        {
            switch (backend)
            {
                case Backend.AllocZeroed:
                    NativeMemory.Free(block);
                    break;
                case Backend.WindowsVirtual:
                    VirtualFree(block, 0, MemRelease);
                    break;
                default:
                    munmap(block, RoundUpToPage(bytes == 0 ? 1 : bytes));
                    break;
            }
        }
        catch (Exception)
        {
            // Leaking the mapping is preferable to throwing out of Dispose.
        }
    }

    /// <summary>
    /// Makes <c>[block, block + bytes)</c> read as zero. Whole pages inside the range are returned to the OS; partial
    /// head/tail pages (and ranges below <see cref="MemsetThreshold"/>) are zeroed in place. Never throws.
    /// </summary>
    internal static void Discard(void* block, nuint bytes) => Discard(block, bytes, s_backend);

    internal static void Discard(void* block, nuint bytes, Backend backend)
    {
        if (bytes == 0) return;
        if (backend != Backend.AllocZeroed && bytes >= MemsetThreshold)
        {
            nuint page = s_pageSize;
            nuint mask = ~(page - 1);
            nuint start = (nuint)block;
            nuint end = start + bytes;
            nuint innerStart = (start + page - 1) & mask;
            nuint innerEnd = end & mask;
            if (innerEnd > innerStart)
            {
                if (innerStart > start)
                    NativeMemory.Clear(block, innerStart - start);
                if (end > innerEnd)
                    NativeMemory.Clear((void*)innerEnd, end - innerEnd);
                if (!OsDiscard((void*)innerStart, innerEnd - innerStart, backend))
                    NativeMemory.Clear((void*)innerStart, innerEnd - innerStart);
                return;
            }
        }

        NativeMemory.Clear(block, bytes);
    }

    private static bool OsDiscard(void* block, nuint length, Backend backend)
    {
        try
        {
            switch (backend)
            {
                case Backend.PosixMadvise:
                    return madvise(block, length, MadvDontNeed) == 0;
                case Backend.PosixRemap:
                    // Darwin's MADV_DONTNEED / MADV_FREE do not promise zero-fill; replacing the mapping does.
                    return mmap(block, length, ProtReadWrite, MapPrivate | MapAnonymousDarwin | MapFixed, -1, 0) == block;
                case Backend.WindowsVirtual:
                    return DecommitAndRecommit(block, length);
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DecommitAndRecommit(void* block, nuint length)
    {
        if (VirtualFree(block, length, MemDecommit) == 0)
            return false;

        // The pages are decommitted now: a failed recommit cannot be degraded to zeroing (touching them would fault).
        // We just released this commit charge, so failure means the system is out of commit; retry briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (VirtualAlloc(block, length, MemCommit, PageReadWrite) != null)
                return true;
            Thread.Sleep(attempt);
        }

        Environment.FailFast("Dotty: unable to recommit discarded terminal arena pages (out of commit charge).");
        return false;
    }

    private static nuint RoundUpToPage(nuint bytes) => (bytes + s_pageSize - 1) & ~(s_pageSize - 1);

    // ---- one-time backend selection ----------------------------------------------------------------------

    private static Backend Select()
    {
        Backend candidate = PreferredBackend();
        if (candidate == Backend.AllocZeroed)
        {
            s_fallbackReason = "no OS virtual-memory shim for this platform";
            return Backend.AllocZeroed;
        }

        try
        {
            if (candidate != Backend.WindowsVirtual)
                NativeLibrary.SetDllImportResolver(typeof(VirtualArena).Assembly, ResolveLibC);

            string? failure = SelfTest(candidate);
            if (failure is null)
                return candidate;
            s_fallbackReason = failure;
        }
        catch (Exception ex)
        {
            s_fallbackReason = $"{ex.GetType().Name}: {ex.Message}";
        }

        return Backend.AllocZeroed;
    }

    private static Backend PreferredBackend()
    {
        if (OperatingSystem.IsWindows()) return Backend.WindowsVirtual;
        if (OperatingSystem.IsMacOS()) return Backend.PosixRemap;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            // The flag values above are the generic/x86/arm ones; other ABIs (mips, ppc, sparc) differ.
            Architecture architecture = RuntimeInformation.ProcessArchitecture;
            bool knownAbi = architecture == Architecture.X64 || architecture == Architecture.Arm64
                || architecture == Architecture.X86 || architecture == Architecture.Arm;
            return knownAbi ? Backend.PosixMadvise : Backend.AllocZeroed;
        }

        return Backend.AllocZeroed;
    }

    /// <summary>Returns null when <paramref name="backend"/> honours the contract, else the reason it does not.</summary>
    private static string? SelfTest(Backend backend)
    {
        nuint page = s_pageSize;
        nuint length = page * 4;
        byte* block = null;
        try
        {
            block = (byte*)Reserve(length, backend);
            var all = new Span<byte>(block, (int)length);
            if (!IsZero(block, length))
                return "fresh reservation is not zero-filled";

            all.Fill(0xA5);
            int pageBytes = (int)page;
            if (!OsDiscard(block + pageBytes, page * 2, backend))
                return $"OS discard call failed (errno {Marshal.GetLastPInvokeError()})";
            if (!IsZero(block + pageBytes, page * 2))
                return "discarded pages did not read back as zero";
            if (all[0] != 0xA5 || all[pageBytes * 3] != 0xA5 || all[(int)length - 1] != 0xA5)
                return "discard modified pages outside the requested range";
            return null;
        }
        finally
        {
            if (block != null)
                Release(block, length, backend);
        }
    }

    private static bool IsZero(byte* start, nuint length) =>
        new ReadOnlySpan<byte>(start, (int)length).IndexOfAnyExcept((byte)0) < 0;

    private static IntPtr ResolveLibC(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibC, StringComparison.Ordinal))
            return IntPtr.Zero;

        IntPtr main = NativeLibrary.GetMainProgramHandle();
        if (main != IntPtr.Zero && NativeLibrary.TryGetExport(main, "mmap", out _))
            return main;

        foreach (string name in s_libcNames)
        {
            if (NativeLibrary.TryLoad(name, out IntPtr handle) && NativeLibrary.TryGetExport(handle, "mmap", out _))
                return handle;
        }

        return IntPtr.Zero;
    }
}
