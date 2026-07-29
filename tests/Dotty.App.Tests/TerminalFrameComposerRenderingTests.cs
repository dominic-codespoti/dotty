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
            Color = SKColors.White,
            IsAntialias = true,
        };
        using var font = new SKFont(SKTypeface.Default, 18f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: 24f, cellH: 24f, startRow: 0, endRow: 0);

        var glyphPaintField = typeof(TerminalFrameComposer).GetField("_glyphPaint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(glyphPaintField);

        var glyphPaint = glyphPaintField!.GetValue(composer) as SKPaint;
        Assert.NotNull(glyphPaint);

        var glyphFontField = typeof(TerminalFrameComposer).GetField("_glyphFont", BindingFlags.NonPublic | BindingFlags.Instance);
        var glyphFont = glyphFontField!.GetValue(composer) as SKFont;
        Assert.NotNull(glyphFont);

        Assert.Equal(paint.IsAntialias, glyphPaint!.IsAntialias);
        Assert.Equal(font.Subpixel, glyphFont!.Subpixel);
        Assert.Equal(font.Edging, glyphFont!.Edging);
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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 26f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 0, endRow: 0);

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
    public void RenderTo_WithGlyphAtlas_RendersNonZeroStartRow()
    {
        using var composer = new TerminalFrameComposer();
        using var atlas = new GlyphAtlas(SKTypeface.Default, 26f);
        atlas.EnsureGlyph(new GlyphKey("W"));
        composer.GlyphAtlas = atlas;

        var buffer = new TerminalBuffer(rows: 2, columns: 2);
        buffer.SetCursor(1, 0);
        buffer.WriteText("W".AsSpan(), CellAttributes.Default);

        const float cellW = 24f;
        const float cellH = 30f;

        using var bitmap = new SKBitmap(48, 60, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 26f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 1, endRow: 1);

        int targetRowPixels = 0;
        int otherRowPixels = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool isDrawn = px.Red != 0 || px.Green != 0 || px.Blue != 0 || px.Alpha != 255;
                if (!isDrawn) continue;

                if (y >= 30 && y < 60)
                {
                    targetRowPixels++;
                }
                else
                {
                    otherRowPixels++;
                }
            }
        }

        Assert.True(targetRowPixels > 0, "Expected glyph pixels inside the rendered non-zero start row.");
        Assert.Equal(0, otherRowPixels);
    }

    [Fact]
    public void RenderTo_BackgroundSpans_FillExactCellRectanglesWithoutStyling()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 2, columns: 3);
        buffer.SetAlternateScreen(true);
        var attributes = new CellAttributes
        {
            Background = SgrColorArgb.FromRgb(0x20, 0x40, 0x60)
        };

        buffer.SetCursor(0, 0);
        buffer.WriteText("   ".AsSpan(), attributes);
        buffer.SetCursor(1, 0);
        buffer.WriteText(" ".AsSpan(), attributes);

        const int cellW = 8;
        const int cellH = 10;
        using var bitmap = new SKBitmap(cellW * 3, cellH * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 8f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 0, endRow: 1);

        var expectedBackground = new SKColor(0x20, 0x40, 0x60, 0xFF);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var expected = y < cellH || x < cellW ? expectedBackground : SKColors.Black;
                Assert.Equal(expected, bitmap.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void RenderTo_MainScreenBackgroundSpans_UsePillStyling()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 1, columns: 3);
        var attributes = new CellAttributes
        {
            Background = SgrColorArgb.FromRgb(0x20, 0x40, 0x60)
        };

        buffer.SetCursor(0, 0);
        buffer.WriteText("   ".AsSpan(), attributes);

        const int cellW = 8;
        const int cellH = 10;
        using var bitmap = new SKBitmap(cellW * 3, cellH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 8f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 0, endRow: 0);

        var expectedBackground = new SKColor(0x20, 0x40, 0x60, 0xFF);
        var corner = bitmap.GetPixel(0, 0);

        var center = bitmap.GetPixel(cellW, cellH / 2);
        Assert.True(
            center.Red > 0 || center.Green > 0 || center.Blue > 0,
            "Expected main-screen pill background to draw through the row center.");
        Assert.NotEqual(expectedBackground, corner);
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
            Color = SKColors.White,
            IsAntialias = false
        };
        using var font = new SKFont(SKTypeface.Default, 16f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 0, endRow: 0);

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

        var fm = font.Metrics;
        var bounds = new SKRect();
        font.MeasureText("█", out bounds);
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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 18f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: 24f, cellH: 24f, startRow: 0, endRow: 0);

        var glyphPaintField = typeof(TerminalFrameComposer).GetField("_glyphPaint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(glyphPaintField);

        var glyphPaint = glyphPaintField!.GetValue(composer) as SKPaint;

        var glyphFontField = typeof(TerminalFrameComposer).GetField("_glyphFont", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(glyphFontField);
        var glyphFont = glyphFontField!.GetValue(composer) as SKFont;
        Assert.NotNull(glyphFont);

        Assert.False(glyphPaint!.IsAntialias);
        Assert.Equal(SKFontEdging.Alias, glyphFont!.Edging);
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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 14f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: cell, cellH: cell, startRow: 0, endRow: 1);

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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 14f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 14f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 14f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

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
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 14f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: cell, cellH: cell, startRow: 0, endRow: 0);

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

    [Fact]
    public void RenderTo_NearbyRows_DoNotBleedIntoEachOther()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 3, columns: 24);

        buffer.SetCursor(0, 0);
        buffer.WriteText(new string('A', 20).AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText(new string('B', 20).AsSpan(), CellAttributes.Default);
        buffer.SetCursor(2, 0);
        buffer.WriteText(new string('C', 20).AsSpan(), CellAttributes.Default);

        const int width = 480;
        const int cellH = 28;
        using var bitmap = new SKBitmap(width, cellH * 3, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 24f);


        composer.RenderTo(canvas, buffer, paint, font, cellW: 20f, cellH: cellH, startRow: 1, endRow: 1);

        int middleRowInk = 0;
        int topRowInk = 0;
        int bottomRowInk = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool isDrawn = px.Red != 0 || px.Green != 0 || px.Blue != 0 || px.Alpha != 255;
                if (!isDrawn) continue;

                if (y >= cellH && y < cellH * 2)
                    middleRowInk++;
                else if (y < cellH)
                    topRowInk++;
                else
                    bottomRowInk++;
            }
        }

        Assert.True(middleRowInk > 0, "Expected ink in the rendered middle row.");
        Assert.Equal(0, topRowInk);
        Assert.Equal(0, bottomRowInk);
    }

    [Fact]
    public void RenderTo_OversizedGlyph_IsClippedToRowBounds()
    {
        using var composer = new TerminalFrameComposer();
        var buffer = new TerminalBuffer(rows: 2, columns: 2);
        buffer.SetCursor(0, 0);
        buffer.WriteText("g".AsSpan(), CellAttributes.Default);

        const float cellW = 24f;
        const float cellH = 18f;

        using var bitmap = new SKBitmap(48, 36, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 34f);


        composer.RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow: 0, endRow: 0);

        int row0Ink = 0;
        int row1Ink = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                bool ink = px.Red != 0 || px.Green != 0 || px.Blue != 0 || px.Alpha != 255;
                if (!ink) continue;

                if (y < cellH)
                    row0Ink++;
                else
                    row1Ink++;
            }
        }

        Assert.True(row0Ink > 0, "Expected oversized glyph to render into row 0.");
        Assert.Equal(0, row1Ink);
    }
}
