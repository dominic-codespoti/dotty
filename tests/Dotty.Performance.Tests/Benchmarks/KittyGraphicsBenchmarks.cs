using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.Abstractions.Graphics;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

// Kitty graphics benchmarks (plan 06, section 5.3). Every class here is an EXPERIMENT: it is deliberately absent from
// Program.BenchmarkTypes (so it has no baselines and is never gated) and runs only for `--filter experiment`.
// Nothing below states a measured number; every expectation is a hypothesis to be checked on a machine with dotnet.
//
// Only public API is used (this assembly has no InternalsVisibleTo): TerminalAdapter.ConfigureGraphics, the parser and
// the adapter's buffer. Each fixture's SetupBenchmark checks the effect of its stream through the public
// GraphicsMemoryMetrics counters and throws when a payload was rejected (a rejected payload would otherwise look like
// a very fast benchmark). Every fixture disposes its adapters in CleanupBenchmark and reports, on stderr, any drift of the
// process-wide GraphicsMemoryMetrics from the value captured before its setup.
//
// The existing parser benchmarks (ParserBenchmarks, ParserScanBenchmarks, ParserOscBenchmarks, BulkOutputBenchmark)
// are the regression guard for the APC bulk path's effect on non-graphics parsing: they are NOT modified and need no
// change; compare their gated results before and after the graphics work.

/// <summary>Shared builders, a fake PNG decoder and the metrics-restore check for the Kitty benchmark fixtures.</summary>
internal static class KittyBenchSupport
{
    public const string Esc = "\u001b";
    public const string St = "\u001b\\";

    /// <summary>The PTY consumer feeds the parser in slices of this size, one SyncRoot hold per slice.</summary>
    public const int FeedSlice = 8192;

    /// <summary>One 1x1 RGBA pixel (four zero bytes) in base64.</summary>
    public const string OnePixelBase64 = "AAAAAA==";

    public static KittyGraphicsOptions Options(int maxPlacements = 20_000) => new()
    {
        Enabled = true,
        StorageLimitBytes = 1L << 30,
        MaxPayloadBytes = 256L << 20,
        MaxPlacementsPerScreen = maxPlacements,
    };

    private static void IgnoreReply(ReadOnlySpan<char> reply) { }

    public static TerminalAdapter CreateAdapter(int scrollback, bool graphics, IImageDecoder? decoder = null, int maxPlacements = 20_000)
    {
        var adapter = new TerminalAdapter(24, 80, scrollbackCapacity: scrollback);
        adapter.ReplyRequested += IgnoreReply;
        if (graphics)
            adapter.ConfigureGraphics(Options(maxPlacements), decoder);
        return adapter;
    }

    /// <summary>Feeds <paramref name="data"/> like <c>TerminalSession.ConsumePtyOutput</c>: 8 KiB slices, one SyncRoot hold each.</summary>
    public static void FeedLocked(TerminalAdapter adapter, BasicAnsiParser parser, ReadOnlySpan<byte> data)
    {
        object root = adapter.Buffer.SyncRoot;
        while (!data.IsEmpty)
        {
            int take = Math.Min(FeedSlice, data.Length);
            bool taken = false;
            try
            {
                Monitor.Enter(root, ref taken);
                parser.Feed(data[..take]);
            }
            finally
            {
                if (taken)
                    Monitor.Exit(root);
            }
            data = data[take..];
        }
    }

