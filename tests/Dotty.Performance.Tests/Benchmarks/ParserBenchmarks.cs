using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// ANSI sequence parsing performance benchmarks
/// </summary>
[BenchmarkCategory("Parser")]
public class ParserBenchmarks : PerformanceTestBase
{
    private BasicAnsiParser _parser = null!;
    private TerminalAdapter _adapter = null!;
    
    // Test data at different sizes
    private byte[] _plainTextTiny = null!;
    private byte[] _plainTextSmall = null!;
    private byte[] _plainTextMedium = null!;
    private byte[] _plainTextLarge = null!;
    
    private byte[] _ansiBasicTiny = null!;
    private byte[] _ansiBasicSmall = null!;
    private byte[] _ansiExtended = null!;
    private byte[] _ansiTrueColor = null!;
    private byte[] _complexAnsi = null!;
    private byte[] _logOutput = null!;
    private byte[] _shellSession = null!;
    private byte[] _mouseEvents = null!;
    private byte[] _oscSequences = null!;

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();

        // Initialize parser and adapter
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser();
        _parser.Handler = _adapter;

        // Generate test data
        _plainTextTiny = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.Tiny);
        _plainTextSmall = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.Small);
        _plainTextMedium = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.Medium);
        _plainTextLarge = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.Large);
        
        _ansiBasicTiny = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.Tiny, 0.05);
        _ansiBasicSmall = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.Small, 0.05);
        _ansiExtended = TestDataGenerator.GenerateExtendedAnsiText(TestDataGenerator.Sizes.Small);
        _ansiTrueColor = TestDataGenerator.GenerateTrueColorAnsiText(TestDataGenerator.Sizes.Small);
        _complexAnsi = TestDataGenerator.GenerateComplexAnsi(TestDataGenerator.Sizes.Small);
        _logOutput = TestDataGenerator.GenerateLogOutput(100);
        _shellSession = TestDataGenerator.GenerateShellSession(20);
        _mouseEvents = TestDataGenerator.GenerateMouseEvents(1000);
        _oscSequences = TestDataGenerator.GenerateOscSequences(100);

        // Warmup
        Warmup(() => _parser.Feed(_plainTextSmall), 5);
    }

    #region Plain Text Parsing

    [Benchmark(Description = "Parse Plain Text 100B")]
    public void PlainText_Tiny() => _parser.Feed(_plainTextTiny);

    [Benchmark(Description = "Parse Plain Text 1KB")]
    public void PlainText_Small() => _parser.Feed(_plainTextSmall);

    [Benchmark(Description = "Parse Plain Text 10KB")]
    public void PlainText_Medium() => _parser.Feed(_plainTextMedium);

    [Benchmark(Description = "Parse Plain Text 100KB")]
    public void PlainText_Large() => _parser.Feed(_plainTextLarge);

    #endregion

    #region Basic ANSI Parsing

    [Benchmark(Description = "Parse Basic ANSI 100B")]
    public void AnsiBasic_Tiny() => _parser.Feed(_ansiBasicTiny);

    [Benchmark(Description = "Parse Basic ANSI 1KB (5% density)")]
    public void AnsiBasic_Small() => _parser.Feed(_ansiBasicSmall);

    #endregion

    #region Extended ANSI Parsing

    [Benchmark(Description = "Parse 256-Color ANSI 1KB")]
    public void AnsiExtended_1KB() => _parser.Feed(_ansiExtended);

    [Benchmark(Description = "Parse TrueColor ANSI 1KB")]
    public void AnsiTrueColor_1KB() => _parser.Feed(_ansiTrueColor);

    #endregion

    #region Complex Sequence Parsing

    [Benchmark(Description = "Parse Complex Sequences 1KB")]
    public void ComplexSequences_1KB() => _parser.Feed(_complexAnsi);

    [Benchmark(Description = "Parse Log Output (100 lines)")]
    public void LogOutput_100Lines() => _parser.Feed(_logOutput);

    [Benchmark(Description = "Parse Shell Session (20 cmds)")]
    public void ShellSession_20Cmds() => _parser.Feed(_shellSession);

    #endregion

    #region Specialized Parsing

    [Benchmark(Description = "Parse Mouse Events (1K events)")]
    public void MouseEvents_1K() => _parser.Feed(_mouseEvents);

    [Benchmark(Description = "Parse OSC Sequences (100 seqs)")]
    public void OscSequences_100() => _parser.Feed(_oscSequences);

    #endregion

    #region Throughput Benchmarks

    [Benchmark(Description = "Throughput - Plain Text 1MB", OperationsPerInvoke = 10)]
    public void Throughput_PlainText_1MB()
    {
        var data = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.XLarge);
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(data);
        }
    }

    [Benchmark(Description = "Throughput - ANSI Text 1MB", OperationsPerInvoke = 10)]
    public void Throughput_AnsiText_1MB()
    {
        var data = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.XLarge, 0.1);
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(data);
        }
    }

    #endregion

    #region Chunks Parsing

    [Benchmark(Description = "Parse Chunks - 1KB x 10")]
    public void Chunks_1KBx10()
    {
        var chunk = TestDataGenerator.GeneratePlainText(1000);
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(chunk);
        }
    }

    [Benchmark(Description = "Parse Chunks - 10KB x 10")]
    public void Chunks_10KBx10()
    {
        var chunk = TestDataGenerator.GeneratePlainText(10000);
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(chunk);
        }
    }

    #endregion
}

