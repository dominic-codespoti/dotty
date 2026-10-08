using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Rendering and buffer manipulation performance benchmarks
/// </summary>
[BenchmarkCategory("Rendering")]
public class RenderingBenchmarks : PerformanceTestBase
{
    private BasicAnsiParser _parser80x24 = null!;
    private BasicAnsiParser _parser120x40 = null!;
    private BasicAnsiParser _parser200x60 = null!;
    private BasicAnsiParser _scrollParser100 = null!;
    private BasicAnsiParser _scrollParser500 = null!;
    private BasicAnsiParser _scrollParser1000 = null!;
    private BasicAnsiParser _progressiveParser = null!;
    private byte[] _fullScreenData80x24 = null!;
    private byte[] _fullScreenData120x40 = null!;
    private byte[] _fullScreenData200x60 = null!;
    private byte[] _scrollData100 = null!;
    private byte[] _scrollData500 = null!;
    private byte[] _scrollData1000 = null!;
    private List<byte[]> _progressiveUpdates = null!;
    private TerminalBuffer _buffer = null!;
    private CellGrid _grid = null!;
    private ushort _boldId;
    private readonly int[] _cursorRows = new int[100];
    private readonly int[] _cursorColumns = new int[100];

    public override void GlobalSetup()
    {
        base.GlobalSetup();

        _parser80x24 = CreateParser(24, 80);
        _parser120x40 = CreateParser(40, 120);
        _parser200x60 = CreateParser(60, 200);
        _scrollParser100 = CreateParser(24, 80);
        _scrollParser500 = CreateParser(24, 80);
        _scrollParser1000 = CreateParser(24, 80);
        _progressiveParser = CreateParser(24, 80);

        _fullScreenData80x24 = TestDataGenerator.GenerateFullScreenRedraw(24, 80);
        _fullScreenData120x40 = TestDataGenerator.GenerateFullScreenRedraw(40, 120);
        _fullScreenData200x60 = TestDataGenerator.GenerateFullScreenRedraw(60, 200);
        _scrollData100 = TestDataGenerator.GenerateScrollingWorkload(100);
        _scrollData500 = TestDataGenerator.GenerateScrollingWorkload(500);
        _scrollData1000 = TestDataGenerator.GenerateScrollingWorkload(1000);
        _progressiveUpdates = TestDataGenerator.GenerateProgressiveUpdates(100, 100);

        _buffer = new TerminalBuffer(24, 80, scrollbackCapacity: 0);
        _grid = new CellGrid(24, 80);
        var styleSet = new StyleSet();
        _boldId = styleSet.GetOrCreateId(new CellAttributes { Bold = true });
        for (int i = 0; i < _cursorRows.Length; i++)
        {
            _cursorRows[i] = i % 24;
            _cursorColumns[i] = i % 80;
        }

        Warmup(() => _parser80x24.Feed(_fullScreenData80x24), 3);
        Warmup(() => _parser120x40.Feed(_fullScreenData120x40), 3);
        Warmup(() => _parser200x60.Feed(_fullScreenData200x60), 3);
        Warmup(() => _scrollParser100.Feed(_scrollData100), 3);
        Warmup(() => _scrollParser500.Feed(_scrollData500), 3);
        Warmup(() => _scrollParser1000.Feed(_scrollData1000), 3);
        Warmup(() => _progressiveParser.Feed(_progressiveUpdates[0]), 3);
    }

    private static BasicAnsiParser CreateParser(int rows, int columns)
    {
        var adapter = new TerminalAdapter(rows, columns, scrollbackCapacity: 0);
        var parser = new BasicAnsiParser { Handler = adapter };
        return parser;
    }

    #region Full Screen Rendering

    [Benchmark(Description = "Full Screen Redraw 80x24")]
    public void FullScreenRedraw_80x24() => _parser80x24.Feed(_fullScreenData80x24);

    [Benchmark(Description = "Full Screen Redraw 120x40")]
    public void FullScreenRedraw_120x40() => _parser120x40.Feed(_fullScreenData120x40);

    [Benchmark(Description = "Full Screen Redraw 200x60")]
    public void FullScreenRedraw_200x60() => _parser200x60.Feed(_fullScreenData200x60);

    #endregion

    #region Scroll Operations