    /// <summary>
    /// A whole transmission as APC chunks of <paramref name="rawChunk"/> payload bytes (3072 raw = 4096 base64 chars,
    /// the protocol's chunk size): the first chunk carries <paramref name="firstKeys"/> plus <c>m</c>, the rest only <c>m</c>.
    /// </summary>
    public static byte[] BuildChunked(string firstKeys, ReadOnlySpan<byte> payload, int rawChunk = 3072)
    {
        if (rawChunk <= 0 || rawChunk % 3 != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 3 so only the last chunk is padded.", nameof(rawChunk));
        int chunks = Math.Max(1, (payload.Length + rawChunk - 1) / rawChunk);
        byte[] first = Encoding.ASCII.GetBytes(Esc + "_G" + firstKeys + (chunks == 1 ? ",m=0;" : ",m=1;"));
        byte[] middle = Encoding.ASCII.GetBytes(Esc + "_Gm=1;");
        byte[] last = Encoding.ASCII.GetBytes(Esc + "_Gm=0;");
        byte[] trailer = Encoding.ASCII.GetBytes(St);

        long total = 0;
        for (int c = 0; c < chunks; c++)
        {
            int n = c == chunks - 1 ? payload.Length - c * rawChunk : rawChunk;
            total += HeaderFor(c, chunks, first, middle, last).Length + Base64.GetMaxEncodedToUtf8Length(n) + trailer.Length;
        }
        if (total > Array.MaxLength)
            throw new InvalidOperationException("Stream does not fit in one array.");

        var dst = new byte[total];
        int pos = 0;
        for (int c = 0; c < chunks; c++)
        {
            int off = c * rawChunk;
            int n = c == chunks - 1 ? payload.Length - off : rawChunk;
            byte[] header = HeaderFor(c, chunks, first, middle, last);
            header.CopyTo(dst, pos);
            pos += header.Length;
            if (Base64.EncodeToUtf8(payload.Slice(off, n), dst.AsSpan(pos), out _, out int written, isFinalBlock: true) != OperationStatus.Done)
                throw new InvalidOperationException("Base64 encoding failed.");
            pos += written;
            trailer.CopyTo(dst, pos);
            pos += trailer.Length;
        }
        if (pos != dst.Length)
            throw new InvalidOperationException("Stream length miscomputed.");
        return dst;
    }

    private static byte[] HeaderFor(int chunk, int chunks, byte[] first, byte[] middle, byte[] last) =>
        chunk == 0 ? first : chunk == chunks - 1 ? last : middle;

    /// <summary>The same transmission as one APC (no <c>m</c> key).</summary>
    public static byte[] BuildSingle(string keys, ReadOnlySpan<byte> payload)
    {
        byte[] header = Encoding.ASCII.GetBytes(Esc + "_G" + keys + ";");
        byte[] trailer = Encoding.ASCII.GetBytes(St);
        var dst = new byte[header.Length + Base64.GetMaxEncodedToUtf8Length(payload.Length) + trailer.Length];
        header.CopyTo(dst, 0);
        if (Base64.EncodeToUtf8(payload, dst.AsSpan(header.Length), out _, out int written, isFinalBlock: true) != OperationStatus.Done
            || header.Length + written + trailer.Length != dst.Length)
            throw new InvalidOperationException("Base64 encoding failed.");
        trailer.CopyTo(dst, header.Length + written);
        return dst;
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>Pseudo-random bytes (a fixed seed): incompressible, alpha varies, so premultiply has real work.</summary>
    public static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        new Random(12345).NextBytes(data);
        return data;
    }

    /// <summary>A smooth RGBA gradient with varying alpha: compresses, as screenshots and renders do.</summary>
    public static byte[] GradientRgba(int width, int height)
    {
        var data = new byte[checked(width * height * 4)];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            byte g = (byte)(y * 255 / Math.Max(1, height - 1));
            for (int x = 0; x < width; x++)
            {
                data[i++] = (byte)(x * 255 / Math.Max(1, width - 1));
                data[i++] = g;
                data[i++] = (byte)((x ^ y) & 0xFF);
                data[i++] = (byte)(64 + ((x >> 5) & 0x7F));
            }
        }
        return data;
    }

    public static byte[] ZlibCompress(ReadOnlySpan<byte> raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Control sequence that transmits one 1x1 image with client id 1 (no reply).</summary>
    public static byte[] OnePixelImage() =>
        Ascii(Esc + "_Ga=t,f=32,s=1,v=1,i=1,q=2;" + OnePixelBase64 + St);

    /// <summary><paramref name="count"/> placements (ids 1..count) of image 1 at the cursor cell, <c>C=1</c> so the cursor stays.</summary>
    public static string Placements(int count, int firstId = 1)
    {
        var sb = new StringBuilder(count * 32);
        for (int p = 0; p < count; p++)
            sb.Append(Esc).Append("_Ga=p,i=1,p=").Append(firstId + p).Append(",C=1,q=2").Append(St);
        return sb.ToString();
    }

    public static long LivePlacements => GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;

    public static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException("Kitty benchmark fixture check failed: " + what);
    }

    /// <summary>Writes to stderr when the process-wide graphics counters did not return to <paramref name="before"/>.</summary>
    public static void WarnIfNotRestored(string fixture, GraphicsMemorySnapshot before)
    {
        GraphicsMemorySnapshot now = GraphicsMemoryMetrics.GetSnapshot();
        if (now.LiveImagePixelBytes != before.LiveImagePixelBytes
            || now.LiveImageCount != before.LiveImageCount
            || now.PendingTransferBytes != before.PendingTransferBytes
            || now.LivePlacementCount != before.LivePlacementCount)
        {
            Console.Error.WriteLine(
                $"[{fixture}] GraphicsMemoryMetrics did not return to its pre-setup value: "
                + $"liveBytes {before.LiveImagePixelBytes}->{now.LiveImagePixelBytes}, images {before.LiveImageCount}->{now.LiveImageCount}, "
                + $"pending {before.PendingTransferBytes}->{now.PendingTransferBytes}, placements {before.LivePlacementCount}->{now.LivePlacementCount}.");
        }
    }
}