/// <summary>
/// Additional parser benchmarks focusing on specific operations
/// </summary>
[BenchmarkCategory("Parser", "Micro")]
public class ParserMicroBenchmarks : PerformanceTestBase
{
    private BasicAnsiParser _parser = null!;
    private TerminalAdapter _adapter = null!;

    [GlobalSetup]
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser();
        _parser.Handler = _adapter;
    }

    [Benchmark(Description = "Parse SGR: Bold")]
    public void ParseSgr_Bold() => _parser.Feed("\u001b[1mHello\u001b[0m"u8);

    [Benchmark(Description = "Parse SGR: Color (256)")]
    public void ParseSgr_256Color() => _parser.Feed("\u001b[38;5;196mRed\u001b[0m"u8);

    [Benchmark(Description = "Parse SGR: TrueColor")]
    public void ParseSgr_TrueColor() => _parser.Feed("\u001b[38;2;255;0;0mRed\u001b[0m"u8);

    [Benchmark(Description = "Parse Cursor: MoveTo")]
    public void ParseCursor_MoveTo() => _parser.Feed("\u001b[10;20H"u8);

    [Benchmark(Description = "Parse Cursor: Up/Down")]
    public void ParseCursor_UpDown() => _parser.Feed("\u001b[5A\u001b[3B"u8);

    [Benchmark(Description = "Parse Erase: Line")]
    public void ParseErase_Line() => _parser.Feed("\u001b[K"u8);

    [Benchmark(Description = "Parse Erase: Display")]
    public void ParseErase_Display() => _parser.Feed("\u001b[2J"u8);

    [Benchmark(Description = "Parse Mode: Alternate Screen")]
    public void ParseMode_AlternateScreen() => _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);

    [Benchmark(Description = "Parse Mode: Cursor Visibility")]
    public void ParseMode_CursorVisibility() => _parser.Feed("\u001b[?25l\u001b[?25h"u8);

    [Benchmark(Description = "Parse OSC: Window Title")]
    public void ParseOsc_WindowTitle() => _parser.Feed("\u001b]0;Terminal\u0007"u8);

    [Benchmark(Description = "Parse Unicode: 2-byte")]
    public void ParseUnicode_2Byte() => _parser.Feed("\u00e4\u00f6\u00fc"u8);

    [Benchmark(Description = "Parse Unicode: 3-byte")]
    public void ParseUnicode_3Byte() => _parser.Feed("\u4e2d\u6587\u6d4b\u8bd5"u8);

    [Benchmark(Description = "Parse Unicode: 4-byte (emoji)")]
    public void ParseUnicode_4Byte() => _parser.Feed("\ud83d\ude80\ud83d\udc34\ud83c\udf89"u8);
}
