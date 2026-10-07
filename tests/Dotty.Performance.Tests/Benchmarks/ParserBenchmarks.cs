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
    private byte[] _throughputPlain = null!;
    private byte[] _throughputAnsi = null!;
    private byte[] _chunkSmall = null!;
    private byte[] _chunkMedium = null!;

    // GlobalSetup inherited from PerformanceTestBase
    public override void GlobalSetup()
    {
        base.GlobalSetup();

        // Initialize parser and adapter
        _adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
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
        _throughputPlain = TestDataGenerator.GeneratePlainText(TestDataGenerator.Sizes.XLarge);
        _throughputAnsi = TestDataGenerator.GenerateBasicAnsiText(TestDataGenerator.Sizes.XLarge, 0.1);
        _chunkSmall = TestDataGenerator.GeneratePlainText(1000);
        _chunkMedium = TestDataGenerator.GeneratePlainText(10000);

        // This setup-generated 1 KB payload includes repeated CSI 2 J full-screen erases.
        // Pre-feed it here so the timed run measures steady-state mutation of the fixed grid.
        Warmup(() => _parser.Feed(_plainTextSmall), 5);
        _parser.Feed(_complexAnsi);
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
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(_throughputPlain);
        }
    }

    [Benchmark(Description = "Throughput - ANSI Text 1MB", OperationsPerInvoke = 10)]
    public void Throughput_AnsiText_1MB()
    {
        for (int i = 0; i < 10; i++)
        {
            _parser.Feed(_throughputAnsi);
        }
    }

    #endregion

    #region Chunks Parsing

    [Benchmark(Description = "Parse Chunks - 1KB x 10")]
    public void Chunks_1KBx10()
    {
        for (int i = 0; i < 10; i++)
            _parser.Feed(_chunkSmall);
    }

    [Benchmark(Description = "Parse Chunks - 10KB x 10")]
    public void Chunks_10KBx10()
    {
        for (int i = 0; i < 10; i++)
            _parser.Feed(_chunkMedium);
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

    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
        _parser = new BasicAnsiParser();
        _parser.Handler = _adapter;
        _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);
    }

    [Benchmark(Description = "Parse SGR: Bold", OperationsPerInvoke = 64)]
    public void ParseSgr_Bold()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b[1mHello\u001b[0m"u8);
    }

    [Benchmark(Description = "Parse SGR: Color (256)", OperationsPerInvoke = 64)]
    public void ParseSgr_256Color()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b[38;5;196mRed\u001b[0m"u8);
    }

    [Benchmark(Description = "Parse SGR: TrueColor", OperationsPerInvoke = 64)]
    public void ParseSgr_TrueColor()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b[38;2;255;0;0mRed\u001b[0m"u8);
    }

    [Benchmark(Description = "Parse Cursor: MoveTo", OperationsPerInvoke = 256)]
    public void ParseCursor_MoveTo()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b[10;20H"u8);
    }

    [Benchmark(Description = "Parse Cursor: Up/Down", OperationsPerInvoke = 256)]
    public void ParseCursor_UpDown()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b[5A\u001b[3B"u8);
    }

    [Benchmark(Description = "Parse Erase: Line", OperationsPerInvoke = 64)]
    public void ParseErase_Line()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b[K"u8);
    }

    [Benchmark(Description = "Parse Erase: Display", OperationsPerInvoke = 16)]
    public void ParseErase_Display()
    {
        for (int i = 0; i < 16; i++) _parser.Feed("\u001b[2J"u8);
    }

    [Benchmark(Description = "Parse Mode: Alternate Screen", OperationsPerInvoke = 64)]
    public void ParseMode_AlternateScreen()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b[?1049h\u001b[?1049l"u8);
    }

    [Benchmark(Description = "Parse Mode: Cursor Visibility", OperationsPerInvoke = 256)]
    public void ParseMode_CursorVisibility()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b[?25l\u001b[?25h"u8);
    }

    [Benchmark(Description = "Parse OSC: Window Title", OperationsPerInvoke = 256)]
    public void ParseOsc_WindowTitle()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b]0;Terminal\u0007"u8);
    }

    [Benchmark(Description = "Parse Query: DECRQM set/reset", OperationsPerInvoke = 256)]
    public void ParseQuery_Decrqm()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b[?1$p\u001b[?2004$p"u8);
    }

    [Benchmark(Description = "Parse Query: XTGETTCAP TN", OperationsPerInvoke = 256)]
    public void ParseQuery_Xtgettcap()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001bP+q544E\u001b\\"u8);
    }

    [Benchmark(Description = "Parse Query: modifyOtherKeys negotiate", OperationsPerInvoke = 256)]
    public void ParseQuery_ModifyOtherKeys()
    {
        for (int i = 0; i < 256; i++) _parser.Feed("\u001b[>4;1m\u001b[>4;0m"u8);
    }

    [Benchmark(Description = "Parse OSC 4: palette set + query", OperationsPerInvoke = 64)]
    public void ParseOsc4_PaletteSetQuery()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b]4;1;#ff0000\u0007\u001b]4;1;?\u0007"u8);
    }

    [Benchmark(Description = "Parse OSC 10: dynamic color set + query", OperationsPerInvoke = 64)]
    public void ParseOsc10_DynamicSetQuery()
    {
        for (int i = 0; i < 64; i++) _parser.Feed("\u001b]10;#112233\u0007\u001b]10;?\u0007"u8);
    }

    [Benchmark(Description = "Parse Unicode: 2-byte", OperationsPerInvoke = 16)]
    public void ParseUnicode_2Byte()
    {
        for (int i = 0; i < 16; i++) _parser.Feed("\u00e4\u00f6\u00fc"u8);
    }

    [Benchmark(Description = "Parse Unicode: 3-byte", OperationsPerInvoke = 16)]
    public void ParseUnicode_3Byte()
    {
        for (int i = 0; i < 16; i++) _parser.Feed("\u4e2d\u6587\u6d4b\u8bd5"u8);
    }

    [Benchmark(Description = "Parse Unicode: 4-byte (emoji)", OperationsPerInvoke = 16)]
    public void ParseUnicode_4Byte()
    {
        for (int i = 0; i < 16; i++) _parser.Feed("\ud83d\ude80\ud83d\udc34\ud83c\udf89"u8);
    }
}