/// <summary>Stand-in PNG decoder: touches every output byte (like a real decode would) and reports success.</summary>
public sealed class FillingImageDecoder : IImageDecoder
{
    public ImageDecodeStatus DecodePngRgba(ReadOnlySpan<byte> png, int width, int height, Span<byte> straightRgba)
    {
        straightRgba.Fill(0x80);
        return ImageDecodeStatus.Ok;
    }
}

/// <summary>
/// Plan 5.3 (1): APC payload scan and base64 decode throughput for a 64 MiB direct transmit, through
/// <see cref="BasicAnsiParser"/> and <see cref="TerminalAdapter"/>. The scan-only variant runs the identical bytes with
/// graphics disabled (the parser discards the APC without buffering), isolating the SearchValues scan from decode, copy
/// and image commit. Hypotheses: the scan-only variant is bound by memory bandwidth and the decode variants by the
/// base64 decoder plus one copy into native staging; the chunked and single-APC forms differ only by per-APC control
/// parsing (about 21 thousand APCs).
/// </summary>
[BenchmarkCategory("Graphics", "Transmit")]
public class KittyApcDecodeBenchmarks : PerformanceTestBase
{
    // 4096 x 5461 x 3 bytes = 67,104,768 raw bytes (64 MiB less 4 KiB, because 64 MiB is not a multiple of 3).
    private const int Width = 4096;
    private const int Height = 5461;

    protected override bool UseNoGcRegion => false;

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _graphicsAdapter = null!;
    private BasicAnsiParser _graphicsParser = null!;
    private TerminalAdapter _discardAdapter = null!;
    private BasicAnsiParser _discardParser = null!;
    private byte[] _chunked = null!;
    private byte[] _single = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();

        string keys = $"a=t,f=24,s={Width},v={Height},i=1,q=2";
        byte[] raw = KittyBenchSupport.RandomBytes(Width * Height * 3);
        _chunked = KittyBenchSupport.BuildChunked(keys, raw);
        _single = KittyBenchSupport.BuildSingle(keys, raw);

        _graphicsAdapter = KittyBenchSupport.CreateAdapter(scrollback: 0, graphics: true);
        _graphicsParser = new BasicAnsiParser { Handler = _graphicsAdapter };
        _discardAdapter = KittyBenchSupport.CreateAdapter(scrollback: 0, graphics: false);
        _discardParser = new BasicAnsiParser { Handler = _discardAdapter };

        VerifyStored(_chunked);
        VerifyStored(_single);
        KittyBenchSupport.FeedLocked(_discardAdapter, _discardParser, _chunked); // JIT the discard path
    }

    private void VerifyStored(byte[] stream)
    {
        KittyBenchSupport.FeedLocked(_graphicsAdapter, _graphicsParser, stream);
        GraphicsMemorySnapshot now = GraphicsMemoryMetrics.GetSnapshot();
        KittyBenchSupport.Require(
            now.LiveImageCount - _metricsBefore.LiveImageCount == 1
            && now.LiveImagePixelBytes - _metricsBefore.LiveImagePixelBytes == (long)Width * Height * 3,
            "the 64 MiB f=24 transmit must store exactly one RGB8 image of Width*Height*3 bytes");
    }

    public override void CleanupBenchmark()
    {
        _graphicsAdapter.Dispose();
        _discardAdapter.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittyApcDecodeBenchmarks), _metricsBefore);
        base.CleanupBenchmark();
    }

    [Benchmark(Description = "Kitty APC: 64 MiB f=24, 4 KiB chunks, graphics disabled (scan + discard)")]
    public void Scan_Discard_Chunked() =>
        KittyBenchSupport.FeedLocked(_discardAdapter, _discardParser, _chunked);

    [Benchmark(Description = "Kitty APC: 64 MiB f=24, 4 KiB chunks, decode + store")]
    public void Decode_Chunked() =>
        KittyBenchSupport.FeedLocked(_graphicsAdapter, _graphicsParser, _chunked);

    [Benchmark(Description = "Kitty APC: 64 MiB f=24, single APC, decode + store")]
    public void Decode_SingleApc() =>
        KittyBenchSupport.FeedLocked(_graphicsAdapter, _graphicsParser, _single);
}

