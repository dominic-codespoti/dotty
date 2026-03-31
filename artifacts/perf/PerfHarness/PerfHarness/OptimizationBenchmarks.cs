using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Dotty.App.Rendering;
using SkiaSharp;

namespace PerfHarness;

/// <summary>
/// Benchmarks targeting the specific performance optimizations:
/// 1. GraphemePool hash-based lookup
/// 2. HyperlinkId dictionary lookup
/// 3. Cell.Grapheme caching (avoiding char.ConvertFromUtf32)
/// </summary>
[MemoryDiagnoser]
public class OptimizationBenchmarks
{
    // --- GraphemePool benchmarks ---
    private string[] _multiCharGraphemes = null!;
    private char[][] _graphemeChars = null!;

    [Params(10, 100, 1000)]
    public int GraphemeCount { get; set; }

    [GlobalSetup(Target = nameof(GraphemePoolBenchmark))]
    public void GraphemePoolSetup()
    {
        // Generate diverse multi-character graphemes (emoji, combining marks, etc.)
        _multiCharGraphemes = new string[GraphemeCount];
        _graphemeChars = new char[GraphemeCount][];
        
        var baseEmojis = new[] { "👨", "👩", "👧", "👦", "🎨", "🎭", "🎪", "🎯", "🎲", "🎸" };
        var skinTones = new[] { "", "\uD83C\uDFFB", "\uD83C\uDFFC", "\uD83C\uDFFD", "\uD83C\uDFFE", "\uD83C\uDFFF" };
        var combiningMarks = new[] { "", "\u0300", "\u0301", "\u0302", "\u0303", "\u0304" };
        
        for (int i = 0; i < GraphemeCount; i++)
        {
            var baseEmoji = baseEmojis[i % baseEmojis.Length];
            var skinTone = skinTones[(i / baseEmojis.Length) % skinTones.Length];
            var combining = combiningMarks[i % combiningMarks.Length];
            
            _multiCharGraphemes[i] = baseEmoji + skinTone + combining;
            _graphemeChars[i] = _multiCharGraphemes[i].ToCharArray();
        }
    }

    [Benchmark]
    public void GraphemePoolBenchmark()
    {
        // Clear any cached graphemes first by using new instances
        for (int i = 0; i < GraphemeCount; i++)
        {
            // Create new char arrays to force pool lookup
            var chars = _graphemeChars[i].AsSpan();
            var grapheme = GraphemePool.GetOrAdd(chars);
            // Force evaluation
            _ = grapheme.Length;
        }
    }

    // --- Hyperlink benchmarks ---
    private TerminalBuffer _buffer = null!;
    private string[] _uris = null!;

    [Params(10, 100, 500)]
    public int UriCount { get; set; }

    [GlobalSetup(Target = nameof(HyperlinkBenchmark))]
    public void HyperlinkSetup()
    {
        _buffer = new TerminalBuffer(24, 80);
        _uris = new string[UriCount];
        
        for (int i = 0; i < UriCount; i++)
        {
            _uris[i] = $"https://example.com/page/{i}/path/to/resource?query={i}&other=value{i}";
        }
    }

    [Benchmark]
    public void HyperlinkBenchmark()
    {
        // Mix of existing and new URIs to simulate real usage
        for (int i = 0; i < UriCount * 2; i++)
        {
            // First pass adds all URIs, second pass looks them up
            int idx = i % UriCount;
            _ = _buffer.GetOrCreateHyperlinkId(_uris[idx]);
        }
    }

    // --- Rendering benchmarks ---
    private TerminalAdapter _adapter = null!;
    private TerminalFrameComposer _composer = null!;
    private SKPaint _paint = null!;
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;

    [GlobalSetup(Target = nameof(RenderAsciiBenchmark))]
    public void RenderAsciiSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _composer = new TerminalFrameComposer();

