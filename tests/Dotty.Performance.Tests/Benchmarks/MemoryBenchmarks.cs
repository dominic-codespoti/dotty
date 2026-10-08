using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Memory allocation and GC pressure benchmarks
/// </summary>
[BenchmarkCategory("Memory")]
public class MemoryBenchmarks : PerformanceTestBase
{
    private readonly TerminalPalette _sgrPalette = new();
    private TerminalBuffer _resizeBuffer = null!;
    private TerminalBuffer _buffer = null!;
    private TerminalBuffer _scrollbackBuffer = null!;
    private TerminalBuffer _historyBuffer = null!;
    private CellGrid _grid = null!;
    private ushort _styleId;
    private ushort _boldId;
    private BasicAnsiParser _plainParser = null!;
    private BasicAnsiParser _ansiParser = null!;
    private byte[] _plainData = null!;
    private byte[] _ansiData = null!;
    private string _text1Kb = null!;
    private readonly string[] _scrollbackLines = new string[100];

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _resizeBuffer = new TerminalBuffer(24, 80, scrollbackCapacity: 0);
        _buffer = new TerminalBuffer(24, 80, scrollbackCapacity: 0);
        _scrollbackBuffer = new TerminalBuffer(24, 80, scrollbackCapacity: 1000);
        _historyBuffer = new TerminalBuffer(24, 80, scrollbackCapacity: 1000);
        _grid = new CellGrid(24, 80);

        var styleSet = new StyleSet();
        _styleId = styleSet.GetOrCreateId(new CellAttributes { Bold = true, Italic = true, UnderlineStyle = UnderlineStyle.Single });
        _boldId = styleSet.GetOrCreateId(new CellAttributes { Bold = true });
        for (int i = 0; i < _scrollbackLines.Length; i++)
            _scrollbackLines[i] = $"Line {i}: This is a test line with some content here.";

        _text1Kb = System.Text.Encoding.UTF8.GetString(TestDataGenerator.GeneratePlainText(1000));
        _plainData = TestDataGenerator.GeneratePlainText(10000);
        _ansiData = TestDataGenerator.GenerateBasicAnsiText(10000, 0.1);
        _plainParser = new BasicAnsiParser { Handler = new TerminalAdapter(24, 80, scrollbackCapacity: 0) };
        _ansiParser = new BasicAnsiParser { Handler = new TerminalAdapter(24, 80, scrollbackCapacity: 0) };

        for (int i = 0; i < 1000; i++)
        {
            _scrollbackBuffer.WriteText("Line: Content", CellAttributes.Default);
            _scrollbackBuffer.LineFeed();
            _historyBuffer.WriteText("Line: Content", CellAttributes.Default);
            _historyBuffer.LineFeed();
        }
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
        _resizeBuffer.Resize(40, 120);
        _resizeBuffer.Resize(24, 80);
    }

    [Benchmark(Description = "Buffer Resize Down")]
    public void Buffer_ResizeDown()
    {
        _resizeBuffer.Resize(60, 200);
        _resizeBuffer.Resize(24, 80);
    }

    [Benchmark(Description = "Buffer Clear")]
    public void Buffer_Clear() => _buffer.EraseDisplay(2);

    [Benchmark(Description = "Buffer Write Text 1KB")]
    public void Buffer_WriteText_1KB() => _buffer.WriteText(_text1Kb.AsSpan(), CellAttributes.Default);

    #endregion

    #region Scrollback Operations

    [Benchmark(Description = "Scrollback: Append 100 Lines")]
    public void Scrollback_Append100()
    {
        for (int i = 0; i < 100; i++)
        {
            _scrollbackBuffer.WriteText(_scrollbackLines[i], CellAttributes.Default);
            _scrollbackBuffer.LineFeed();
        }
    }

    [Benchmark(Description = "Scrollback: Read History 100 Lines")]
    public void Scrollback_Read100()
    {
        for (int i = 0; i < 100; i++)
            _historyBuffer.GetScrollbackLine(i);
    }

    #endregion

    #region Parser Allocations

    [Benchmark(Description = "Parser: Plain Text 10KB (alloc check)")]
    public void Parser_Plain_Allocations() => _plainParser.Feed(_plainData);

    [Benchmark(Description = "Parser: ANSI Text 10KB (alloc check)")]
    public void Parser_Ansi_Allocations() => _ansiParser.Feed(_ansiData);

    #endregion

    #region Cell Operations

    [Benchmark(Description = "Cell: Set Attributes")]
    public void Cell_SetAttributes()
    {
        for (int row = 0; row < 24; row++)
            for (int col = 0; col < 80; col++)
                _grid.GetRef(row, col).StyleId = _styleId;
    }

    [Benchmark(Description = "Cell: Set Character")]
    public void Cell_SetCharacter()
    {
        for (int row = 0; row < 24; row++)
            for (int col = 0; col < 80; col++)
                _grid.GetRef(row, col).SetAscii('X');
    }

    [Benchmark(Description = "Cell: Reset")]
    public void Cell_Reset()
    {
        for (int row = 0; row < 24; row++)
            for (int col = 0; col < 80; col++)
            {
                ref var cell = ref _grid.GetRef(row, col);
                cell.SetAscii('X');
                cell.StyleId = _boldId;
            }
        _grid.ClearAll();
    }

    #endregion

    #region SGR Parsing

    [Benchmark(Description = "SGR Parse: Simple")]
    public CellAttributes Sgr_ParseSimple() =>
        SgrParserArgb.Apply("1;31", CellAttributes.Default, _sgrPalette);

    [Benchmark(Description = "SGR Parse: 256 Color")]
    public CellAttributes Sgr_Parse256Color() =>
        SgrParserArgb.Apply("38;5;196", CellAttributes.Default, _sgrPalette);

    [Benchmark(Description = "SGR Parse: TrueColor")]
    public CellAttributes Sgr_ParseTrueColor() =>
        SgrParserArgb.Apply("38;2;255;100;50", CellAttributes.Default, _sgrPalette);

    [Benchmark(Description = "SGR Parse: Complex")]
    public CellAttributes Sgr_ParseComplex() =>
        SgrParserArgb.Apply("1;3;4;38;2;255;0;0;48;2;0;0;255", CellAttributes.Default, _sgrPalette);

    #endregion
}
