using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Rendering and buffer manipulation performance benchmarks
/// </summary>
[BenchmarkCategory("Rendering")]
public class RenderingBenchmarks : PerformanceTestBase
{
    private TerminalAdapter _adapter = null!;
    private BasicAnsiParser _parser = null!;
    private byte[] _fullScreenData80x24 = null!;
    private byte[] _fullScreenData120x40 = null!;
    private byte[] _scrollData = null!;
    private List<byte[]> _progressiveUpdates = null!;

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();

        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser();
        _parser.Handler = _adapter;

        // Pre-generate test data
        _fullScreenData80x24 = TestDataGenerator.GenerateFullScreenRedraw(24, 80);
        _fullScreenData120x40 = TestDataGenerator.GenerateFullScreenRedraw(40, 120);
        _scrollData = TestDataGenerator.GenerateScrollingWorkload(1000);
        _progressiveUpdates = TestDataGenerator.GenerateProgressiveUpdates(100, 100);

        Warmup(() => _parser.Feed(_fullScreenData80x24), 3);
    }

    #region Full Screen Rendering

    [Benchmark(Description = "Full Screen Redraw 80x24")]
    public void FullScreenRedraw_80x24()
    {
        _parser.Feed(_fullScreenData80x24);
    }

    [Benchmark(Description = "Full Screen Redraw 120x40")]
    public void FullScreenRedraw_120x40()
    {
        // Reset and resize
        _adapter.ResizeBuffer(40, 120);
        _parser.Feed(_fullScreenData120x40);
    }

    [Benchmark(Description = "Full Screen Redraw 200x60")]
    public void FullScreenRedraw_200x60()
    {
        var data = TestDataGenerator.GenerateFullScreenRedraw(60, 200);
        _adapter.ResizeBuffer(60, 200);
        _parser.Feed(data);
    }

    #endregion

    #region Scroll Operations

    [Benchmark(Description = "Scroll: 100 Lines")]
    public void Scroll_100Lines()
    {
        // Setup buffer
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        _parser.Feed(_scrollData);
    }

    [Benchmark(Description = "Scroll: 500 Lines")]
    public void Scroll_500Lines()
    {
        var data = TestDataGenerator.GenerateScrollingWorkload(500);
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        _parser.Feed(data);
    }

    [Benchmark(Description = "Scroll: 1000 Lines")]
    public void Scroll_1000Lines()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        _parser.Feed(_scrollData);
    }

    #endregion

    #region Progressive Updates

    [Benchmark(Description = "Progressive Update: 10 updates")]
    public void ProgressiveUpdate_10()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        
        for (int i = 0; i < Math.Min(10, _progressiveUpdates.Count); i++)
        {
            _parser.Feed(_progressiveUpdates[i]);
        }
    }

    [Benchmark(Description = "Progressive Update: 50 updates")]
    public void ProgressiveUpdate_50()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        
        for (int i = 0; i < Math.Min(50, _progressiveUpdates.Count); i++)
        {
            _parser.Feed(_progressiveUpdates[i]);
        }
    }

    #endregion

    #region Cursor Operations

    [Benchmark(Description = "Cursor: Move + Print 100x")]
    public void Cursor_MoveAndPrint()
    {
        var buffer = new TerminalBuffer(24, 80);
        var attrs = CellAttributes.Default;
        
        for (int i = 0; i < 100; i++)
        {
            buffer.SetCursor(i % 24, i % 80);
            buffer.WriteText("X", attrs);
        }
    }

    [Benchmark(Description = "Cursor: Random Jumps 100x")]
    public void Cursor_RandomJumps()
    {
        var random = new Random(42);
        var buffer = new TerminalBuffer(24, 80);
        
        for (int i = 0; i < 100; i++)
        {
            buffer.SetCursor(random.Next(24), random.Next(80));
        }
    }

    #endregion

    #region Cell Rendering

    [Benchmark(Description = "Render: Clear 80x24")]
    public void Render_Clear80x24()
    {
        var grid = new CellGrid(24, 80);
        grid.ClearAll();
    }

    [Benchmark(Description = "Render: Fill 80x24")]
    public void Render_Fill80x24()
    {
        var grid = new CellGrid(24, 80);
        
        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 80; col++)
            {
                ref var cell = ref grid.GetRef(row, col);
                cell.SetAscii((char)('A' + (col % 26)));
                cell.Bold = true;
            }
        }
    }

    #endregion

    #region Buffer Operations

    [Benchmark(Description = "Buffer: Line Feed 100x")]
    public void Buffer_LineFeed100()
    {
        var buffer = new TerminalBuffer(24, 80);
        
        for (int i = 0; i < 100; i++)
        {
            buffer.LineFeed();
        }
    }

    [Benchmark(Description = "Buffer: Insert Lines 10x")]
    public void Buffer_InsertLines10()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursor(10, 0);
        
        for (int i = 0; i < 10; i++)
        {
            buffer.InsertLines(1);
        }
    }

    [Benchmark(Description = "Buffer: Delete Lines 10x")]
    public void Buffer_DeleteLines10()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursor(10, 0);
        
        for (int i = 0; i < 10; i++)
        {
            buffer.DeleteLines(1);
        }
    }

    #endregion

    #region Erase Operations

    [Benchmark(Description = "Erase: Display")]
    public void Erase_Display()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.EraseDisplay(2);
    }

    [Benchmark(Description = "Erase: Line")]
    public void Erase_Line()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursor(10, 0);
        buffer.EraseLine(2);
    }

    [Benchmark(Description = "Erase: Line End")]
    public void Erase_LineEnd()
    {
        var buffer = new TerminalBuffer(24, 80);
        buffer.SetCursor(10, 40);
        buffer.EraseLine(0);
    }

    #endregion
}
