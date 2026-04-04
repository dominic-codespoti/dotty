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

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };
    }

    #region Character Throughput

    [Benchmark(Description = "Throughput: 1MB Plain Text", OperationsPerInvoke = 1)]
    public void Throughput_1MB_Plain()
    {
        var data = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.XLarge);
        _parser.Feed(data);
    }

    [Benchmark(Description = "Throughput: 10MB Plain Text", OperationsPerInvoke = 1)]
    public void Throughput_10MB_Plain()
    {
        // Generate 10MB in chunks to avoid huge allocations
        const int chunkSize = 100000;
        const int chunks = 100;
        
        var chunk = TestDataGenerator.GeneratePlainText(chunkSize);
        
        for (int i = 0; i < chunks; i++)
        {
            _parser.Feed(chunk);
        }
    }

    [Benchmark(Description = "Throughput: 1MB ANSI Text", OperationsPerInvoke = 1)]
    public void Throughput_1MB_Ansi()
    {
        var data = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.XLarge, 0.1);
        _parser.Feed(data);
    }

    #endregion

    #region Line Throughput

    [Benchmark(Description = "Throughput: 10K Lines")]
    public void Throughput_10K_Lines()
    {
        var data = TestDataGenerator.GenerateScrollingWorkload(10000);
        _parser.Feed(data);
    }

    [Benchmark(Description = "Throughput: 100K Lines")]
    public void Throughput_100K_Lines()
    {
        // Process in batches
        for (int batch = 0; batch < 10; batch++)
        {
            var data = TestDataGenerator.GenerateScrollingWorkload(10000);
            _parser.Feed(data);
        }
    }

    #endregion

    #region Sequence Throughput

    [Benchmark(Description = "Throughput: 1K SGR Sequences")]
    public void Throughput_1K_SgrSequences()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            sb.Append($"\u001b[{i % 256}m");
            sb.Append("Text ");
        }
        sb.Append("\u001b[0m");
        
        _parser.Feed(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [Benchmark(Description = "Throughput: 10K Cursor Moves")]
    public void Throughput_10K_CursorMoves()
    {
        var sb = new System.Text.StringBuilder();
        var random = new Random(42);
        
        for (int i = 0; i < 10000; i++)
        {
            sb.Append($"\u001b[{random.Next(1, 25)};{random.Next(1, 81)}H");
        }
        
        _parser.Feed(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    #endregion

    #region Mixed Workload

    [Benchmark(Description = "Throughput: Mixed (logs+code+ansi)")]
    public void Throughput_Mixed()
    {
        // Simulate realistic mixed terminal output
        var logs = TestDataGenerator.GenerateLogOutput(500);
        _parser.Feed(logs);
        
        var code = TestDataGenerator.GeneratePlainText(5000);
        _parser.Feed(code);
        
        var ansi = TestDataGenerator.GenerateBasicAnsiText(5000, 0.1);
        _parser.Feed(ansi);
    }

    [Benchmark(Description = "Throughput: Interactive (keys+output)")]
    public void Throughput_Interactive()
    {
        // Simulate interactive session
        for (int i = 0; i < 100; i++)
        {
            // Command input
            var cmd = System.Text.Encoding.UTF8.GetBytes($"echo Line {i}\r\n");
            _parser.Feed(cmd);
            
            // Command output
            var output = System.Text.Encoding.UTF8.GetBytes($"Line {i}\r\n");
            _parser.Feed(output);
        }
    }

    #endregion

    #region Burst Throughput

    [Benchmark(Description = "Burst: 100KB chunks x 10")]
    public void Burst_100KBx10()
    {
        var chunk = TestDataGenerator.GeneratePlainText(100000);
        
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(chunk);
        }
    }

    [Benchmark(Description = "Burst: 10KB chunks x 100")]
    public void Burst_10KBx100()
    {
        var chunk = TestDataGenerator.GeneratePlainText(10000);
        
        for (int i = 0; i < 100; i++)
        {
            _parser.Feed(chunk);
        }
    }

    #endregion
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

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _buffer = new TerminalBuffer(24, 80);
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };
    }

    [Benchmark(Description = "Latency: Single Character", Baseline = true)]
    public void Latency_SingleChar() => _buffer.WriteText("A", CellAttributes.Default);

    [Benchmark(Description = "Latency: 10 Characters")]
    public void Latency_10Chars() => _buffer.WriteText("HelloWorld", CellAttributes.Default);

    [Benchmark(Description = "Latency: 100 Characters")]
    public void Latency_100Chars() => _buffer.WriteText(new string('X', 100), CellAttributes.Default);

    [Benchmark(Description = "Latency: Parse SGR")]
    public void Latency_ParseSgr() => _parser.Feed("\u001b[1;31mText\u001b[0m"u8);

    [Benchmark(Description = "Latency: Cursor Move")]
    public void Latency_CursorMove() => _parser.Feed("\u001b[12;40H"u8);

    [Benchmark(Description = "Latency: Line Clear")]
    public void Latency_LineClear() => _parser.Feed("\u001b[K"u8);

    [Benchmark(Description = "Latency: Screen Clear")]
    public void Latency_ScreenClear() => _parser.Feed("\u001b[2J"u8);

    [Benchmark(Description = "Latency: Line Feed")]
    public void Latency_LineFeed() => _parser.Feed("\n"u8);

    [Benchmark(Description = "Latency: Tab Character")]
    public void Latency_Tab() => _parser.Feed("\t"u8);
}
