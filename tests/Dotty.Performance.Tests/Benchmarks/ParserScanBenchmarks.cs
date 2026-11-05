using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Parser run-scan, CJK/grapheme, REP and scroll-region benchmarks (plan 08, WI-1a).
///
/// Gate rules (plan 08, section 1.8): the baseline key is the <c>Description</c>, so every variant is
/// its own method and there are no <c>[Params]</c>. Scan/CJK methods feed one pre-built 64 KiB array per
/// invoke with <c>OperationsPerInvoke = 1</c>: read the result as ns per 64 KiB
/// (MB/s = 65536 / ns x 1000). The "10k scrollback" variants feed a populated ring so the working set
/// is about 10 MB instead of the 23 KB of the scrollback-0 grid (cache effect). Only the stable public
/// entry point <see cref="BasicAnsiParser.Feed"/> is used, so parser-internal refactors keep the keys valid.
/// </summary>
[BenchmarkCategory("Parser", "Scan")]
public class ParserScanBenchmarks : PerformanceTestBase
{
    private const int ScrollbackLinesForFullRing = 10_000;
    private const int RegionRows = 60;
    private const int RegionColumns = 200;
    private const int RegionOperations = 1000;

    private BasicAnsiParser _parser = null!;        // 24x80, scrollback 0
    private BasicAnsiParser _parserSb = null!;      // 24x80, scrollback 10k (ring pre-filled)
    private BasicAnsiParser _repParser = null!;     // 24x80, scrollback 0, last printed char 'x'
    private BasicAnsiParser _regionParser = null!;  // 60x200, scrollback 10k

    private byte[] _ascii8 = null!;
    private byte[] _ascii22 = null!;
    private byte[] _ascii80 = null!;
    private byte[] _ascii4096 = null!;
    private byte[] _asciiSgr22 = null!;
    private byte[] _cjkRun = null!;
    private byte[] _mixedAsciiCjk = null!;
    private byte[] _combining = null!;
    private byte[] _emojiZwj = null!;
    private byte[] _regionLineFeeds = null!;
    private byte[] _regionReverseIndexes = null!;
    private byte[] _regionDeleteLines = null!;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();

        _ascii8 = BenchmarkContent.AsciiRuns(8, crlf: true);
        _ascii22 = BenchmarkContent.AsciiRuns(22, crlf: true);
        _ascii80 = BenchmarkContent.AsciiRuns(80, crlf: true);
        _ascii4096 = BenchmarkContent.AsciiRuns(4096, crlf: false);
        _asciiSgr22 = BenchmarkContent.AsciiRunsSplitBySgr(22);
        _cjkRun = BenchmarkContent.CjkRun();
        _mixedAsciiCjk = BenchmarkContent.MixedAsciiCjkLines();
        _combining = BenchmarkContent.CombiningMarkLines();
        _emojiZwj = BenchmarkContent.EmojiZwjLines();

        _parser = new BasicAnsiParser { Handler = new TerminalAdapter(24, 80, scrollbackCapacity: 0) };

        var sbAdapter = new TerminalAdapter(24, 80, scrollbackCapacity: ScrollbackLinesForFullRing);
        _parserSb = new BasicAnsiParser { Handler = sbAdapter };
        // ~800 one-row lines per feed: 16 feeds overfill a 10k ring so the measured feeds are steady state.
        for (int i = 0; i < 16; i++)
            _parserSb.Feed(_ascii80);
        if (sbAdapter.Buffer.ScrollbackCount != ScrollbackLinesForFullRing)
            throw new InvalidOperationException(
                $"Scrollback ring not full after setup: {sbAdapter.Buffer.ScrollbackCount}.");

        _repParser = new BasicAnsiParser { Handler = new TerminalAdapter(24, 80, scrollbackCapacity: 0) };
        _repParser.Feed("x"u8);

        var regionAdapter = new TerminalAdapter(RegionRows, RegionColumns, scrollbackCapacity: ScrollbackLinesForFullRing);
        _regionParser = new BasicAnsiParser { Handler = regionAdapter };
        // Fill the whole grid first so shifted rows carry real cell data.
        _regionParser.Feed(BenchmarkContent.LogGrid(RegionRows, RegionColumns));
        // Each array sets its own region and cursor so the three variants do not depend on run order.
        _regionLineFeeds = RegionOperationsArray("\u001b[1;59r\u001b[59;1H", "\n");
        _regionReverseIndexes = RegionOperationsArray("\u001b[1;59r\u001b[1;1H", "\u001bM");
        _regionDeleteLines = RegionOperationsArray("\u001b[2;59r\u001b[2;1H", "\u001b[M");

