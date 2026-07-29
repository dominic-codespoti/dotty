using System;
using BenchmarkDotNet.Attributes;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace PerfHarness;

[MemoryDiagnoser]
public class GuiRenderBenchmarks
{
    private TerminalAdapter _adapter = null!;
    private TerminalFrameComposer _composer = null!;
    private SKPaint _paint = null!;
    private SKFont _font = null!;
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;

    private const float CellWidth = 9f;
    private const float CellHeight = 18f;
    private const int ScrollbackVisibleLines = 24;

    [GlobalSetup]
    public void Setup()
    {
        _adapter = new TerminalAdapter(24, 80);
        _composer = new TerminalFrameComposer();

        _paint = new SKPaint
        {
            IsAntialias = true,
        };
        _font = new SKFont(SKTypeface.Default, 14f)
        {
            Edging = SKFontEdging.SubpixelAntialias,
            Subpixel = true,
            Hinting = SKFontHinting.Full
        };

        SeedActiveViewport(_adapter.Buffer!);
        SeedScrollback(_adapter.Buffer!);

        _bitmap = new SKBitmap(1600, 900, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _canvas.Clear(SKColors.Black);
    }

    [Benchmark(Baseline = true)]
    public void RenderActiveViewport()
    {
        var buffer = _adapter.Buffer!;
        int endRow = buffer.Rows - 1;
        int startRow = Math.Max(0, endRow - ScrollbackVisibleLines + 1);
        _composer.RenderTo(_canvas, buffer, _paint, _font, CellWidth, CellHeight, startRow, endRow);
    }

    [Benchmark]
    public void RenderVisibleScrollbackSlice()
    {
        var buffer = _adapter.Buffer!;
        int sbCount = buffer.ScrollbackCount;
        int start = Math.Max(0, sbCount - ScrollbackVisibleLines);
        var fm = _font.Metrics;
        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
        float baselineOffset = (CellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);

        for (int i = start; i < sbCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            if (line.Length <= 0) continue;

            string text = line.Text ?? string.Empty;
            float y = ((i - start) * CellHeight) + baselineOffset;
            _canvas.DrawText(SKTextBlob.Create(text, _font), 0, y, _paint);
        }
    }

    [Benchmark]
    public void RenderCombinedGuiFrame()
    {
        RenderVisibleScrollbackSlice();
        RenderActiveViewport();
    }

    private static void SeedActiveViewport(TerminalBuffer buffer)
    {
        buffer.ClearScreen();
        var line = new string('y', buffer.Columns);

        for (int i = 0; i < buffer.Rows; i++)
        {
            buffer.WriteText(line.AsSpan(), CellAttributes.Default);
            if (i < buffer.Rows - 1)
            {
                buffer.LineFeed();
                buffer.CarriageReturn();
            }
        }
    }

    private static void SeedScrollback(TerminalBuffer buffer)
    {
        var line = new string('y', buffer.Columns);
        int targetLines = buffer.MaxScrollback;
        for (int i = 0; i < targetLines + buffer.Rows; i++)
        {
            buffer.CarriageReturn();
            buffer.WriteText(line.AsSpan(), CellAttributes.Default);
            buffer.CarriageReturn();
            buffer.LineFeed();
        }
    }
}
