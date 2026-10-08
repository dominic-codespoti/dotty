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
    private TerminalBuffer _writeBuffer = null!;
    private TerminalBuffer _lineFeedBuffer = null!;
    private byte[] _data500k = null!;
    private static readonly string WriteText = "The quick brown fox jumps over the lazy dog 0123456789";
    private const int _rows = 30;
    private const int _cols = 80;

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(_rows, _cols, scrollbackCapacity: 0);
        _parser = new BasicAnsiParser { Handler = _adapter };
        _writeBuffer = new TerminalBuffer(_rows, _cols, scrollbackCapacity: 0);
        _lineFeedBuffer = new TerminalBuffer(_rows, _cols, scrollbackCapacity: 0);

        var line = "The quick brown fox jumps over the lazy dog 0123456789\n";
        var sb = new System.Text.StringBuilder(500_000 * line.Length);
        for (int i = 0; i < 500_000; i++)
            sb.Append(line);
        _data500k = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);
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
        var text = WriteText.AsSpan();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500_000; i++)
        {
            _writeBuffer.WriteText(text, (string?)null);
            _writeBuffer.CarriageReturn();
            _writeBuffer.LineFeed();
        }
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    [Benchmark]
    public long LineFeedOnly()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500_000; i++)
            _lineFeedBuffer.LineFeed();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}
