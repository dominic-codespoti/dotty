using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
[BenchmarkCategory("BulkOutput")]
public class BulkOutputBenchmark : PerformanceTestBase
{
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _data500k = null!;
    private const int _rows = 30;
    private const int _cols = 80;

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(_rows, _cols);
        _adapter.Buffer.MaxScrollback = 10000;
        _parser = new BasicAnsiParser { Handler = _adapter };

        var line = "The quick brown fox jumps over the lazy dog 0123456789\n";
        var sb = new System.Text.StringBuilder(500_000 * (line.Length + 1));
        for (int i = 0; i < 500_000; i++)
            sb.Append(line);
        _data500k = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Benchmark]
    public long FullPipeline()
    {
        var sw = Stopwatch.StartNew();
        _parser.Feed(_data500k);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    [Benchmark]
    public long WriteOnly()
    {
        var buf = new TerminalBuffer(_rows, _cols);
        buf.MaxScrollback = 0;
        var text = "The quick brown fox jumps over the lazy dog 0123456789".AsSpan();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500_000; i++)
        {
            buf.WriteText(text, (string?)null);
            buf.CarriageReturn();
            buf.LineFeed();
        }
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    [Benchmark]
    public long LineFeedOnly()
    {
        var buf = new TerminalBuffer(_rows, _cols);
        buf.MaxScrollback = 10000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500_000; i++)
            buf.LineFeed();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}
