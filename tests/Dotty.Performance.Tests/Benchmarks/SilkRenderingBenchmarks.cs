using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using SkiaSharp;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// CPU-side frame construction and glyph atlas benchmarks for Silk/GPU terminal rendering.
/// Measures QuadFrameBuilder and GlyphAtlas workloads without requiring a live GLFW/OpenGL window or context.
/// </summary>
[BenchmarkCategory("SilkRendering")]
public class SilkRenderingBenchmarks : PerformanceTestBase
{
    private const float DefaultFontSize = 14f;
    private const float CellWidth = 8.4f;
    private const float CellHeight = 18f;

    private SKTypeface _typeface = null!;
    private GlyphAtlas _warmAtlas = null!;

    private TerminalBuffer _buffer80x24 = null!;
    private FrameGeometry _geometry80x24;

    private TerminalBuffer _buffer120x40 = null!;
    private FrameGeometry _geometry120x40;

    private TerminalBuffer _bufferPowerline = null!;
    private FrameGeometry _geometryPowerline;

    private CellInstance[] _pooledInstances80x24 = null!;
    private HashSet<int> _pooledDirtyRows = null!;

    public override void GlobalSetup()
    {
        base.GlobalSetup();

        _typeface = SKTypeface.Default;
        _warmAtlas = new GlyphAtlas(_typeface, DefaultFontSize);

        _geometry80x24 = new FrameGeometry(CellWidth, CellHeight, 24, 80);
        _buffer80x24 = CreateAsciiBuffer(24, 80);

        _geometry120x40 = new FrameGeometry(CellWidth, CellHeight, 40, 120);
        _buffer120x40 = CreateAsciiBuffer(40, 120);

        _geometryPowerline = new FrameGeometry(CellWidth, CellHeight, 24, 80);
        _bufferPowerline = CreatePowerlineBuffer(24, 80);

        _pooledInstances80x24 = new CellInstance[24 * 80];
        _pooledDirtyRows = new HashSet<int>();

        // Pre-warm the shared warm atlas outside measured methods with all test buffers
        PreWarmAtlas(_warmAtlas, _buffer80x24, _geometry80x24);
        PreWarmAtlas(_warmAtlas, _buffer120x40, _geometry120x40);
        PreWarmAtlas(_warmAtlas, _bufferPowerline, _geometryPowerline);
    }

    public override void GlobalCleanup()
    {
        _warmAtlas?.Dispose();

        base.GlobalCleanup();
    }

    private void PreWarmAtlas(GlyphAtlas atlas, TerminalBuffer buffer, FrameGeometry geometry)
    {
        var tempInstances = new CellInstance[geometry.Rows * geometry.Columns];
        QuadFrameBuilder.Build(
            buffer,
            atlas,
            _typeface,
            DefaultFontSize,
            tempInstances.AsSpan(),
            dirtyAtlasRows: null,
            maxRows: geometry.Rows,
            maxCols: geometry.Columns);
    }

    private static TerminalBuffer CreateAsciiBuffer(int rows, int cols)
    {
        var buffer = new TerminalBuffer(rows, cols);
        var sampleLines = new[]
        {
            "public static void Main(string[] args) => Console.WriteLine(\"Hello, Dotty!\");",
            "for (int i = 0; i < 100; i++) { ProcessItem(items[i], flags: 0x2A); }",
            "git commit -m \"feat: implement Silk GPU CPU frame builder pipeline\"",
            "var result = QuadFrameBuilder.Build(source, atlas, typeface, textSize, geometry);",
            "dotnet test --filter Category=SilkRendering -c Release --no-build",
            "const uint DefaultFgColor = 0xFFCCCCCCu; const uint DefaultBgColor = 0xFF1E1E1Eu;",
            "Checking buffer dirty line hashes and updating instance quad batches efficiently...",
            "1234567890 [] () {} <> + - * / = ! ? @ # $ % ^ & _ ~ | \\ : ; \" ' , . < >"
        };

        for (int r = 0; r < rows; r++)
        {
            string line = sampleLines[r % sampleLines.Length];
            if (line.Length > cols)
            {
                line = line.Substring(0, cols);
            }
            else if (line.Length < cols)
            {
                line = line.PadRight(cols, ' ');
            }

            buffer.WriteText(line.AsSpan(), CellAttributes.Default);
            if (r < rows - 1)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }

        return buffer;
    }

