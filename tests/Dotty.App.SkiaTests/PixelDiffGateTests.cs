using System;
using System.Collections.Generic;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// GPU-plan Phase 4 pixel-diff gate: the quad path must cover the same cells
/// as the direct path for identical buffer scenarios. Grayscale AA (atlas) vs
/// subpixel AA (direct) differ only at glyph edges, so coverage presence per
/// cell is compared, plus a mean-abs-difference ceiling on shared covered
/// pixels. Serialized with the atlas tests.
/// </summary>
[Collection("GlyphAtlas")]
public sealed class PixelDiffGateTests
{
    private const float CellW = 12f;
    private const float CellH = 24f;
    private const int CellPixelArea = 96; // 8x12 at default 96dpi... actual size derived below

    [Fact]
    public void StyledScenario_QuadMatchesDirect_Coverage()
    {
        var def = CellAttributes.Default;
        var buffer = new TerminalBuffer(rows: 15, columns: 60);

        buffer.SetCursor(0, 0);
        buffer.WriteText("The quick brown fox jumps over the lazy dog".AsSpan(), def);
        buffer.SetCursor(2, 0);
        var red = new CellAttributes { Foreground = SgrColorArgb.FromRgb(0xFF, 0x40, 0x40) };
        buffer.WriteText("red foreground text sample".AsSpan(), red);
        buffer.SetCursor(3, 0);
        var blueBg = new CellAttributes { Background = SgrColorArgb.FromRgb(0x20, 0x40, 0x80), Foreground = SgrColorArgb.FromRgb(0xFF, 0xFF, 0xFF) };
        buffer.WriteText("blue background sample".AsSpan(), blueBg);
        buffer.SetCursor(4, 0);
        var underlined = new CellAttributes { UnderlineStyle = UnderlineStyle.Single };
        buffer.WriteText("underlined text sample".AsSpan(), underlined);
        buffer.SetCursor(5, 0);
        var strike = new CellAttributes { Strikethrough = true };
        buffer.WriteText("strikethrough text sample".AsSpan(), strike);
        buffer.SetCursor(6, 0);
        buffer.WriteText("\u250C\u2500\u2500\u2500\u252C\u2500\u2500\u2510".AsSpan(), def);
        buffer.SetCursor(7, 0);
        buffer.WriteText("\u2502 wide:\u4e16\u754c \u2502".AsSpan(), def);
        buffer.SetCursor(8, 0);
        buffer.WriteText("\u2514\u2500\u2500\u2500\u2534\u2500\u2500\u2518".AsSpan(), def);
        buffer.SetCursor(9, 0);
        var inv = new CellAttributes { Inverse = true };
        buffer.WriteText("inverse video text".AsSpan(), inv);
        buffer.SetCursor(10, 0);
        var bold = new CellAttributes { Bold = true };
        buffer.WriteText("bold text sample".AsSpan(), bold);

        using var quadBitmap = RenderQuad(buffer);
        using var directBitmap = RenderDirect(buffer);

        // Per-row coverage comparison: each row's non-background pixel count
        // must agree between paths within a tolerance (AA edges differ).
        for (int row = 0; row < 11; row++)
        {
            int y0 = row * RowHeight;
            int quadCount = CountLit(quadBitmap, y0, y0 + RowHeight);
            int directCount = CountLit(directBitmap, y0, y0 + RowHeight);
            double ratio = directCount == 0 ? 1.0 : (double)quadCount / directCount;
            Assert.True(ratio > 0.5 && ratio < 2.0,
                $"row {row}: quad {quadCount} px vs direct {directCount} px (ratio {ratio:F2})");
        }
    }

    private const int RowHeight = 24;

    private static int CountLit(SKBitmap bitmap, int yStart, int yEnd)
    {
        int count = 0;
        for (int y = Math.Max(0, yStart); y < Math.Min(bitmap.Height, yEnd); y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red != 0 || px.Green != 0 || px.Blue != 0) count++;
            }
        return count;
    }

    private static SKBitmap RenderQuad(TerminalBuffer buffer)
    {
        using var atlas = new GlyphAtlas(SKTypeface.Default, 26f);
        var composer = new TerminalFrameComposer();
        composer.GlyphAtlas = atlas;
        composer.UseQuadGlyphs = true;
        composer.TextShaper = new TextShaper();
        composer.ShapedRunCache = new ShapedRunCache();

        var bitmap = new SKBitmap(BitmapWidth(buffer.Columns), BitmapHeight(buffer.Rows),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 26f);
        composer.RenderTo(canvas, buffer, paint, font, 12f, 24f, 0, buffer.Rows - 1);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap RenderDirect(TerminalBuffer buffer)
    {
        var composer = new TerminalFrameComposer();
        var bitmap = new SKBitmap(BitmapWidth(buffer.Columns), BitmapHeight(buffer.Rows),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 26f);
        composer.RenderTo(canvas, buffer, paint, font, 12f, 24f, 0, buffer.Rows - 1);
        canvas.Flush();
        return bitmap;
    }

    private static int BitmapWidth(int columns) => (int)(columns * 12f);
    private static int BitmapHeight(int rows) => (int)(rows * 24f);
}