/// <summary>
/// Plan 5.3 (2): <c>f=32</c> 4096x4096 end to end (APC scan, base64 decode, staging copy, in-place premultiply, store).
/// The data is random so most pixels have alpha below 255 and the premultiply does real work. Hypothesis: the premultiply
/// pass is a small fraction of the f=24 decode cost of the same byte count.
/// </summary>
[BenchmarkCategory("Graphics", "Transmit")]
public class KittyRgbaTransmitBenchmarks : PerformanceTestBase
{
    private const int Side = 4096;

    protected override bool UseNoGcRegion => false;

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _stream = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();
        _stream = KittyBenchSupport.BuildChunked(
            $"a=t,f=32,s={Side},v={Side},i=1,q=2", KittyBenchSupport.RandomBytes(Side * Side * 4));
        _adapter = KittyBenchSupport.CreateAdapter(scrollback: 0, graphics: true);
        _parser = new BasicAnsiParser { Handler = _adapter };

        KittyBenchSupport.FeedLocked(_adapter, _parser, _stream);
        GraphicsMemorySnapshot now = GraphicsMemoryMetrics.GetSnapshot();
        KittyBenchSupport.Require(
            now.LiveImageCount - _metricsBefore.LiveImageCount == 1
            && now.LiveImagePixelBytes - _metricsBefore.LiveImagePixelBytes == (long)Side * Side * 4,
            "the f=32 4096x4096 transmit must store exactly one RGBA image");
    }

    public override void CleanupBenchmark()
    {
        _adapter.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittyRgbaTransmitBenchmarks), _metricsBefore);
        base.CleanupBenchmark();
    }

    [Benchmark(Description = "Kitty APC: f=32 4096x4096 transmit incl. premultiply")]
    public void Transmit_F32_4096() => KittyBenchSupport.FeedLocked(_adapter, _parser, _stream);
}

/// <summary>
/// Plan 5.3 (3): <c>o=z</c> inflate. The same 4096x4096 f=32 image as <see cref="KittyRgbaTransmitBenchmarks"/> but a
/// smooth gradient, zlib-compressed (fastest level) before transmission, so the timed work is APC scan, base64 decode of
/// the (much shorter) compressed payload, a bounded inflate into the final pixel buffer and the premultiply.
/// Hypothesis: inflate dominates; the compressed stream's share of the scan/decode cost is small.
/// </summary>
[BenchmarkCategory("Graphics", "Transmit")]
public class KittyInflateBenchmarks : PerformanceTestBase
{
    private const int Side = 4096;

    protected override bool UseNoGcRegion => false;

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _stream = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();
        byte[] compressed = KittyBenchSupport.ZlibCompress(KittyBenchSupport.GradientRgba(Side, Side));
        _stream = KittyBenchSupport.BuildChunked($"a=t,f=32,o=z,s={Side},v={Side},i=1,q=2", compressed);
        _adapter = KittyBenchSupport.CreateAdapter(scrollback: 0, graphics: true);
        _parser = new BasicAnsiParser { Handler = _adapter };

        KittyBenchSupport.FeedLocked(_adapter, _parser, _stream);
        GraphicsMemorySnapshot now = GraphicsMemoryMetrics.GetSnapshot();
        KittyBenchSupport.Require(
            now.LiveImageCount - _metricsBefore.LiveImageCount == 1
            && now.LiveImagePixelBytes - _metricsBefore.LiveImagePixelBytes == (long)Side * Side * 4,
            "the o=z transmit must inflate to exactly one 4096x4096 RGBA image");
    }

    public override void CleanupBenchmark()
    {
        _adapter.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittyInflateBenchmarks), _metricsBefore);
        base.CleanupBenchmark();
    }

    [Benchmark(Description = "Kitty APC: o=z f=32 4096x4096 transmit (inflate + premultiply)")]
    public void Transmit_Inflate_F32_4096() => KittyBenchSupport.FeedLocked(_adapter, _parser, _stream);
}