        _paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            IsAntialias = true,
            LcdRenderText = true,
            SubpixelText = true,
            IsLinearText = true,
            IsAutohinted = true
        };

        // Fill buffer with ASCII content
        var asciiLine = new string('A', 80);
        for (int i = 0; i < 24; i++)
        {
            _adapter.Buffer!.WriteText(asciiLine.AsSpan(), CellAttributes.Default);
            if (i < 23)
            {
                _adapter.Buffer!.LineFeed();
                _adapter.Buffer!.CarriageReturn();
            }
        }

        _bitmap = new SKBitmap(1600, 900, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
    }

    [GlobalSetup(Target = nameof(RenderUnicodeBenchmark))]
    public void RenderUnicodeSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _composer = new TerminalFrameComposer();

        _paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            IsAntialias = true,
            LcdRenderText = true,
            SubpixelText = true,
            IsLinearText = true,
            IsAutohinted = true
        };

        // Fill buffer with Unicode content
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            // Mix of ASCII and Unicode
            sb.Append(i % 2 == 0 ? 'A' : 'é');
        }
        var unicodeLine = sb.ToString();

        for (int i = 0; i < 24; i++)
        {
            _adapter.Buffer!.WriteText(unicodeLine.AsSpan(), CellAttributes.Default);
            if (i < 23)
            {
                _adapter.Buffer!.LineFeed();
                _adapter.Buffer!.CarriageReturn();
            }
        }

        _bitmap = new SKBitmap(1600, 900, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
    }

    [IterationSetup(Target = nameof(RenderAsciiBenchmark))]
    public void RenderAsciiIterationSetup()
    {
        _canvas.Clear(SKColors.Black);
    }

    [IterationSetup(Target = nameof(RenderUnicodeBenchmark))]
    public void RenderUnicodeIterationSetup()
    {
        _canvas.Clear(SKColors.Black);
    }

    [Benchmark]
    public void RenderAsciiBenchmark()
    {
        var buffer = _adapter.Buffer!;
        _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, 0, buffer.Rows - 1);
    }

    [Benchmark]
    public void RenderUnicodeBenchmark()
    {
        var buffer = _adapter.Buffer!;
        _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, 0, buffer.Rows - 1);
    }

    // --- Full stack benchmarks with various workloads ---
    private BasicAnsiParser _parser = null!;
    private byte[] _emojiPayload = null!;
    private byte[] _hyperlinkPayload = null!;

    [GlobalSetup(Target = nameof(ParseEmojiHeavy))]
    public void EmojiPayloadSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };

        var sb = new StringBuilder();
        for (int i = 0; i < 10000; i++)
        {
            sb.Append("👨‍👩‍👧‍👦 ");
        }
        _emojiPayload = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [GlobalSetup(Target = nameof(ParseHyperlinkHeavy))]
    public void HyperlinkPayloadSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };

        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            // OSC 8 hyperlink sequences
            sb.Append($"\x1b]8;;https://example.com/{i}\x1b\\Link{i}\x1b]8;;\x1b\\ ");
            if (i % 80 == 0) sb.Append("\r\n");
        }
        _hyperlinkPayload = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [IterationSetup(Target = nameof(ParseEmojiHeavy))]
    public void EmojiIterationSetup()
    {
        _adapter.OnClearScrollback();
        _adapter.OnEraseDisplay(2);
        _adapter.OnMoveCursor(1, 1);
    }

    [IterationSetup(Target = nameof(ParseHyperlinkHeavy))]
    public void HyperlinkIterationSetup()
    {
        _adapter.OnClearScrollback();
        _adapter.OnEraseDisplay(2);
        _adapter.OnMoveCursor(1, 1);
    }

    [Benchmark]
    public void ParseEmojiHeavy()
    {
        _parser.Feed(_emojiPayload);
    }

    [Benchmark]
    public void ParseHyperlinkHeavy()
    {
        _parser.Feed(_hyperlinkPayload);
    }
}