        // Steady state + JIT for every timed path.
        _parser.Feed(_ascii8);
        _parser.Feed(_ascii22);
        _parser.Feed(_ascii80);
        _parser.Feed(_ascii4096);
        _parser.Feed(_asciiSgr22);
        _parser.Feed(_cjkRun);
        _parser.Feed(_mixedAsciiCjk);
        _parser.Feed(_combining);
        _parser.Feed(_emojiZwj);
        _parserSb.Feed(_ascii4096);
        _regionParser.Feed(_regionLineFeeds);
        _regionParser.Feed(_regionReverseIndexes);
        _regionParser.Feed(_regionDeleteLines);
    }

    private static byte[] RegionOperationsArray(string prefix, string operation)
    {
        var builder = new StringBuilder(prefix.Length + RegionOperations * operation.Length);
        builder.Append(prefix);
        for (int i = 0; i < RegionOperations; i++)
            builder.Append(operation);
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    #region ASCII runs (scrollback 0)

    [Benchmark(Description = "Scan: ASCII 8B runs + CRLF")]
    public void Ascii_8B_Runs() => _parser.Feed(_ascii8);

    [Benchmark(Description = "Scan: ASCII 22B runs + CRLF")]
    public void Ascii_22B_Runs() => _parser.Feed(_ascii22);

    [Benchmark(Description = "Scan: ASCII 80B runs + CRLF")]
    public void Ascii_80B_Runs() => _parser.Feed(_ascii80);

    [Benchmark(Description = "Scan: ASCII 4096B single run")]
    public void Ascii_4096B_SingleRun() => _parser.Feed(_ascii4096);

    #endregion

    #region ASCII runs (10k scrollback)

    [Benchmark(Description = "Scan: ASCII 80B runs + CRLF, 10k scrollback")]
    public void Ascii_80B_Runs_Scrollback() => _parserSb.Feed(_ascii80);

    [Benchmark(Description = "Scan: ASCII 4096B single run, 10k scrollback")]
    public void Ascii_4096B_SingleRun_Scrollback() => _parserSb.Feed(_ascii4096);

    #endregion

    #region SGR, CJK, combining, emoji (scrollback 0)

    [Benchmark(Description = "Scan: ASCII runs split by SGR (22B)")]
    public void Ascii_Runs_SplitBySgr() => _parser.Feed(_asciiSgr22);

    [Benchmark(Description = "Scan: CJK 3-byte run (64 KiB)")]
    public void Cjk_ThreeByteRun() => _parser.Feed(_cjkRun);

    [Benchmark(Description = "Scan: mixed ASCII+CJK lines")]
    public void Mixed_AsciiCjkLines() => _parser.Feed(_mixedAsciiCjk);

    [Benchmark(Description = "Scan: combining marks (e + U+0301)")]
    public void CombiningMarks() => _parser.Feed(_combining);

    [Benchmark(Description = "Scan: emoji ZWJ sequences")]
    public void EmojiZwjSequences() => _parser.Feed(_emojiZwj);

    #endregion

    #region REP

    [Benchmark(Description = "REP: CSI 80 b after x", OperationsPerInvoke = 256)]
    public void Rep_80()
    {
        for (int i = 0; i < 256; i++)
            _repParser.Feed("\u001b[80b"u8);
    }

    [Benchmark(Description = "REP: CSI 100000 b after x")]
    public void Rep_100000() => _repParser.Feed("\u001b[100000b"u8);

    #endregion

    #region Partial scroll regions (200x60 grid, 1,000 operations per invoke)

    [Benchmark(Description = "Region: LF in DECSTBM 1..rows-1 (200x60)", OperationsPerInvoke = RegionOperations)]
    public void Region_LineFeed() => _regionParser.Feed(_regionLineFeeds);

    [Benchmark(Description = "Region: RI in DECSTBM 1..rows-1 (200x60)", OperationsPerInvoke = RegionOperations)]
    public void Region_ReverseIndex() => _regionParser.Feed(_regionReverseIndexes);

    [Benchmark(Description = "Region: DL 1 in DECSTBM 2..rows-1 (200x60)", OperationsPerInvoke = RegionOperations)]
    public void Region_DeleteLine() => _regionParser.Feed(_regionDeleteLines);

    #endregion
}