/// <summary>
/// Plan 5.3 (4) and (5): placements under scrolling, driven by line feeds through the adapter so the buffer's
/// store notifications (<c>OnRingRotatedUp</c>, <c>OnRegionScrolled</c>) run exactly as in a session.
///
/// Full-screen (4): 10,000 placements of one image, then <see cref="FullScrolls"/> scrolls. Hypothesis: a full-screen
/// scroll is O(1) in the number of placements, so "scroll with 10,000 placements" minus "place 10,000" is close to
/// "scroll with none". The plan's figure of 1 M scrolls cannot be kept alive by a bounded scrollback (a placement is
/// deleted when it scrolls out of the history), so each invocation scrolls <see cref="FullScrolls"/> times, less than
/// <see cref="Scrollback"/>, and then clears everything with ED 3 / ED 2; the O(1) claim is checked by comparing the
/// three methods, not by a 1 M run.
///
/// Partial region (5): margins on rows 5..20, 1,000 placements re-placed (same ids, so replaced, not accumulated) at the
/// bottom margin row each cycle and then scrolled up <see cref="RegionScrollsPerCycle"/> rows inside the region for
/// <see cref="RegionCycles"/> cycles. Hypothesis: region scrolls cost O(placements) each (anchors are adjusted
/// explicitly), unlike the full-screen case; "scroll" minus "place only" estimates that cost.
/// Every method includes its own placement creation and cleanup, so compare methods with each other rather than reading
/// one in isolation.
/// </summary>
[BenchmarkCategory("Graphics", "Placements")]
public class KittyPlacementScrollBenchmarks : PerformanceTestBase
{
    private const int Scrollback = 30_000;
    private const int FullScrolls = 20_000;
    private const int FullPlacements = 10_000;
    private const int RegionPlacements = 1_000;
    private const int RegionCycles = 20;
    private const int RegionScrollsPerCycle = 14; // rows 5..20 are 16 rows; a placement placed at row 20 stays inside after 14 scrolls

    protected override bool UseNoGcRegion => false;

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _fullNoPlacements = null!;
    private byte[] _fullPlaceOnly = null!;
    private byte[] _fullPlaceAndScroll = null!;
    private byte[] _regionPlaceOnly = null!;
    private byte[] _regionPlaceAndScroll = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();
        _adapter = KittyBenchSupport.CreateAdapter(Scrollback, graphics: true);
        _parser = new BasicAnsiParser { Handler = _adapter };
        KittyBenchSupport.FeedLocked(_adapter, _parser, KittyBenchSupport.OnePixelImage());
        KittyBenchSupport.Require(
            GraphicsMemoryMetrics.GetSnapshot().LiveImageCount - _metricsBefore.LiveImageCount == 1,
            "the 1x1 image must be stored");

        string toBottom = KittyBenchSupport.Esc + "[24;1H";
        string scrolls = new string('\n', FullScrolls);
        string clear = KittyBenchSupport.Esc + "[3J" + KittyBenchSupport.Esc + "[2J" + KittyBenchSupport.Esc + "[H";
        string place10k = KittyBenchSupport.Placements(FullPlacements);
        _fullNoPlacements = KittyBenchSupport.Ascii(toBottom + scrolls + clear);
        _fullPlaceOnly = KittyBenchSupport.Ascii(toBottom + place10k + clear);
        _fullPlaceAndScroll = KittyBenchSupport.Ascii(toBottom + place10k + scrolls + clear);

        string regionOn = KittyBenchSupport.Esc + "[5;20r" + KittyBenchSupport.Esc + "[20;1H";
        string regionOff = KittyBenchSupport.Esc + "[r";
        string place1k = KittyBenchSupport.Placements(RegionPlacements);
        string regionScrolls = new string('\n', RegionScrollsPerCycle);
        var placeOnly = new StringBuilder(regionOn);
        var placeAndScroll = new StringBuilder(regionOn);
        for (int c = 0; c < RegionCycles; c++)
        {
            placeOnly.Append(place1k);
            placeAndScroll.Append(place1k).Append(regionScrolls).Append(KittyBenchSupport.Esc + "[20;1H");
        }
        _regionPlaceOnly = KittyBenchSupport.Ascii(placeOnly.Append(regionOff).Append(clear).ToString());
        _regionPlaceAndScroll = KittyBenchSupport.Ascii(placeAndScroll.Append(regionOff).Append(clear).ToString());

