using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using SkiaSharp;

namespace PerfHarness;

[MemoryDiagnoser]
public class ChunkedUpdateBenchmarks
{
    private BasicAnsiParser _parser = null!;
    private TerminalAdapter _adapter = null!;
    private TerminalFrameComposer _composer = null!;
    private SKPaint _paint = null!;
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;
    private byte[] _payload = null!;

    [Params(2, 128, 1024, 4096)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser = new BasicAnsiParser { Handler = _adapter };
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

        var sb = new StringBuilder();
        for (int i = 0; i < 500000; i++)
        {
            sb.Append('y').Append('\n');
        }

        _payload = Encoding.UTF8.GetBytes(sb.ToString());
        _bitmap = new SKBitmap(1600, 900, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _parser.Handler = _adapter;
        _canvas.Clear(SKColors.Black);
    }

    [Benchmark]
    public void ParseAndRenderChunked()
    {
        var buffer = _adapter.Buffer!;
        int visibleRows = Math.Min(24, buffer.Rows);
        int endRow = buffer.Rows - 1;
        int startRow = Math.Max(0, endRow - visibleRows + 1);

        for (int offset = 0; offset < _payload.Length; offset += ChunkSize)
        {
            int len = Math.Min(ChunkSize, _payload.Length - offset);
            _parser.Feed(_payload.AsSpan(offset, len));

            _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, startRow, endRow);
        }
    }

    [Benchmark]
    public void ParseAndRenderChunkedBatched()
    {
        var buffer = _adapter.Buffer!;
        int visibleRows = Math.Min(24, buffer.Rows);
        int endRow = buffer.Rows - 1;
        int startRow = Math.Max(0, endRow - visibleRows + 1);

        Span<byte> batch = stackalloc byte[65536];
        int batchLength = 0;

        for (int offset = 0; offset < _payload.Length; offset += ChunkSize)
        {
            int len = Math.Min(ChunkSize, _payload.Length - offset);

            if (batchLength + len > batch.Length)
            {
                _parser.Feed(batch.Slice(0, batchLength));
                _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, startRow, endRow);
                batchLength = 0;
            }

            _payload.AsSpan(offset, len).CopyTo(batch.Slice(batchLength));
            batchLength += len;

            if (batchLength >= 32768)
            {
                _parser.Feed(batch.Slice(0, batchLength));
                _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, startRow, endRow);
                batchLength = 0;
            }
        }

        if (batchLength > 0)
        {
            _parser.Feed(batch.Slice(0, batchLength));
            _composer.RenderTo(_canvas, buffer, _paint, 9f, 18f, startRow, endRow);
        }
    }
}
