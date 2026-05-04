using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using System.Reflection;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalFrameComposerRenderingTests
{
    [Fact]
    public void RenderTo_SyncGlyphPaint_PropagatesRasterizationFlags()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("A".AsSpan(), CellAttributes.Default);

        using var bitmap = new SKBitmap(24, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 18f,
            Color = SKColors.White,
            IsAntialias = true,
            IsLinearText = true,
            SubpixelText = true,
            LcdRenderText = true,
            IsAutohinted = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: 24f, cellH: 24f, startRow: 0, endRow: 0);

        var glyphPaintField = typeof(TerminalFrameComposer).GetField("_glyphPaint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(glyphPaintField);

        var glyphPaint = glyphPaintField!.GetValue(composer) as SKPaint;
        Assert.NotNull(glyphPaint);

        Assert.Equal(paint.IsAntialias, glyphPaint!.IsAntialias);
        Assert.Equal(paint.IsLinearText, glyphPaint.IsLinearText);
        Assert.Equal(paint.SubpixelText, glyphPaint.SubpixelText);
        Assert.Equal(paint.LcdRenderText, glyphPaint.LcdRenderText);
        Assert.Equal(paint.IsAutohinted, glyphPaint.IsAutohinted);
    }

    [Fact]
    public void RenderTo_ClipsGlyphDrawingToRowBounds()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 2, columns: 2);
        buffer.SetCursor(0, 0);
        buffer.WriteText("W".AsSpan(), CellAttributes.Default);

        const float cellW = 24f;
        const float cellH = 30f;

        using var bitmap = new SKBitmap(48, 60, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 26f,
            Color = SKColors.White,
            IsAntialias = true,
            LcdRenderText = true,
            SubpixelText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW, cellH, startRow: 0, endRow: 0);

        int insideRowPixels = 0;
        int outsideRowPixels = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool isDrawn = px.Red != 0 || px.Green != 0 || px.Blue != 0 || px.Alpha != 255;
                if (!isDrawn) continue;

                if (y >= 0 && y < 30)
                {
                    insideRowPixels++;
                }
                else
                {
                    outsideRowPixels++;
                }
            }
        }

        Assert.True(insideRowPixels > 0, "Expected glyph pixels inside rendered row.");
        Assert.Equal(0, outsideRowPixels);
    }

    [Fact]
    public void RenderTo_UsesFontAscentBaselineWithoutRowCenteringGap()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("█".AsSpan(), CellAttributes.Default);

        const float cellW = 30f;
        const float cellH = 30f;

        using var bitmap = new SKBitmap(30, 30, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 16f,
            Color = SKColors.White,
            IsAntialias = false,
            LcdRenderText = false,
            SubpixelText = false
        };

        composer.RenderTo(canvas, buffer, paint, cellW, cellH, startRow: 0, endRow: 0);

        int topDrawnY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            bool rowHasInk = false;
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                {
                    rowHasInk = true;
                    break;
                }
            }

            if (!rowHasInk) continue;
            if (topDrawnY == -1) topDrawnY = y;
        }

        Assert.True(topDrawnY >= 0, "Expected block glyph to draw pixels.");

        var fm = paint.FontMetrics;
        var bounds = new SKRect();
        paint.MeasureText("█", ref bounds);
        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);

        float ascentBaselineTop = (-fm.Ascent) + bounds.Top;
        float centeredBaseline = (cellH * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
        float centeredBaselineTop = centeredBaseline + bounds.Top;

        float deltaAscent = Math.Abs(topDrawnY - ascentBaselineTop);
        float deltaCentered = Math.Abs(topDrawnY - centeredBaselineTop);

        Assert.True(
            deltaAscent + 0.5f < deltaCentered,
            $"Expected top ink row ({topDrawnY}) to match ascent baseline ({ascentBaselineTop:F2}) more than centered baseline ({centeredBaselineTop:F2}).");
    }

    [Fact]
    public void RenderTo_DisablesSmoothingForPixelGridGlyphs()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("█".AsSpan(), CellAttributes.Default);

        using var bitmap = new SKBitmap(24, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 18f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: 24f, cellH: 24f, startRow: 0, endRow: 0);

        var glyphPaintField = typeof(TerminalFrameComposer).GetField("_glyphPaint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(glyphPaintField);

        var glyphPaint = glyphPaintField!.GetValue(composer) as SKPaint;
        Assert.NotNull(glyphPaint);

        Assert.False(glyphPaint!.IsAntialias);
        Assert.False(glyphPaint.SubpixelText);
        Assert.False(glyphPaint.LcdRenderText);
    }

    [Fact]
    public void RenderTo_BlockGeometryFullBlock_FillsEveryScanlineWithoutStriping()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 2, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("█".AsSpan(), CellAttributes.Default);

        const int cell = 16;
        using var bitmap = new SKBitmap(cell, cell * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: cell, cellH: cell, startRow: 0, endRow: 1);

        for (int y = 0; y < cell; y++)
        {
            int rowInk = 0;
            for (int x = 0; x < cell; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                {
                    rowInk++;
                }
            }

            Assert.Equal(cell, rowInk);
        }

        int outsideInk = 0;
        for (int y = cell; y < cell * 2; y++)
        {
            for (int x = 0; x < cell; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                {
                    outsideInk++;
                }
            }
        }

        Assert.Equal(0, outsideInk);
    }

    [Fact]
    public void RenderTo_BlockGeometryLowerHalfBlock_FillsExpectedCoverage()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("▄".AsSpan(), CellAttributes.Default);

        const int cell = 16;
        using var bitmap = new SKBitmap(cell, cell, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

        int topHalfInk = 0;
        int bottomHalfInk = 0;
        for (int y = 0; y < cell; y++)
        {
            for (int x = 0; x < cell; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool ink = px.Red > 0 || px.Green > 0 || px.Blue > 0;
                if (!ink) continue;

                if (y < cell / 2)
                {
                    topHalfInk++;
                }
                else
                {
                    bottomHalfInk++;
                }
            }
        }

        Assert.Equal(0, topHalfInk);
        Assert.Equal(cell * (cell / 2), bottomHalfInk);
    }

    [Fact]
    public void RenderTo_BoxDrawingHorizontal_UsesDeterministicGeometryBand()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("─".AsSpan(), CellAttributes.Default);

        const int cell = 16;
        using var bitmap = new SKBitmap(cell, cell, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

        int rowsWithAnyInk = 0;
        int fullInkRows = 0;
        int bandCenter = cell / 2;

        for (int y = 0; y < cell; y++)
        {
            int rowInk = 0;
            for (int x = 0; x < cell; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                {
                    rowInk++;
                }
            }

            if (rowInk > 0) rowsWithAnyInk++;
            if (rowInk == cell) fullInkRows++;

            if (rowInk > 0)
            {
                Assert.InRange(y, bandCenter - 2, bandCenter + 2);
            }
        }

        Assert.True(rowsWithAnyInk > 0, "Expected horizontal box-drawing glyph to render ink.");
        Assert.True(fullInkRows > 0, "Expected at least one fully covered scanline from geometry rendering.");
    }

    [Fact]
    public void RenderTo_BoxDrawingVertical_UsesDeterministicGeometryBand()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText("│".AsSpan(), CellAttributes.Default);

        const int cell = 16;
        using var bitmap = new SKBitmap(cell, cell, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

        int colsWithAnyInk = 0;
        int fullInkCols = 0;
        int bandCenter = cell / 2;

        for (int x = 0; x < cell; x++)
        {
            int colInk = 0;
            for (int y = 0; y < cell; y++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 0 || px.Green > 0 || px.Blue > 0)
                {
                    colInk++;
                }
            }

            if (colInk > 0) colsWithAnyInk++;
            if (colInk == cell) fullInkCols++;

            if (colInk > 0)
            {
                Assert.InRange(x, bandCenter - 2, bandCenter + 2);
            }
        }

        Assert.True(colsWithAnyInk > 0, "Expected vertical box-drawing glyph to render ink.");
        Assert.True(fullInkCols > 0, "Expected at least one fully covered column from geometry rendering.");
    }

    [Theory]
    [InlineData("┌")]
    [InlineData("┐")]
    [InlineData("└")]
    [InlineData("┘")]
    [InlineData("├")]
    [InlineData("┤")]
    [InlineData("┬")]
    [InlineData("┴")]
    [InlineData("┼")]
    [InlineData("╔")]
    [InlineData("╗")]
    [InlineData("╚")]
    [InlineData("╝")]
    [InlineData("╠")]
    [InlineData("╣")]
    [InlineData("╦")]
    [InlineData("╩")]
    [InlineData("╬")]
    public void RenderTo_BoxDrawingCornersAndJunctions_AvoidsTopBandStriping(string glyph)
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 1);
        buffer.SetCursor(0, 0);
        buffer.WriteText(glyph.AsSpan(), CellAttributes.Default);

        const int cell = 16;
        using var bitmap = new SKBitmap(cell, cell, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Typeface = SKTypeface.Default,
            TextSize = 14f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            LcdRenderText = true
        };

        composer.RenderTo(canvas, buffer, paint, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

        int totalInk = 0;
        bool topQuarterHasFullWidthBand = false;
        for (int y = 0; y < cell; y++)
        {
            int rowInk = 0;
            for (int x = 0; x < cell; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool ink = px.Red > 0 || px.Green > 0 || px.Blue > 0;
                if (!ink) continue;

                totalInk++;
                rowInk++;
            }

            if (y < cell / 4 && rowInk == cell)
            {
                topQuarterHasFullWidthBand = true;
            }
        }

        Assert.True(totalInk > 0, $"Expected box-drawing glyph '{glyph}' to render ink.");
        Assert.False(topQuarterHasFullWidthBand, $"Glyph '{glyph}' rendered an unexpected full-width top band.");
    }
}
