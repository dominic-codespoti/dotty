using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Memory allocation and GC pressure benchmarks
/// </summary>
[BenchmarkCategory("Memory")]
public class MemoryBenchmarks : PerformanceTestBase
{
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();
    }

    #region Grid Allocations

    [Benchmark(Description = "Allocate CellGrid 80x24")]
    public CellGrid Grid_Allocate_80x24() => new(24, 80);

    [Benchmark(Description = "Allocate CellGrid 120x40")]
    public CellGrid Grid_Allocate_120x40() => new(40, 120);

    [Benchmark(Description = "Allocate CellGrid 200x60")]
    public CellGrid Grid_Allocate_200x60() => new(60, 200);

    #endregion

    #region Buffer Operations

    [Benchmark(Description = "Buffer Resize Up")]
    public void Buffer_ResizeUp()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.Resize(40, 120);
    }

    [Benchmark(Description = "Buffer Resize Down")]
    public void Buffer_ResizeDown()
    {
        var buffer = new TerminalBuffer(60, 200);
        buffer.Resize(24, 80);
    }

    [Benchmark(Description = "Buffer Clear")]
    public void Buffer_Clear()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.EraseDisplay(2);
    }

    [Benchmark(Description = "Buffer Write Text 1KB")]
    public void Buffer_WriteText_1KB()
    {
        var buffer = new TerminalBuffer(24, 80);
        var text = TestDataGenerator.GeneratePlainText(1000);
        buffer.WriteText(System.Text.Encoding.UTF8.GetString(text).AsSpan(), CellAttributes.Default);
    }

    #endregion

    #region Scrollback Operations

    [Benchmark(Description = "Scrollback: Append 100 Lines")]
    public void Scrollback_Append100()
    {
        var buffer = new TerminalBuffer(24, 80);
        for (int i = 0; i < 100; i++)
        {
            buffer.WriteText($"Line {i}: This is a test line with some content here.", CellAttributes.Default);
            buffer.LineFeed();
        }
    }

    [Benchmark(Description = "Scrollback: Read History 100 Lines")]
    public void Scrollback_Read100()
    {
        var buffer = new TerminalBuffer(24, 80);
        
        // Populate scrollback
        for (int i = 0; i < 1000; i++)
        {
            buffer.WriteText($"Line {i}: Content", CellAttributes.Default);
            buffer.LineFeed();
        }
        
        // Read history
        for (int i = 0; i < 100; i++)
        {
            var line = buffer.GetScrollbackLine(i);
        }
    }

    #endregion

    #region Parser Allocations

    [Benchmark(Description = "Parser: Plain Text 10KB (alloc check)")]
    public void Parser_Plain_Allocations()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;
        
        var data = TestDataGenerator.GeneratePlainText(10000);
        parser.Feed(data);
    }

    [Benchmark(Description = "Parser: ANSI Text 10KB (alloc check)")]
    public void Parser_Ansi_Allocations()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;
        
        var data = TestDataGenerator.GenerateBasicAnsiText(10000, 0.1);
        parser.Feed(data);
    }

    #endregion

    #region Cell Operations

    [Benchmark(Description = "Cell: Set Attributes")]
    public void Cell_SetAttributes()
    {
        var grid = new CellGrid(24, 80);
        
        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 80; col++)
            {
                ref var cell = ref grid.GetRef(row, col);
                cell.Bold = true;
                cell.Italic = true;
                cell.Underline = true;
            }
        }
    }

    [Benchmark(Description = "Cell: Set Character")]
    public void Cell_SetCharacter()
    {
        var grid = new CellGrid(24, 80);
        
        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 80; col++)
            {
                grid.GetRef(row, col).SetAscii('X');
            }
        }
    }

    [Benchmark(Description = "Cell: Reset")]
    public void Cell_Reset()
    {
        var grid = new CellGrid(24, 80);
        
        // Fill first
        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 80; col++)
            {
                ref var cell = ref grid.GetRef(row, col);
                cell.SetAscii('X');
                cell.Bold = true;
            }
        }
        
        // Reset
        grid.ClearAll();
    }

    #endregion

    #region SGR Parsing

    [Benchmark(Description = "SGR Parse: Simple")]
    public CellAttributes Sgr_ParseSimple() => 
        SgrParserArgb.Apply("1;31", CellAttributes.Default);

    [Benchmark(Description = "SGR Parse: 256 Color")]
    public CellAttributes Sgr_Parse256Color() => 
        SgrParserArgb.Apply("38;5;196", CellAttributes.Default);

    [Benchmark(Description = "SGR Parse: TrueColor")]
    public CellAttributes Sgr_ParseTrueColor() => 
        SgrParserArgb.Apply("38;2;255;100;50", CellAttributes.Default);

    [Benchmark(Description = "SGR Parse: Complex")]
    public CellAttributes Sgr_ParseComplex() => 
        SgrParserArgb.Apply("1;3;4;38;2;255;0;0;48;2;0;0;255", CellAttributes.Default);

    #endregion
}