        VerifyFull(place10k, toBottom, scrolls, clear);
        VerifyRegion(regionOn, place1k, regionScrolls, regionOff, clear);
        // JIT every timed path once more at steady state.
        Feed(_fullNoPlacements);
        Feed(_fullPlaceOnly);
        Feed(_fullPlaceAndScroll);
        Feed(_regionPlaceOnly);
        Feed(_regionPlaceAndScroll);
    }

    private void Feed(byte[] stream) => KittyBenchSupport.FeedLocked(_adapter, _parser, stream);

    private void VerifyFull(string place10k, string toBottom, string scrolls, string clear)
    {
        long baseline = KittyBenchSupport.LivePlacements;
        Feed(KittyBenchSupport.Ascii(toBottom + place10k));
        KittyBenchSupport.Require(KittyBenchSupport.LivePlacements - baseline == FullPlacements, "10,000 placements must be live after placing");
        Feed(KittyBenchSupport.Ascii(scrolls));
        KittyBenchSupport.Require(
            KittyBenchSupport.LivePlacements - baseline == FullPlacements,
            "the placements must survive the full-screen scrolls (history is larger than the scroll count)");
        Feed(KittyBenchSupport.Ascii(clear));
        KittyBenchSupport.Require(KittyBenchSupport.LivePlacements == baseline, "ED 3 + ED 2 must delete every placement");
    }

    private void VerifyRegion(string regionOn, string place1k, string regionScrolls, string regionOff, string clear)
    {
        long baseline = KittyBenchSupport.LivePlacements;
        var sb = new StringBuilder(regionOn);
        for (int c = 0; c < RegionCycles; c++)
            sb.Append(place1k).Append(regionScrolls).Append(KittyBenchSupport.Esc + "[20;1H");
        Feed(KittyBenchSupport.Ascii(sb.Append(regionOff).ToString()));
        KittyBenchSupport.Require(
            KittyBenchSupport.LivePlacements - baseline == RegionPlacements,
            "1,000 placements (re-placed with the same ids each cycle) must be live after the region scrolls");
        Feed(KittyBenchSupport.Ascii(clear));
        KittyBenchSupport.Require(KittyBenchSupport.LivePlacements == baseline, "ED 3 + ED 2 must delete every placement");
    }

    public override void CleanupBenchmark()
    {
        _adapter.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittyPlacementScrollBenchmarks), _metricsBefore);
        base.CleanupBenchmark();
    }

    [Benchmark(Description = "Kitty scroll: full-screen x20,000, no placements (control)")]
    public void Full_Scroll_NoPlacements() => Feed(_fullNoPlacements);

    [Benchmark(Description = "Kitty scroll: place 10,000 only (control)")]
    public void Full_PlaceOnly() => Feed(_fullPlaceOnly);

    [Benchmark(Description = "Kitty scroll: place 10,000 then full-screen x20,000")]
    public void Full_PlaceAndScroll() => Feed(_fullPlaceAndScroll);

    [Benchmark(Description = "Kitty scroll: region, 1,000 placements x20 cycles, place only (control)")]
    public void Region_PlaceOnly() => Feed(_regionPlaceOnly);

    [Benchmark(Description = "Kitty scroll: region, 1,000 placements x20 cycles x14 scrolls")]
    public void Region_PlaceAndScroll() => Feed(_regionPlaceAndScroll);
}

/// <summary>
/// Plan 5.3 (6): per-frame render snapshot capture with 1,000 visible placements, taken under SyncRoot as the host does,
/// against the same capture without placements. Hypothesis: the difference is linear in the placement count (one
/// dictionary walk plus one retained lease per visible placement) and the pooled snapshot allocates nothing in steady
/// state (see KittyAllocationTests); the capture time is the SyncRoot hold the renderer imposes on the PTY thread.
/// </summary>
[BenchmarkCategory("Graphics", "Snapshot")]
public class KittySnapshotBenchmarks : PerformanceTestBase
{
    private const int PlacementCount = 1_000;

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _withPlacements = null!;
    private TerminalAdapter _withoutPlacements = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();
        _withPlacements = KittyBenchSupport.CreateAdapter(scrollback: 100, graphics: true);
        _withoutPlacements = KittyBenchSupport.CreateAdapter(scrollback: 100, graphics: true);
        var parser = new BasicAnsiParser { Handler = _withPlacements };
        KittyBenchSupport.FeedLocked(_withPlacements, parser, KittyBenchSupport.OnePixelImage());

        // 1,000 placements spread over the 24 visible rows (each CUP moves the cursor; C=1 keeps it there).
        var sb = new StringBuilder();
        for (int p = 1; p <= PlacementCount; p++)
            sb.Append(KittyBenchSupport.Esc).Append('[').Append(p % 24 + 1).Append(";1H").Append(KittyBenchSupport.Placements(1, p));
        KittyBenchSupport.FeedLocked(_withPlacements, parser, KittyBenchSupport.Ascii(sb.ToString()));