    [Benchmark(Description = "Scroll: 100 Lines")]
    public void Scroll_100Lines() => _scrollParser100.Feed(_scrollData100);

    [Benchmark(Description = "Scroll: 500 Lines")]
    public void Scroll_500Lines() => _scrollParser500.Feed(_scrollData500);

    [Benchmark(Description = "Scroll: 1000 Lines")]
    public void Scroll_1000Lines() => _scrollParser1000.Feed(_scrollData1000);

    #endregion

    #region Progressive Updates

    [Benchmark(Description = "Progressive Update: 10 updates")]
    public void ProgressiveUpdate_10()
    {
        for (int i = 0; i < 10; i++)
            _progressiveParser.Feed(_progressiveUpdates[i]);
    }

    [Benchmark(Description = "Progressive Update: 50 updates")]
    public void ProgressiveUpdate_50()
    {
        for (int i = 0; i < 50; i++)
            _progressiveParser.Feed(_progressiveUpdates[i]);
    }

    #endregion

    #region Cursor Operations

    [Benchmark(Description = "Cursor: Move + Print 100x")]
    public void Cursor_MoveAndPrint()
    {
        var attrs = CellAttributes.Default;
        for (int i = 0; i < 100; i++)
        {
            _buffer.SetCursor(_cursorRows[i], _cursorColumns[i]);
            _buffer.WriteText("X", attrs);
        }
    }

    [Benchmark(Description = "Cursor: Random Jumps 100x")]
    public void Cursor_RandomJumps()
    {
        for (int i = 0; i < 100; i++)
            _buffer.SetCursor(_cursorRows[i], _cursorColumns[i]);
    }

    #endregion

    #region Cell Rendering

    [Benchmark(Description = "Render: Clear 80x24", OperationsPerInvoke = 16)]
    public void Render_Clear80x24()
    {
        for (int i = 0; i < 16; i++)
            _grid.ClearAll();
    }

    [Benchmark(Description = "Render: Fill 80x24", OperationsPerInvoke = 16)]
    public void Render_Fill80x24()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            for (int row = 0; row < 24; row++)
            {
                for (int col = 0; col < 80; col++)
                {
                    ref var cell = ref _grid.GetRef(row, col);
                    cell.SetAscii((char)('A' + (col % 26)));
                    cell.StyleId = _boldId;
                }
            }
        }
    }

    #endregion

    #region Buffer Operations

    [Benchmark(Description = "Buffer: Line Feed 100x", OperationsPerInvoke = 1600)]
    public void Buffer_LineFeed100()
    {
        for (int repeat = 0; repeat < 16; repeat++)
        {
            for (int i = 0; i < 100; i++)
                _buffer.LineFeed();
        }
    }

    [Benchmark(Description = "Buffer: Insert Lines 10x", OperationsPerInvoke = 160)]
    public void Buffer_InsertLines10()
    {
        for (int repeat = 0; repeat < 16; repeat++)
        {
            _buffer.SetCursor(10, 0);
            for (int i = 0; i < 10; i++)
                _buffer.InsertLines(1);
        }
    }

    [Benchmark(Description = "Buffer: Delete Lines 10x", OperationsPerInvoke = 160)]
    public void Buffer_DeleteLines10()
    {
        for (int repeat = 0; repeat < 16; repeat++)
        {
            _buffer.SetCursor(10, 0);
            for (int i = 0; i < 10; i++)
                _buffer.DeleteLines(1);
        }
    }

    #endregion

    #region Erase Operations

    [Benchmark(Description = "Erase: Display", OperationsPerInvoke = 16)]
    public void Erase_Display()
    {
        for (int i = 0; i < 16; i++)
            _buffer.EraseDisplay(2);
    }

    [Benchmark(Description = "Erase: Line", OperationsPerInvoke = 128)]
    public void Erase_Line()
    {
        for (int i = 0; i < 128; i++)
        {
            _buffer.SetCursor(10, 0);
            _buffer.EraseLine(2);
        }
    }

    [Benchmark(Description = "Erase: Line End", OperationsPerInvoke = 128)]
    public void Erase_LineEnd()
    {
        for (int i = 0; i < 128; i++)
        {
            _buffer.SetCursor(10, 40);
            _buffer.EraseLine(0);
        }
    }

    #endregion
}
