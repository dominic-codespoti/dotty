using System;
using System.Collections.Generic;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class QuadFrameBuilderTests
{
    private const float CellW = 10f;
    private const float CellH = 20f;
    private const float TextSize = 14f;

    [Fact]
    public void AsciiText_ProducesCorrectNumberOfInstances()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 10);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Hello".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("World".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 2, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(10, result.InstanceCount);

        // Verify first row
        for (int i = 0; i < 5; i++)
        {
            var inst = result.Instances[i];
            Assert.Equal((ushort)i, inst.Col);
            Assert.Equal((ushort)0, inst.Row);
            Assert.True(inst.GlyphW > 0);
            Assert.True(inst.GlyphH > 0);
        }

        // Verify second row
        for (int i = 0; i < 5; i++)
        {
            var inst = result.Instances[5 + i];
            Assert.Equal((ushort)i, inst.Col);
            Assert.Equal((ushort)1, inst.Row);
            Assert.True(inst.GlyphW > 0);
            Assert.True(inst.GlyphH > 0);
        }
    }

    [Fact]
    public void WideCJKCharacters_ProduceCorrectAdvanceAndWideFlag()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 10);
        // "你好" - 2 CJK characters, each width 2 (takes cols 0-1 and 2-3)
        buffer.SetCursor(0, 0);
        buffer.WriteText("你好".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        // Should produce 2 instances (one per wide char, continuations skipped)
        Assert.Equal(2, result.InstanceCount);

        var first = result.Instances[0];
        Assert.Equal((ushort)0, first.Col);
        Assert.Equal((ushort)0, first.Row);
        Assert.True((first.Flags & CellFlags.WideCell) != 0);

        var second = result.Instances[1];
        Assert.Equal((ushort)2, second.Col);
        Assert.Equal((ushort)0, second.Row);
        Assert.True((second.Flags & CellFlags.WideCell) != 0);
    }

    [Fact]
    public void EmptyCells_AreSkipped()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 5);
        buffer.SetCursor(0, 1);
        buffer.WriteText("A".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(2, 3);
        buffer.WriteText("B".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 3, 5);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        // Only 'A' and 'B' should be emitted
        Assert.Equal(2, result.InstanceCount);

        var first = result.Instances[0];
        Assert.Equal((ushort)1, first.Col);
        Assert.Equal((ushort)0, first.Row);

        var second = result.Instances[1];
        Assert.Equal((ushort)3, second.Col);
        Assert.Equal((ushort)2, second.Row);
    }

    [Fact]
    public void Colors_AreCorrectlyResolvedFromStyles()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        var expectedFg = SgrColorArgb.FromRgb(255, 0, 0);
        var expectedBg = SgrColorArgb.FromRgb(0, 255, 0);
        var attrs = new CellAttributes
        {
            Foreground = expectedFg,
            Background = expectedBg
        };

        buffer.SetCursor(0, 0);
        buffer.WriteText("A".AsSpan(), attrs);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 5);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(1, result.InstanceCount);

        var inst = result.Instances[0];
        Assert.Equal(expectedFg.R, inst.FgR);
        Assert.Equal(expectedFg.G, inst.FgG);
        Assert.Equal(expectedFg.B, inst.FgB);

        Assert.Equal(expectedBg.R, inst.BgR);
        Assert.Equal(expectedBg.G, inst.BgG);
        Assert.Equal(expectedBg.B, inst.BgB);
        Assert.Equal(255, inst.BgA);
    }

    [Fact]
    public void BoldAndInverse_SetCorrectFlagsAndSwapColors()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        var fgColor = SgrColorArgb.FromRgb(0, 0, 255);
        var bgColor = SgrColorArgb.FromRgb(255, 255, 0);
        var attrs = new CellAttributes
        {
            Foreground = fgColor,
            Background = bgColor,
            Bold = true,
            Inverse = true
        };

        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), attrs);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 5);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(1, result.InstanceCount);

        var inst = result.Instances[0];
        Assert.True((inst.Flags & CellFlags.Bold) != 0);
        Assert.True((inst.Flags & CellFlags.InverseVideo) != 0);

        // Due to inverse, fg and bg are swapped
        Assert.Equal(bgColor.R, inst.FgR);
        Assert.Equal(bgColor.G, inst.FgG);
        Assert.Equal(bgColor.B, inst.FgB);

        Assert.Equal(fgColor.R, inst.BgR);
        Assert.Equal(fgColor.G, inst.BgG);
        Assert.Equal(fgColor.B, inst.BgB);
        Assert.Equal(255, inst.BgA);
    }

    [Fact]
    public void DirtyAtlasRows_TracksRowsWithNewlyAddedGlyphs()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 5);
        buffer.SetCursor(0, 0);
        buffer.WriteText("A".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(2, 0);
        buffer.WriteText("B".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 3, 5);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Contains(0, result.DirtyAtlasRows);
        Assert.DoesNotContain(1, result.DirtyAtlasRows);
        Assert.Contains(2, result.DirtyAtlasRows);

        // Subsequent build of the same buffer with warm atlas should report no dirty rows
        var result2 = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);
        Assert.Empty(result2.DirtyAtlasRows);
    }
}
