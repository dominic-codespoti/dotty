using System;
using System.Reflection;
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
    private SKBitmap _bitmap = null!;
    private SKCanvas _canvas = null!;

    private const float CellWidth = 9f;
    private const float CellHeight = 18f;
    private const int ScrollbackVisibleLines = 24;
    private const int SimulatedScrollbackCount = 500000;

    private static readonly FieldInfo ScrollbackRingField = typeof(TerminalBuffer).GetField("_scrollbackRing", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ScrollbackHeadField = typeof(TerminalBuffer).GetField("_scrollbackHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ScrollbackCountField = typeof(TerminalBuffer).GetField("_scrollbackCount", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [GlobalSetup]
    public void Setup()
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
        _composer.RenderTo(_canvas, buffer, _paint, CellWidth, CellHeight, startRow, endRow);
    }

    [Benchmark]
    public void RenderVisibleScrollbackSlice()
    {
        var buffer = _adapter.Buffer!;
        int sbCount = buffer.ScrollbackCount;
        int start = Math.Max(0, sbCount - ScrollbackVisibleLines);
        var fm = _paint.FontMetrics;
        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
        float baselineOffset = (CellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);

        for (int i = start; i < sbCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            if (line.Length <= 0) continue;

            string text = new string(line.Buffer, 0, line.Length);
            float y = ((i - start) * CellHeight) + baselineOffset;
            _canvas.DrawText(text, 0, y, _paint);
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
        var sharedLine = new string('y', buffer.Columns).ToCharArray();
        var ring = new TerminalBuffer.ScrollbackLine[SimulatedScrollbackCount];

        for (int i = 0; i < ring.Length; i++)
        {
            ring[i] = new TerminalBuffer.ScrollbackLine(sharedLine, sharedLine.Length);
        }

        ScrollbackRingField.SetValue(buffer, ring);
        ScrollbackHeadField.SetValue(buffer, 0);
        ScrollbackCountField.SetValue(buffer, SimulatedScrollbackCount);
    }
}