        using (var snapshot = _withPlacements.Buffer.CaptureRenderSnapshotVisible())
        {
            KittyBenchSupport.Require(
                snapshot.Graphics is { } graphics && graphics.Placements.Length == PlacementCount,
                "all 1,000 placements must be visible in the capture");
        }
        using (var snapshot = _withoutPlacements.Buffer.CaptureRenderSnapshotVisible())
            KittyBenchSupport.Require(snapshot.Graphics is null, "the control adapter must have no graphics in its capture");
    }

    public override void CleanupBenchmark()
    {
        _withPlacements.Dispose();
        _withoutPlacements.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittySnapshotBenchmarks), _metricsBefore);
        base.CleanupBenchmark();
    }

    [Benchmark(Description = "Kitty snapshot: capture, 1,000 visible placements")]
    public int Capture_1000Placements()
    {
        var buffer = _withPlacements.Buffer;
        lock (buffer.SyncRoot)
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
            return snapshot.Graphics!.Placements.Length;
        }
    }

    [Benchmark(Description = "Kitty snapshot: capture, no placements (control)")]
    public int Capture_NoPlacements()
    {
        var buffer = _withoutPlacements.Buffer;
        lock (buffer.SyncRoot)
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
            return snapshot.Rows;
        }
    }
}

/// <summary>
/// Plan 5.3 lock-hold evidence (experiment; no assertions). Feeds a 64 MiB PNG-sized direct payload (<c>f=100</c>, the
/// declared 4096x4096 image decoded by <see cref="FillingImageDecoder"/>) in 8 KiB slices with one SyncRoot hold per
/// slice, exactly like <c>TerminalSession.ConsumePtyOutput</c>, while a probing thread samples the lock. The control feeds
/// the same number of plain-text bytes the same way. The probe loops <c>Monitor.TryEnter(SyncRoot, 0)</c>; when that
/// fails it raises <see cref="TerminalBuffer.ReaderWaiting"/> (as the renderer does), blocks in <c>Monitor.Enter</c> and
/// records how long it waited. Two maxima are written to the console in CleanupBenchmark: the longest wait of a blocked
/// probe, and the longest interval between two successful zero-wait acquisitions. Both overestimate the true hold
/// (scheduling, monitor unfairness) and neither is a pass/fail number.
/// Hypothesis (plan D2-c): decode runs with SyncRoot released, so the PNG variant's maxima are of the same order as the
/// plain-text variant's and far below the decode time; if the decode were under the lock the maxima would be the
/// decode duration.
/// </summary>
[BenchmarkCategory("Graphics", "Experiment", "LockProbe")]
public class KittyLockHoldProbe : PerformanceTestBase
{
    private const int PngFileBytes = 48 * 1024 * 1024; // base64 inflates 3 -> 4, so the APC payload is 64 MiB
    private const int TextBytes = 64 * 1024 * 1024;

    protected override bool UseNoGcRegion => false;

    private sealed class ProbeResult
    {
        public long MaxBlockedWaitTicks;
        public long MaxAcquireGapTicks;
        public long Samples;
    }

    private GraphicsMemorySnapshot _metricsBefore = null!;
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _png = null!;
    private byte[] _text = null!;
    private ProbeResult _pngMax = new();
    private ProbeResult _textMax = new();
    private bool _pngRan;
    private bool _textRan;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        _metricsBefore = GraphicsMemoryMetrics.GetSnapshot();
        _adapter = KittyBenchSupport.CreateAdapter(scrollback: 1000, graphics: true, new FillingImageDecoder());
        _parser = new BasicAnsiParser { Handler = _adapter };

