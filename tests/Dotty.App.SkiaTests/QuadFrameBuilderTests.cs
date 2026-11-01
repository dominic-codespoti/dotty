using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class QuadFrameBuilderTests
{
    private const float CellW = 10f;
    private const float CellH = 20f;
    private const float TextSize = 14f;
    // 10 × 20 cell: underline at row 17, strikethrough at 10, overline at 0, all 1 px.
    private static readonly CellSpriteMetrics Sprites = CellSpriteMetrics.Create(10, 20, 15, 17, 1, 10, 1, 0, 1);
    private static readonly TextAttributeOptions SpriteOptions = new(0.5f, BlinkVisible: true, Sprites: Sprites);

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

    [Fact]
    public void GlyphPlacement_MatchesAtlasBearingsAndBaselineOffset()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 10);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Ag".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(2, result.InstanceCount);

        string[] graphemes = ["A", "g"];
        for (int i = 0; i < result.InstanceCount; i++)
        {
            var inst = result.Instances[i];
            var key = new GlyphKey(graphemes[i], SKTypeface.Default, TextSize, GlyphStyle.Regular);
            Assert.True(atlas.TryGetGlyph(key, out var info));

            Assert.Equal(QuadFrameBuilder.RoundPixelOffset(info.BaselineOffset + info.TopBearing), inst.OffY);
            Assert.Equal((short)info.LeftBearing, inst.OffX);
        }
    }

    [Fact]
    public void StyledWhitespace_EmitsBackgroundCellInstances()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 10);
        var expectedFg = SgrColorArgb.FromRgb(255, 255, 255);
        var expectedBg = SgrColorArgb.FromRgb(50, 100, 150);
        var attrs = new CellAttributes
        {
            Foreground = expectedFg,
            Background = expectedBg
        };

        buffer.SetCursor(0, 0);
        buffer.WriteText("\uE0B0 A".AsSpan(), attrs);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo);

        Assert.Equal(3, result.InstanceCount);

        for (int i = 0; i < result.InstanceCount; i++)
        {
            var inst = result.Instances[i];
            Assert.Equal((ushort)i, inst.Col);
            Assert.Equal((ushort)0, inst.Row);
            Assert.Equal(expectedBg.R, inst.BgR);
            Assert.Equal(expectedBg.G, inst.BgG);
            Assert.Equal(expectedBg.B, inst.BgB);
            Assert.Equal(255, inst.BgA);
        }

        var spaceInst = result.Instances[1];
        Assert.Equal((ushort)1, spaceInst.Col);
        Assert.Equal(expectedFg.R, spaceInst.FgR);
        Assert.Equal(expectedFg.G, spaceInst.FgG);
        Assert.Equal(expectedFg.B, spaceInst.FgB);
    }

    [Fact]
    public void UnderlinedWhitespaceWithoutBackground_EmitsADecorationInstanceAndPlainWhitespaceStaysSkipped()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 10);
        var underlined = new CellAttributes { UnderlineStyle = UnderlineStyle.Single };
        buffer.SetCursor(0, 0);
        buffer.WriteText("A B".AsSpan(), underlined);
        buffer.SetCursor(1, 0);
        buffer.WriteText("A B".AsSpan(), CellAttributes.Default);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 2, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo, textOptions: SpriteOptions);

        // Row 0: A + underline, space underline only, B + underline; row 1: A, B.
        Assert.Equal(7, result.InstanceCount);
        var space = result.Instances[2];
        Assert.Equal(((ushort)1, (ushort)0), (space.Col, space.Row));
        Assert.Equal(CellFlags.Decoration, space.Flags);
        Assert.Equal(((short)10, (short)1, (short)17), (space.GlyphW, space.GlyphH, space.OffY));
        Assert.Equal(0, space.BgA);
        Assert.Equal(((ushort)0, (ushort)1), (result.Instances[5].Col, result.Instances[5].Row));
        Assert.Equal(((ushort)2, (ushort)1), (result.Instances[6].Col, result.Instances[6].Row));
        Assert.Equal(1, atlas.SpriteCount);
    }

    [Fact]
    public void PlaceholderCell_NeverDrawsAGlyphButKeepsItsDecoration()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 10);
        var underlined = new CellAttributes { UnderlineStyle = UnderlineStyle.Single };
        buffer.SetCursor(0, 0);
        buffer.WriteText(char.ConvertFromUtf32(0x10EEEE).AsSpan(), underlined);

        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var geo = new FrameGeometry(CellW, CellH, 1, 10);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, geo, textOptions: SpriteOptions);

        var cell = Assert.Single(result.Instances.AsSpan(0, result.InstanceCount).ToArray());
        Assert.Equal(CellFlags.Decoration, cell.Flags);
        Assert.Equal((short)10, cell.GlyphW);
        Assert.Equal(0, cell.BgA);
    }

    [Theory]
    // inverse, expected fg (75 % of the visible foreground, 25 % of the visible background), expected bg
    [InlineData(false, 150, 0, 25, 0, 0, 100)]
    [InlineData(true, 50, 0, 75, 200, 0, 0)]
    public void Faint_MixesTheVisibleForegroundTowardTheVisibleBackground(
        bool inverse, int fgR, int fgG, int fgB, int bgR, int bgG, int bgB)
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        var attrs = new CellAttributes
        {
            Foreground = SgrColorArgb.FromRgb(200, 0, 0),
            Background = SgrColorArgb.FromRgb(0, 0, 100),
            Faint = true,
            Inverse = inverse,
        };
        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), attrs);
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 5),
            textOptions: new TextAttributeOptions(0.75f, BlinkVisible: true));

        var inst = Assert.Single(result.Instances.AsSpan(0, result.InstanceCount).ToArray());
        Assert.Equal((fgR, fgG, fgB), ((int)inst.FgR, (int)inst.FgG, (int)inst.FgB));
        Assert.Equal((bgR, bgG, bgB), ((int)inst.BgR, (int)inst.BgG, (int)inst.BgB));
        Assert.True(inst.GlyphW > 0);
    }

    [Fact]
    public void Conceal_DropsTheGlyphButKeepsBackgroundAndDecorations()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        var attrs = new CellAttributes
        {
            Background = SgrColorArgb.FromRgb(10, 20, 30),
            Invisible = true,
            UnderlineStyle = UnderlineStyle.Single,
        };
        buffer.SetCursor(0, 0);
        buffer.WriteText("AB".AsSpan(), attrs);
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 5),
            textOptions: SpriteOptions);

        Assert.Equal(4, result.InstanceCount);
        for (int i = 0; i < result.InstanceCount; i += 2)
        {
            var background = result.Instances[i];
            Assert.Equal((short)0, background.GlyphW);
            Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), (background.BgR, background.BgG, background.BgB, background.BgA));
            var underline = result.Instances[i + 1];
            Assert.Equal(CellFlags.Decoration, underline.Flags);
            Assert.Equal(background.Col, underline.Col);
        }
        Assert.False(atlas.TryGetGlyph(new GlyphKey("A", SKTypeface.Default, TextSize, GlyphStyle.Regular), out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Blink_DrawsTheGlyphOnlyInTheOnPhaseAndReportsTheRow(bool blinkVisible)
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5);
        buffer.SetCursor(0, 0);
        buffer.WriteText("A".AsSpan(), new CellAttributes { SlowBlink = true, Background = SgrColorArgb.FromRgb(1, 2, 3) });
        buffer.SetCursor(1, 0);
        buffer.WriteText("B".AsSpan(), CellAttributes.Default);
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var destination = new CellInstance[5];
        var options = new TextAttributeOptions(0.5f, blinkVisible);

        int blinkRow = QuadFrameBuilder.BuildRow(buffer, 0, atlas, SKTypeface.Default, TextSize, destination, null, 5,
            QuadFrameBuilder.DefaultForeground, QuadFrameBuilder.DefaultBackground, in options, out bool blinkRowHasBlink);
        CellInstance blinkCell = destination[0];
        int plainRow = QuadFrameBuilder.BuildRow(buffer, 1, atlas, SKTypeface.Default, TextSize, destination, null, 5,
            QuadFrameBuilder.DefaultForeground, QuadFrameBuilder.DefaultBackground, in options, out bool plainRowHasBlink);

        Assert.Equal(1, blinkRow);
        Assert.Equal(1, plainRow);
        Assert.True(blinkRowHasBlink);
        Assert.False(plainRowHasBlink);
        Assert.Equal(blinkVisible, blinkCell.GlyphW > 0);
        Assert.Equal((byte)255, blinkCell.BgA);
    }

    [Theory]
    [InlineData(false, false, GlyphStyle.Regular)]
    [InlineData(true, false, GlyphStyle.Bold)]
    [InlineData(false, true, GlyphStyle.Italic)]
    [InlineData(true, true, GlyphStyle.BoldItalic)]
    public void BoldAndItalic_SelectTheMatchingGlyphStyle(bool bold, bool italic, GlyphStyle expected)
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        buffer.SetCursor(0, 0);
        buffer.WriteText("W".AsSpan(), new CellAttributes { Bold = bold, Italic = italic });
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 5));

        foreach (GlyphStyle style in new[] { GlyphStyle.Regular, GlyphStyle.Bold, GlyphStyle.Italic, GlyphStyle.BoldItalic })
            Assert.Equal(style == expected, atlas.TryGetGlyph(new GlyphKey("W", SKTypeface.Default, TextSize, style), out _));
    }

    [Fact]
    public void Decorations_UseTheUnderlineColourAndTheTextColourInTheirOwnInstances()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        var attrs = new CellAttributes
        {
            Foreground = SgrColorArgb.FromRgb(200, 0, 0),
            UnderlineColor = SgrColorArgb.FromRgb(0, 200, 0),
            UnderlineStyle = UnderlineStyle.Curl,
            Strikethrough = true,
            Overline = true,
        };
        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), attrs);
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 4),
            textOptions: SpriteOptions);

        Assert.Equal(QuadFrameBuilder.MaxInstancesPerCell, result.InstanceCount);
        var (glyph, underline, lines) = (result.Instances[0], result.Instances[1], result.Instances[2]);
        Assert.Equal(0, glyph.Flags & CellFlags.Decoration);
        Assert.Equal((200, 0, 0), ((int)glyph.FgR, (int)glyph.FgG, (int)glyph.FgB));
        Assert.Equal((0, 200, 0), ((int)underline.FgR, (int)underline.FgG, (int)underline.FgB));
        Assert.Equal((200, 0, 0), ((int)lines.FgR, (int)lines.FgG, (int)lines.FgB));
        var (curlTop, curlHeight) = DecorationGeometry.UnderlineBand(UnderlineStyle.Curl, Sprites);
        Assert.Equal(((short)curlTop, (short)curlHeight), (underline.OffY, underline.GlyphH));
        // Overline (row 0) through strikethrough (row 10).
        Assert.Equal(((short)0, (short)11), (lines.OffY, lines.GlyphH));
        Assert.Equal(2, atlas.SpriteCount);
    }

    [Fact]
    public void WideCell_DecorationSpansBothCells()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("你".AsSpan(), new CellAttributes { UnderlineStyle = UnderlineStyle.Dashed });
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 4),
            textOptions: SpriteOptions);

        Assert.Equal(2, result.InstanceCount);
        Assert.Equal((short)20, result.Instances[1].GlyphW);
    }

    [Fact]
    public void UnmeasuredMetrics_EmitNoDecorations()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("A".AsSpan(), new CellAttributes { UnderlineStyle = UnderlineStyle.Single, Strikethrough = true });
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(CellW, CellH, 1, 4));

        Assert.Equal(1, result.InstanceCount);
        Assert.Equal(0, atlas.SpriteCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 7)]
    [InlineData(5, 40)]
    public void FullyDecoratedGrid_FitsTheInstanceBudgetExactly(int rows, int columns)
    {
        var buffer = new TerminalBuffer(rows, columns);
        var attrs = new CellAttributes
        {
            Background = SgrColorArgb.FromRgb(1, 2, 3),
            UnderlineColor = SgrColorArgb.FromRgb(0, 200, 0),
            UnderlineStyle = UnderlineStyle.Double,
            Strikethrough = true,
        };
        for (int row = 0; row < rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText(new string('x', columns).AsSpan(), attrs);
        }
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);

        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize,
            new FrameGeometry(CellW, CellH, rows, columns), textOptions: SpriteOptions);

        Assert.Equal(QuadFrameBuilder.InstanceBudget(rows, columns), result.Instances.Length);
        Assert.Equal(result.Instances.Length, result.InstanceCount);
    }

    [Fact]
    public void BuildRow_NeverWritesPastASmallDestination()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("ABCD".AsSpan(), new CellAttributes { UnderlineStyle = UnderlineStyle.Single, Strikethrough = true });
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var destination = new CellInstance[5];

        int written = QuadFrameBuilder.BuildRow(buffer, 0, atlas, SKTypeface.Default, TextSize, destination, null, 4,
            QuadFrameBuilder.DefaultForeground, QuadFrameBuilder.DefaultBackground, in SpriteOptions, out _);

        Assert.Equal(5, written);
    }
}
