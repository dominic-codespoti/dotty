using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Startup time benchmarks
/// </summary>
[BenchmarkCategory("Startup")]
public class StartupBenchmarks : PerformanceTestBase
{
    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();
    }

    #region Terminal Initialization

    [Benchmark(Description = "Cold Start: TerminalAdapter 80x24")]
    public TerminalAdapter ColdStart_80x24() => new(24, 80);

    [Benchmark(Description = "Cold Start: TerminalAdapter 120x40")]
    public TerminalAdapter ColdStart_120x40() => new(40, 120);

    [Benchmark(Description = "Cold Start: Full Setup (Parser+Adapter)")]
    public (BasicAnsiParser, TerminalAdapter) ColdStart_FullSetup()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        return (parser, adapter);
    }

    #endregion

    #region Parser Initialization

    [Benchmark(Description = "Init: BasicAnsiParser")]
    public BasicAnsiParser Init_Parser()
    {
        return new BasicAnsiParser();
    }

    [Benchmark(Description = "Init: Parser + Handler Setup")]
    public BasicAnsiParser Init_ParserWithHandler()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;
        return parser;
    }

    #endregion

    #region Buffer Initialization

    [Benchmark(Description = "Init: CellGrid 80x24")]
    public CellGrid Init_CellGrid_80x24() => new(24, 80);

    [Benchmark(Description = "Init: CellGrid 120x40")]
    public CellGrid Init_CellGrid_120x40() => new(40, 120);

    [Benchmark(Description = "Init: TerminalBuffer 80x24")]
    public TerminalBuffer Init_TerminalBuffer_80x24() => new(24, 80);

    [Benchmark(Description = "Init: TerminalBuffer with Scrollback")]
    public TerminalBuffer Init_TerminalBuffer_WithScrollback() => 
        new(24, 80);

    #endregion

    #region First Frame

    [Benchmark(Description = "First Frame: Parse 1KB")]
    public void FirstFrame_Parse1KB()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        var data = TestDataGenerator.GeneratePlainText(1000);
        parser.Feed(data);
    }

    [Benchmark(Description = "First Frame: Parse + Render 1KB")]
    public void FirstFrame_ParseAndRender()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        var data = TestDataGenerator.GenerateBasicAnsiText(1000, 0.1);
        parser.Feed(data);
        adapter.RequestRenderExtern();
    }

    #endregion

    #region Resize Operations

    [Benchmark(Description = "Resize: 80x24 -> 120x40")]
    public void Resize_80x24_to_120x40()
    {
        var adapter = new TerminalAdapter(24, 80);
        // Fill with some content first
        var data = TestDataGenerator.GeneratePlainText(10000);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(data);
        // Resize
        adapter.ResizeBuffer(40, 120);
    }

    [Benchmark(Description = "Resize: 120x40 -> 80x24")]
    public void Resize_120x40_to_80x24()
    {
        var adapter = new TerminalAdapter(40, 120);
        var data = TestDataGenerator.GeneratePlainText(10000);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(data);
        adapter.ResizeBuffer(24, 80);
    }

    #endregion
}
