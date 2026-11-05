using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dotty.Performance.Tests.Infrastructure;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Experiment-only A/B of printable-ASCII run scanners (plan 08, WI-1a / WI-P1). Never part of the gate:
/// it is not in <c>Program.BenchmarkTypes</c>, runs only when <c>--filter</c> contains "experiment", and its
/// summaries are never fed to baseline checking or baseline updates.
///
/// Every scanner answers "index of the first byte of the run that ends a printable run, or the span length".
/// The inputs are <c>N</c> printable ASCII bytes followed by one CR, so the current parser predicate
/// (ESC, 0x07-0x0D, 0x7F and every byte &gt;= 0x80) and the "outside 0x20..0x7E" predicate agree and
/// <c>SetupBenchmark</c> verifies every variant against a scalar reference on every buffer (it throws on any
/// disagreement). Stray C0 bytes (0x00-0x06, 0x0E-0x1A, 0x1C-0x1F) are the one place the two predicates
/// differ and are deliberately absent from the buffers (plan 08, WI-P1 behavior-change note).
///
/// One method per (variant, length), no <c>[Params]</c>. BenchmarkDotNet allows one baseline per logical
/// group, so each length is its own category group (<see cref="BenchmarkLogicalGroupRule.ByCategory"/>); the
/// current <see cref="SearchValues{T}"/> variant is the baseline of every group.
///
/// The "arm64 MaxAcross" variant falls back to the portable Vector128 scanner on hosts without AdvSimd.Arm64
/// (so on x64 its row duplicates the Vector128 row); "Vector512" and "Vector256" fall back to the next
/// narrower width when not hardware accelerated.
/// </summary>
[BenchmarkCategory("Experiment", "Scan")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ParserScanExperiments : PerformanceTestBase
{
    private static readonly SearchValues<byte> s_printableRunBreaks = CreateCurrentParserBreakSet();

    private byte[] _len4 = null!;
    private byte[] _len8 = null!;
    private byte[] _len16 = null!;
    private byte[] _len22 = null!;
    private byte[] _len55 = null!;
    private byte[] _len80 = null!;
    private byte[] _len256 = null!;
    private byte[] _len4096 = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _len4 = BuildBuffer(4);
        _len8 = BuildBuffer(8);
        _len16 = BuildBuffer(16);
        _len22 = BuildBuffer(22);
        _len55 = BuildBuffer(55);
        _len80 = BuildBuffer(80);
        _len256 = BuildBuffer(256);
        _len4096 = BuildBuffer(4096);
        VerifyAllVariants();
    }

    // Same set as BasicAnsiParser.s_printableRunBreaks: ESC, BEL..CR (0x07-0x0D), DEL and 0x80..0xFF.
    private static SearchValues<byte> CreateCurrentParserBreakSet()
    {
        Span<byte> values = stackalloc byte[9 + 128];
        values[0] = 0x1B;
        for (int i = 0; i < 7; i++)
            values[1 + i] = (byte)(0x07 + i);
        values[8] = 0x7F;
        for (int i = 0; i < 128; i++)
            values[9 + i] = (byte)(0x80 + i);
        return SearchValues.Create(values);
    }

    // N printable bytes ('a'..'z', digits, space) then one CR: the break is at index N.
    private static byte[] BuildBuffer(int printableLength)
    {
        var buffer = new byte[printableLength + 1];
        for (int i = 0; i < printableLength; i++)
            buffer[i] = (byte)(0x20 + 1 + i % 0x5D); // 0x21..0x7D, never a break byte
        buffer[printableLength] = (byte)'\r';
        return buffer;
    }

    private void VerifyAllVariants()
    {
        foreach (byte[] buffer in new[] { _len4, _len8, _len16, _len22, _len55, _len80, _len256, _len4096 })
        {
            int expected = ScanScalar(buffer);
            if (expected != buffer.Length - 1)
                throw new InvalidOperationException("Scalar reference disagrees with the constructed break position.");

            // Also check every suffix so the vector tails (overlapping final load, sub-vector spans) are covered.
            for (int start = 0; start < buffer.Length; start++)
            {
                var span = buffer.AsSpan(start);
                int reference = ScanScalar(span);
                Check(nameof(ScanSearchValues), ScanSearchValues(span), reference, buffer.Length, start);
                Check(nameof(ScanExceptInRange), ScanExceptInRange(span), reference, buffer.Length, start);
                Check(nameof(ScanVector128), ScanVector128(span), reference, buffer.Length, start);
                Check(nameof(ScanVector256), ScanVector256(span), reference, buffer.Length, start);
                Check(nameof(ScanVector512), ScanVector512(span), reference, buffer.Length, start);
                Check(nameof(ScanArm64MaxAcross), ScanArm64MaxAcross(span), reference, buffer.Length, start);
            }
        }
    }

    private static void Check(string variant, int actual, int expected, int bufferLength, int start)
    {
        if (actual != expected)
            throw new InvalidOperationException(
                $"{variant} returned {actual}, scalar reference {expected} (buffer {bufferLength}, offset {start}).");
    }

    #region Scanners

    /// <summary>Scalar reference: first byte outside 0x20..0x7E.</summary>
    private static int ScanScalar(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if ((uint)(span[i] - 0x20) > 0x5E)
                return i;
        }

        return span.Length;
    }

    /// <summary>Variant 1: the parser's current predicate via <see cref="SearchValues{T}"/>.</summary>
    private static int ScanSearchValues(ReadOnlySpan<byte> span)
    {
        int index = span.IndexOfAny(s_printableRunBreaks);
        return index < 0 ? span.Length : index;
    }

    /// <summary>Variant 2: BCL range search for the first byte outside 0x20..0x7E.</summary>
    private static int ScanExceptInRange(ReadOnlySpan<byte> span)
    {
        int index = span.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
        return index < 0 ? span.Length : index;
    }

    /// <summary>Variant 3: hand-written Vector128, one unsigned range compare per 16 bytes, overlapping tail.</summary>
    private static int ScanVector128(ReadOnlySpan<byte> span)
    {
        if (!Vector128.IsHardwareAccelerated || span.Length < Vector128<byte>.Count)
            return ScanScalar(span);

        ref byte start = ref MemoryMarshal.GetReference(span);
        int lastOffset = span.Length - Vector128<byte>.Count;
        Vector128<byte> bias = Vector128.Create((byte)0x20);
        Vector128<byte> limit = Vector128.Create((byte)0x5E);

        int offset = 0;
        while (offset < lastOffset)
        {
            var mask = Vector128.GreaterThan(Vector128.LoadUnsafe(ref start, (nuint)offset) - bias, limit);
            if (mask != Vector128<byte>.Zero)
                return offset + BitOperations.TrailingZeroCount(mask.ExtractMostSignificantBits());
            offset += Vector128<byte>.Count;
        }

        // Final (possibly overlapping) load: everything before `offset` is already known to be printable.
        var tail = Vector128.GreaterThan(Vector128.LoadUnsafe(ref start, (nuint)lastOffset) - bias, limit);
        return tail != Vector128<byte>.Zero
            ? lastOffset + BitOperations.TrailingZeroCount(tail.ExtractMostSignificantBits())
            : span.Length;
    }

    /// <summary>Variant 4a: Vector256 when hardware accelerated, else Vector128.</summary>
    private static int ScanVector256(ReadOnlySpan<byte> span)
    {
        if (!Vector256.IsHardwareAccelerated || span.Length < Vector256<byte>.Count)
            return ScanVector128(span);

        ref byte start = ref MemoryMarshal.GetReference(span);
        int lastOffset = span.Length - Vector256<byte>.Count;
        Vector256<byte> bias = Vector256.Create((byte)0x20);
        Vector256<byte> limit = Vector256.Create((byte)0x5E);

        int offset = 0;
        while (offset < lastOffset)
        {
            var mask = Vector256.GreaterThan(Vector256.LoadUnsafe(ref start, (nuint)offset) - bias, limit);
            if (mask != Vector256<byte>.Zero)
                return offset + BitOperations.TrailingZeroCount(mask.ExtractMostSignificantBits());
            offset += Vector256<byte>.Count;
        }

        var tail = Vector256.GreaterThan(Vector256.LoadUnsafe(ref start, (nuint)lastOffset) - bias, limit);
        return tail != Vector256<byte>.Zero
            ? lastOffset + BitOperations.TrailingZeroCount(tail.ExtractMostSignificantBits())
            : span.Length;
    }

    /// <summary>Variant 4b: Vector512 when hardware accelerated, else Vector256/128.</summary>
    private static int ScanVector512(ReadOnlySpan<byte> span)
    {
        if (!Vector512.IsHardwareAccelerated || span.Length < Vector512<byte>.Count)
            return ScanVector256(span);

        ref byte start = ref MemoryMarshal.GetReference(span);
        int lastOffset = span.Length - Vector512<byte>.Count;
        Vector512<byte> bias = Vector512.Create((byte)0x20);
        Vector512<byte> limit = Vector512.Create((byte)0x5E);

        int offset = 0;
        while (offset < lastOffset)
        {
            var mask = Vector512.GreaterThan(Vector512.LoadUnsafe(ref start, (nuint)offset) - bias, limit);
            if (mask != Vector512<byte>.Zero)
                return offset + BitOperations.TrailingZeroCount(mask.ExtractMostSignificantBits());
            offset += Vector512<byte>.Count;
        }

        var tail = Vector512.GreaterThan(Vector512.LoadUnsafe(ref start, (nuint)lastOffset) - bias, limit);
        return tail != Vector512<byte>.Zero
            ? lastOffset + BitOperations.TrailingZeroCount(tail.ExtractMostSignificantBits())
            : span.Length;
    }

    /// <summary>
    /// Variant 5: arm64 "any break in these 16 bytes" check with <c>MaxAcross</c> (cheaper than the multi-instruction
    /// <c>ExtractMostSignificantBits</c> emulation), then the portable locate step only for the block that has one.
    /// Falls back to <see cref="ScanVector128"/> when AdvSimd.Arm64 is unavailable.
    /// </summary>
    private static int ScanArm64MaxAcross(ReadOnlySpan<byte> span)
    {
        if (!AdvSimd.Arm64.IsSupported || span.Length < Vector128<byte>.Count)
            return ScanVector128(span);

        ref byte start = ref MemoryMarshal.GetReference(span);
        int lastOffset = span.Length - Vector128<byte>.Count;
        Vector128<byte> bias = Vector128.Create((byte)0x20);
        Vector128<byte> limit = Vector128.Create((byte)0x5E);

        int offset = 0;
        while (offset < lastOffset)
        {
            var mask = Vector128.GreaterThan(Vector128.LoadUnsafe(ref start, (nuint)offset) - bias, limit);
            if (AdvSimd.Arm64.MaxAcross(mask).ToScalar() != 0)
                return offset + BitOperations.TrailingZeroCount(mask.ExtractMostSignificantBits());
            offset += Vector128<byte>.Count;
        }

        var tail = Vector128.GreaterThan(Vector128.LoadUnsafe(ref start, (nuint)lastOffset) - bias, limit);
        return AdvSimd.Arm64.MaxAcross(tail).ToScalar() != 0
            ? lastOffset + BitOperations.TrailingZeroCount(tail.ExtractMostSignificantBits())
            : span.Length;
    }

    #endregion

    #region 4 bytes

    [BenchmarkCategory("Len4"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 4B")]
    public int SearchValues_4() => ScanSearchValues(_len4);

    [BenchmarkCategory("Len4"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 4B")]
    public int ExceptInRange_4() => ScanExceptInRange(_len4);

    [BenchmarkCategory("Len4"), Benchmark(Description = "Scan experiment: Vector128 4B")]
    public int Vector128_4() => ScanVector128(_len4);

    [BenchmarkCategory("Len4"), Benchmark(Description = "Scan experiment: Vector256 4B")]
    public int Vector256_4() => ScanVector256(_len4);

    [BenchmarkCategory("Len4"), Benchmark(Description = "Scan experiment: Vector512 4B")]
    public int Vector512_4() => ScanVector512(_len4);

    [BenchmarkCategory("Len4"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 4B")]
    public int Arm64MaxAcross_4() => ScanArm64MaxAcross(_len4);

    #endregion

    #region 8 bytes

    [BenchmarkCategory("Len8"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 8B")]
    public int SearchValues_8() => ScanSearchValues(_len8);

    [BenchmarkCategory("Len8"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 8B")]
    public int ExceptInRange_8() => ScanExceptInRange(_len8);

    [BenchmarkCategory("Len8"), Benchmark(Description = "Scan experiment: Vector128 8B")]
    public int Vector128_8() => ScanVector128(_len8);

    [BenchmarkCategory("Len8"), Benchmark(Description = "Scan experiment: Vector256 8B")]
    public int Vector256_8() => ScanVector256(_len8);

    [BenchmarkCategory("Len8"), Benchmark(Description = "Scan experiment: Vector512 8B")]
    public int Vector512_8() => ScanVector512(_len8);

    [BenchmarkCategory("Len8"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 8B")]
    public int Arm64MaxAcross_8() => ScanArm64MaxAcross(_len8);

    #endregion

    #region 16 bytes

    [BenchmarkCategory("Len16"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 16B")]
    public int SearchValues_16() => ScanSearchValues(_len16);

    [BenchmarkCategory("Len16"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 16B")]
    public int ExceptInRange_16() => ScanExceptInRange(_len16);

    [BenchmarkCategory("Len16"), Benchmark(Description = "Scan experiment: Vector128 16B")]
    public int Vector128_16() => ScanVector128(_len16);

    [BenchmarkCategory("Len16"), Benchmark(Description = "Scan experiment: Vector256 16B")]
    public int Vector256_16() => ScanVector256(_len16);

    [BenchmarkCategory("Len16"), Benchmark(Description = "Scan experiment: Vector512 16B")]
    public int Vector512_16() => ScanVector512(_len16);

    [BenchmarkCategory("Len16"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 16B")]
    public int Arm64MaxAcross_16() => ScanArm64MaxAcross(_len16);

    #endregion

    #region 22 bytes

    [BenchmarkCategory("Len22"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 22B")]
    public int SearchValues_22() => ScanSearchValues(_len22);

    [BenchmarkCategory("Len22"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 22B")]
    public int ExceptInRange_22() => ScanExceptInRange(_len22);

    [BenchmarkCategory("Len22"), Benchmark(Description = "Scan experiment: Vector128 22B")]
    public int Vector128_22() => ScanVector128(_len22);

    [BenchmarkCategory("Len22"), Benchmark(Description = "Scan experiment: Vector256 22B")]
    public int Vector256_22() => ScanVector256(_len22);

    [BenchmarkCategory("Len22"), Benchmark(Description = "Scan experiment: Vector512 22B")]
    public int Vector512_22() => ScanVector512(_len22);

    [BenchmarkCategory("Len22"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 22B")]
    public int Arm64MaxAcross_22() => ScanArm64MaxAcross(_len22);

    #endregion

    #region 55 bytes

    [BenchmarkCategory("Len55"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 55B")]
    public int SearchValues_55() => ScanSearchValues(_len55);

    [BenchmarkCategory("Len55"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 55B")]
    public int ExceptInRange_55() => ScanExceptInRange(_len55);

    [BenchmarkCategory("Len55"), Benchmark(Description = "Scan experiment: Vector128 55B")]
    public int Vector128_55() => ScanVector128(_len55);

    [BenchmarkCategory("Len55"), Benchmark(Description = "Scan experiment: Vector256 55B")]
    public int Vector256_55() => ScanVector256(_len55);

    [BenchmarkCategory("Len55"), Benchmark(Description = "Scan experiment: Vector512 55B")]
    public int Vector512_55() => ScanVector512(_len55);

    [BenchmarkCategory("Len55"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 55B")]
    public int Arm64MaxAcross_55() => ScanArm64MaxAcross(_len55);

    #endregion

    #region 80 bytes

    [BenchmarkCategory("Len80"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 80B")]
    public int SearchValues_80() => ScanSearchValues(_len80);

    [BenchmarkCategory("Len80"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 80B")]
    public int ExceptInRange_80() => ScanExceptInRange(_len80);

    [BenchmarkCategory("Len80"), Benchmark(Description = "Scan experiment: Vector128 80B")]
    public int Vector128_80() => ScanVector128(_len80);

    [BenchmarkCategory("Len80"), Benchmark(Description = "Scan experiment: Vector256 80B")]
    public int Vector256_80() => ScanVector256(_len80);

    [BenchmarkCategory("Len80"), Benchmark(Description = "Scan experiment: Vector512 80B")]
    public int Vector512_80() => ScanVector512(_len80);

    [BenchmarkCategory("Len80"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 80B")]
    public int Arm64MaxAcross_80() => ScanArm64MaxAcross(_len80);

    #endregion

    #region 256 bytes

    [BenchmarkCategory("Len256"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 256B")]
    public int SearchValues_256() => ScanSearchValues(_len256);

    [BenchmarkCategory("Len256"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 256B")]
    public int ExceptInRange_256() => ScanExceptInRange(_len256);

    [BenchmarkCategory("Len256"), Benchmark(Description = "Scan experiment: Vector128 256B")]
    public int Vector128_256() => ScanVector128(_len256);

    [BenchmarkCategory("Len256"), Benchmark(Description = "Scan experiment: Vector256 256B")]
    public int Vector256_256() => ScanVector256(_len256);

    [BenchmarkCategory("Len256"), Benchmark(Description = "Scan experiment: Vector512 256B")]
    public int Vector512_256() => ScanVector512(_len256);

    [BenchmarkCategory("Len256"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 256B")]
    public int Arm64MaxAcross_256() => ScanArm64MaxAcross(_len256);

    #endregion

    #region 4096 bytes

    [BenchmarkCategory("Len4096"), Benchmark(Baseline = true, Description = "Scan experiment: SearchValues 4096B")]
    public int SearchValues_4096() => ScanSearchValues(_len4096);

    [BenchmarkCategory("Len4096"), Benchmark(Description = "Scan experiment: IndexOfAnyExceptInRange 4096B")]
    public int ExceptInRange_4096() => ScanExceptInRange(_len4096);

    [BenchmarkCategory("Len4096"), Benchmark(Description = "Scan experiment: Vector128 4096B")]
    public int Vector128_4096() => ScanVector128(_len4096);

    [BenchmarkCategory("Len4096"), Benchmark(Description = "Scan experiment: Vector256 4096B")]
    public int Vector256_4096() => ScanVector256(_len4096);

    [BenchmarkCategory("Len4096"), Benchmark(Description = "Scan experiment: Vector512 4096B")]
    public int Vector512_4096() => ScanVector512(_len4096);

    [BenchmarkCategory("Len4096"), Benchmark(Description = "Scan experiment: arm64 MaxAcross 4096B")]
    public int Arm64MaxAcross_4096() => ScanArm64MaxAcross(_len4096);

    #endregion
}
