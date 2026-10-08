using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Sustained throughput benchmarks
/// </summary>
[BenchmarkCategory("Throughput")]
public class ThroughputBenchmarks : PerformanceTestBase
{
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _plain1Mb = null!;
    private byte[] _plain100Kb = null!;
    private byte[] _ansi1Mb = null!;
    private byte[] _lines10K = null!;
    private byte[] _lines100K = null!;
    private byte[] _sgr1K = null!;
    private byte[] _cursor10K = null!;
    private byte[] _logs = null!;
    private byte[] _code = null!;
    private byte[] _ansiMixed = null!;
    private byte[] _interactive = null!;

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
        _parser = new BasicAnsiParser { Handler = _adapter };
        _plain1Mb = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.XLarge);
        _plain100Kb = TestDataGenerator.GeneratePlainText(100_000);
        _ansi1Mb = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.XLarge, 0.1);
        _lines10K = TestDataGenerator.GenerateScrollingWorkload(10_000);
        _lines100K = new byte[_lines10K.Length * 10];
        for (int i = 0; i < 10; i++) _lines10K.CopyTo(_lines100K, i * _lines10K.Length);

        var sgr = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            sgr.Append($"\u001b[{i % 256}m");
            sgr.Append("Text ");
        }
        sgr.Append("\u001b[0m");
        _sgr1K = System.Text.Encoding.UTF8.GetBytes(sgr.ToString());

        var cursors = new System.Text.StringBuilder();
        var random = new Random(42);
        for (int i = 0; i < 10_000; i++)
            cursors.Append($"\u001b[{random.Next(1, 25)};{random.Next(1, 81)}H");
        _cursor10K = System.Text.Encoding.UTF8.GetBytes(cursors.ToString());

        _logs = TestDataGenerator.GenerateLogOutput(500);
        _code = TestDataGenerator.GeneratePlainText(5000);
        _ansiMixed = TestDataGenerator.GenerateBasicAnsiText(5000, 0.1);
        var interactive = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            interactive.Append("echo Line ").Append(i).Append('\r').Append('\n');
            interactive.Append("Line ").Append(i).Append('\r').Append('\n');
        }
        _interactive = System.Text.Encoding.UTF8.GetBytes(interactive.ToString());
        _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);
    }

    [Benchmark(Description = "Throughput: 1MB Plain Text", OperationsPerInvoke = 1)]
    public void Throughput_1MB_Plain() => _parser.Feed(_plain1Mb);
    [Benchmark(Description = "Throughput: 10MB Plain Text", OperationsPerInvoke = 1)]
    public void Throughput_10MB_Plain() { for (int i = 0; i < 100; i++) _parser.Feed(_plain100Kb); }
    [Benchmark(Description = "Throughput: 1MB ANSI Text", OperationsPerInvoke = 1)]
    public void Throughput_1MB_Ansi() => _parser.Feed(_ansi1Mb);
    [Benchmark(Description = "Throughput: 10K Lines", OperationsPerInvoke = 1)]
    public void Throughput_10K_Lines() => _parser.Feed(_lines10K);
    [Benchmark(Description = "Throughput: 100K Lines", OperationsPerInvoke = 1)]
    public void Throughput_100K_Lines() => _parser.Feed(_lines100K);
    [Benchmark(Description = "Throughput: 1K SGR Sequences")]
    public void Throughput_1K_SgrSequences() => _parser.Feed(_sgr1K);
    [Benchmark(Description = "Throughput: 10K Cursor Moves")]
    public void Throughput_10K_CursorMoves() => _parser.Feed(_cursor10K);
    [Benchmark(Description = "Throughput: Mixed (logs+code+ansi)")]
    public void Throughput_Mixed() { _parser.Feed(_logs); _parser.Feed(_code); _parser.Feed(_ansiMixed); }
    [Benchmark(Description = "Throughput: Interactive (keys+output)")]
    public void Throughput_Interactive() => _parser.Feed(_interactive);
    [Benchmark(Description = "Burst: 100KB chunks x 10")]
    public void Burst_100KBx10() { for (int i = 0; i < 10; i++) _parser.Feed(_plain100Kb); }
    [Benchmark(Description = "Burst: 10KB chunks x 100")]
    public void Burst_10KBx100()
    {
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 10; j++) _parser.Feed(_plain100Kb.AsSpan(0, 10_000));
    }
}

/// <summary>
/// Latency benchmarks for individual operations
/// </summary>
[BenchmarkCategory("Latency")]
public class LatencyBenchmarks : PerformanceTestBase
{
    private TerminalBuffer _buffer = null!;
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private static readonly byte[] ParseSgr = "\u001b[1;31mText\u001b[0m"u8.ToArray();
    private static readonly byte[] CursorMove = "\u001b[12;40H"u8.ToArray();
    private static readonly byte[] LineClear = "\u001b[K"u8.ToArray();
    private static readonly byte[] ScreenClear = "\u001b[2J"u8.ToArray();
    private static readonly byte[] LineFeed = "\n"u8.ToArray();
    private static readonly byte[] Tab = "\t"u8.ToArray();
    private static readonly string HundredChars = new('X', 100);

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _buffer = new TerminalBuffer(24, 80, scrollbackCapacity: 0);
        _adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
        _parser = new BasicAnsiParser { Handler = _adapter };
        _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);
        _ = ParseSgr.Length + CursorMove.Length + LineClear.Length + ScreenClear.Length + LineFeed.Length + Tab.Length + HundredChars.Length;
    }

    [Benchmark(Description = "Latency: Single Character", Baseline = true, OperationsPerInvoke = 256)]
    public void Latency_SingleChar() { for (int i = 0; i < 256; i++) _buffer.WriteText("A", CellAttributes.Default); }
    [Benchmark(Description = "Latency: 10 Characters", OperationsPerInvoke = 64)]
    public void Latency_10Chars() { for (int i = 0; i < 64; i++) _buffer.WriteText("HelloWorld", CellAttributes.Default); }
    [Benchmark(Description = "Latency: 100 Characters", OperationsPerInvoke = 16)]
    public void Latency_100Chars() { for (int i = 0; i < 16; i++) _buffer.WriteText(HundredChars, CellAttributes.Default); }
    [Benchmark(Description = "Latency: Parse SGR", OperationsPerInvoke = 256)]
    public void Latency_ParseSgr() { for (int i = 0; i < 256; i++) _parser.Feed(ParseSgr); }
    [Benchmark(Description = "Latency: Cursor Move", OperationsPerInvoke = 256)]
    public void Latency_CursorMove() { for (int i = 0; i < 256; i++) _parser.Feed(CursorMove); }
    [Benchmark(Description = "Latency: Line Clear", OperationsPerInvoke = 256)]
    public void Latency_LineClear() { for (int i = 0; i < 256; i++) _parser.Feed(LineClear); }
    [Benchmark(Description = "Latency: Screen Clear", OperationsPerInvoke = 64)]
    public void Latency_ScreenClear() { for (int i = 0; i < 64; i++) _parser.Feed(ScreenClear); }
    [Benchmark(Description = "Latency: Line Feed", OperationsPerInvoke = 256)]
    public void Latency_LineFeed() { for (int i = 0; i < 256; i++) _parser.Feed(LineFeed); }
    [Benchmark(Description = "Latency: Tab Character", OperationsPerInvoke = 256)]
    public void Latency_Tab() { for (int i = 0; i < 256; i++) _parser.Feed(Tab); }
}
