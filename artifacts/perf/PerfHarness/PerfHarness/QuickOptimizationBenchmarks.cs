using System;
using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Dotty.App.Rendering;
using SkiaSharp;

namespace PerfHarness;

/// <summary>
/// Quick focused benchmarks for the specific optimizations.
/// Run with: --filter "*QuickOptimizationBenchmarks*" --job short
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class QuickOptimizationBenchmarks
{
    // --- GraphemePool benchmarks ---
    private string[] _multiCharGraphemes = null!;

    [Params(100, 1000)]
    public int GraphemeCount { get; set; }

    [GlobalSetup(Target = nameof(GraphemePoolLookup))]
    public void GraphemePoolSetup()
    {
        _multiCharGraphemes = new string[GraphemeCount];
        var baseEmojis = new[] { "👨", "👩", "👧", "👦", "🎨", "🎭", "🎪", "🎯" };
        var skinTones = new[] { "", "\uD83C\uDFFB", "\uD83C\uDFFC" };
        
        for (int i = 0; i < GraphemeCount; i++)
        {
            _multiCharGraphemes[i] = baseEmojis[i % baseEmojis.Length] + skinTones[i % skinTones.Length];
        }
    }

    [Benchmark]
    public void GraphemePoolLookup()
    {
        for (int i = 0; i < GraphemeCount; i++)
        {
            var chars = _multiCharGraphemes[i].AsSpan();
            var grapheme = GraphemePool.GetOrAdd(chars);
            _ = grapheme.Length;
        }
    }

    // --- Hyperlink benchmarks ---
    private TerminalBuffer _buffer = null!;
    private string[] _uris = null!;

    [Params(100, 500)]
    public int UriCount { get; set; }

    [GlobalSetup(Target = nameof(HyperlinkLookup))]
    public void HyperlinkSetup()
    {
        _buffer = new TerminalBuffer(24, 80);
        _uris = new string[UriCount];
        for (int i = 0; i < UriCount; i++)
        {
            _uris[i] = $"https://example.com/page/{i}/path";
        }
        // Warm up - add all once
        foreach (var uri in _uris)
        {
            _buffer.GetOrCreateHyperlinkId(uri);
        }
    }

    [Benchmark]
    public void HyperlinkLookup()
    {
        for (int i = 0; i < UriCount; i++)
        {
            _ = _buffer.GetOrCreateHyperlinkId(_uris[i]);
        }
    }

    // --- Rendering benchmark ---
    private TerminalAdapter _adapter = null!;
    private TerminalFrameComposer _composer = null!;
    private SKPaint _paint = null!;
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;

    [GlobalSetup]
    public void RenderSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _composer = new TerminalFrameComposer();

        _paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            IsAntialias = true
        };

        // Fill with ASCII content - optimization benefits this
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

    [Benchmark]
    public void RenderFrame()
    {
        var buffer = _adapter.Buffer!;
        _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, 0, buffer.Rows - 1);
    }

    // --- Full stack with heavy graphemes ---
    private BasicAnsiParser _parser = null!;
    private byte[] _emojiPayload = null!;

    [GlobalSetup(Target = nameof(ParseEmojiContent))]
    public void EmojiSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };

        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            sb.Append("👨‍👩‍👧‍👦 ");
        }
        _emojiPayload = Encoding.UTF8.GetBytes(sb.ToString());
    }

    [IterationSetup(Target = nameof(ParseEmojiContent))]
    public void EmojiIterationSetup()
    {
        _adapter.OnClearScrollback();
        _adapter.OnEraseDisplay(2);
        _adapter.OnMoveCursor(1, 1);
    }

    [Benchmark]
    public void ParseEmojiContent()
    {
        _parser.Feed(_emojiPayload);
    }
}