        // PNG signature + IHDR (4096x4096, 8-bit RGBA) then filler: the core reads only the header; the fake decoder never looks at the rest.
        var png = new byte[PngFileBytes];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 4096);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 4096);
        png[24] = 8; // bit depth
        png[25] = 6; // RGBA
        for (int i = 33; i < png.Length; i++)
            png[i] = (byte)(i * 31);
        _png = KittyBenchSupport.BuildChunked("a=t,f=100,i=1,q=2", png);

        byte[] line = KittyBenchSupport.Ascii("The quick brown fox jumps over the lazy dog 0123456789\r\n");
        _text = new byte[TextBytes];
        for (int off = 0; off < _text.Length; off += line.Length)
            line.AsSpan(0, Math.Min(line.Length, _text.Length - off)).CopyTo(_text.AsSpan(off));

        KittyBenchSupport.FeedLocked(_adapter, _parser, _png);
        GraphicsMemorySnapshot now = GraphicsMemoryMetrics.GetSnapshot();
        KittyBenchSupport.Require(
            now.LiveImageCount - _metricsBefore.LiveImageCount == 1
            && now.LiveImagePixelBytes - _metricsBefore.LiveImagePixelBytes == 4096L * 4096 * 4,
            "the PNG payload must be accepted and decoded by the fake decoder into one 4096x4096 RGBA image");
        KittyBenchSupport.FeedLocked(_adapter, _parser, _text.AsSpan(0, 1 << 20));
    }

    public override void CleanupBenchmark()
    {
        Report("PNG f=100 64 MiB payload", _pngRan, _pngMax);
        Report("plain text 64 MiB (control)", _textRan, _textMax);
        _adapter.Dispose();
        KittyBenchSupport.WarnIfNotRestored(nameof(KittyLockHoldProbe), _metricsBefore);
        base.CleanupBenchmark();
    }

    private static void Report(string label, bool ran, ProbeResult result)
    {
        if (!ran)
            return;
        double toMs = 1000.0 / Stopwatch.Frequency;
        Console.WriteLine(
            $"[KittyLockHoldProbe] {label}: max blocked wait {result.MaxBlockedWaitTicks * toMs:F3} ms, "
            + $"max gap between zero-wait acquisitions {result.MaxAcquireGapTicks * toMs:F3} ms, probe samples {result.Samples}.");
    }

    private void FeedSessionLike(byte[] data)
    {
        TerminalBuffer buffer = _adapter.Buffer;
        object root = buffer.SyncRoot;
        ReadOnlySpan<byte> remaining = data;
        while (!remaining.IsEmpty)
        {
            int take = Math.Min(KittyBenchSupport.FeedSlice, remaining.Length);
            bool taken = false;
            try
            {
                Monitor.Enter(root, ref taken);
                _parser.Feed(remaining[..take]);
            }
            finally
            {
                if (taken)
                    Monitor.Exit(root);
            }
            remaining = remaining[take..];

            // The same short handoff the session uses so a waiting renderer is not barged past.
            if (!remaining.IsEmpty && buffer.ReaderWaiting)
            {
                int spins = 0;
                while (buffer.ReaderWaiting && spins++ < 64)
                    Thread.Yield();
            }
        }
    }

    private void RunProbed(byte[] data, ProbeResult overall)
    {
        TerminalBuffer buffer = _adapter.Buffer;
        object root = buffer.SyncRoot;
        var local = new ProbeResult();
        int stop = 0;
        using var started = new ManualResetEventSlim(false);
        var probe = new Thread(() =>
        {
            started.Set();
            long lastAcquire = Stopwatch.GetTimestamp();
            long samples = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                samples++;
                if (Monitor.TryEnter(root, 0))
                {
                    Monitor.Exit(root);
                    long now = Stopwatch.GetTimestamp();
                    local.MaxAcquireGapTicks = Math.Max(local.MaxAcquireGapTicks, now - lastAcquire);
                    lastAcquire = now;
                }
                else
                {
                    buffer.ReaderWaiting = true;
                    long begin = Stopwatch.GetTimestamp();
                    Monitor.Enter(root);
                    long end = Stopwatch.GetTimestamp();
                    Monitor.Exit(root);
                    buffer.ReaderWaiting = false;
                    local.MaxBlockedWaitTicks = Math.Max(local.MaxBlockedWaitTicks, end - begin);
                    local.MaxAcquireGapTicks = Math.Max(local.MaxAcquireGapTicks, end - lastAcquire);
                    lastAcquire = end;
                }
                Thread.Yield();
            }
            local.Samples = samples;
        })
        { IsBackground = true, Name = "KittyLockHoldProbe" };
        probe.Start();
        started.Wait();
        try
        {
            FeedSessionLike(data);
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            probe.Join();
            buffer.ReaderWaiting = false;
        }
        overall.MaxBlockedWaitTicks = Math.Max(overall.MaxBlockedWaitTicks, local.MaxBlockedWaitTicks);
        overall.MaxAcquireGapTicks = Math.Max(overall.MaxAcquireGapTicks, local.MaxAcquireGapTicks);
        overall.Samples += local.Samples;
    }

    [Benchmark(Description = "Kitty lock probe: 64 MiB PNG payload (SyncRoot sampled)")]
    public void Png_Payload()
    {
        _pngRan = true;
        RunProbed(_png, _pngMax);
    }

    [Benchmark(Description = "Kitty lock probe: 64 MiB plain text (control)")]
    public void Plain_Text()
    {
        _textRan = true;
        RunProbed(_text, _textMax);
    }
}