    private static TerminalBuffer CreatePowerlineBuffer(int rows, int cols)
    {
        var buffer = new TerminalBuffer(rows, cols);

        var fgBlue = SgrColorArgb.FromRgb(30, 100, 200);
        var bgDark = SgrColorArgb.FromRgb(30, 30, 30);
        var bgBlue = SgrColorArgb.FromRgb(30, 100, 200);
        var fgWhite = SgrColorArgb.FromRgb(255, 255, 255);
        var bgGreen = SgrColorArgb.FromRgb(40, 160, 60);
        var fgGreen = SgrColorArgb.FromRgb(40, 160, 60);
        var bgGray = SgrColorArgb.FromRgb(60, 60, 60);
        var fgGray = SgrColorArgb.FromRgb(60, 60, 60);

        var segment1Style = new CellAttributes { Foreground = fgWhite, Background = bgBlue, Bold = true };
        var arrow1Style = new CellAttributes { Foreground = fgBlue, Background = bgGreen };
        var segment2Style = new CellAttributes { Foreground = fgWhite, Background = bgGreen };
        var arrow2Style = new CellAttributes { Foreground = fgGreen, Background = bgGray };
        var segment3Style = new CellAttributes { Foreground = fgWhite, Background = bgGray };
        var arrow3Style = new CellAttributes { Foreground = fgGray, Background = bgDark };
        var remainderStyle = new CellAttributes { Foreground = fgWhite, Background = bgDark };

        for (int r = 0; r < rows; r++)
        {
            // Segment 1: " master " (with literal styled spaces)
            buffer.WriteText(" master ".AsSpan(), segment1Style);

            // Powerline right arrow separator: U+E0B0
            buffer.WriteText("\uE0B0".AsSpan(), arrow1Style);

            // Segment 2: " dotnet-term " (with literal styled spaces)
            buffer.WriteText(" dotnet-term ".AsSpan(), segment2Style);

            // Powerline right arrow separator: U+E0B0
            buffer.WriteText("\uE0B0".AsSpan(), arrow2Style);

            // Segment 3: " src/Dotty.Rendering.Gpu "
            buffer.WriteText(" src/Dotty.Rendering.Gpu ".AsSpan(), segment3Style);

            // Powerline right arrow separator: U+E0B0
            buffer.WriteText("\uE0B0".AsSpan(), arrow3Style);

            // Fill remainder with styled spaces
            int writtenCols = 8 + 1 + 13 + 1 + 25 + 1;
            int remaining = cols - writtenCols;
            if (remaining > 0)
            {
                string pad = new string(' ', remaining);
                buffer.WriteText(pad.AsSpan(), remainderStyle);
            }

            if (r < rows - 1)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }

        return buffer;
    }

    #region Benchmarks

    [Benchmark(Description = "Silk CPU warm-atlas frame build 80x24")]
    public int SilkCpu_WarmAtlas_FrameBuild_80x24()
    {
        var result = QuadFrameBuilder.Build(
            _buffer80x24,
            _warmAtlas,
            _typeface,
            DefaultFontSize,
            _geometry80x24);

        return result.InstanceCount;
    }

    [Benchmark(Description = "Silk CPU warm-atlas frame build 120x40")]
    public int SilkCpu_WarmAtlas_FrameBuild_120x40()
    {
        var result = QuadFrameBuilder.Build(
            _buffer120x40,
            _warmAtlas,
            _typeface,
            DefaultFontSize,
            _geometry120x40);

        return result.InstanceCount;
    }

    [Benchmark(Description = "Silk CPU pooled Span<CellInstance> build 80x24")]
    public int SilkCpu_PooledSpan_Build_80x24()
    {
        _pooledDirtyRows.Clear();

        return QuadFrameBuilder.Build(
            _buffer80x24,
            _warmAtlas,
            _typeface,
            DefaultFontSize,
            _pooledInstances80x24.AsSpan(),
            _pooledDirtyRows,
            _geometry80x24.Rows,
            _geometry80x24.Columns);
    }

    [Benchmark(Description = "Silk CPU cold-atlas frame build 80x24")]
    public int SilkCpu_ColdAtlas_FrameBuild_80x24()
    {
        using var coldAtlas = new GlyphAtlas(_typeface, DefaultFontSize);

        var result = QuadFrameBuilder.Build(
            _buffer80x24,
            coldAtlas,
            _typeface,
            DefaultFontSize,
            _geometry80x24);

        return result.InstanceCount;
    }

    [Benchmark(Description = "Silk CPU warm-atlas powerline styled frame build 80x24")]
    public int SilkCpu_WarmAtlas_PowerlineFrameBuild_80x24()
    {
        var result = QuadFrameBuilder.Build(
            _bufferPowerline,
            _warmAtlas,
            _typeface,
            DefaultFontSize,
            _geometryPowerline);

        return result.InstanceCount;
    }

    #endregion
}
