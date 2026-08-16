using System;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// GPU-plan Phase 2: the quad glyph path (A8 atlas + DrawVertices) must render
/// glyphs, colors, box geometry, and underlines into the right cells, and fall
/// back to the direct path for complex decorations.
/// Rasterization is grayscale AA (the atlas is A8) vs the direct path's
/// subpixel AA — coverage-presence assertions, not pixel identity.
/// All scenarios live in ONE test method: the xunit v3 vstest adapter flakily
/// drops individual methods from this small native-heavy assembly during
/// discovery (observed 2026-08-16), so per-scenario methods could silently not
/// run. A single method either runs fully or is visibly absent.
/// </summary>
public sealed class QuadGlyphRenderingTests
{
    private const float CellW = 24f;
    private const float CellH = 30f;

    private static (TerminalFrameComposer composer, GlyphAtlas atlas, QuadGlyphRenderer renderer) CreateQuadComposer()
    {
        var composer = new TerminalFrameComposer();
        var textShaper = new TextShaper();
        composer.TextShaper = textShaper;
        composer.ShapedRunCache = new ShapedRunCache();
        var atlas = new GlyphAtlas(SKTypeface.Default, 26f);
        var renderer = new QuadGlyphRenderer(atlas);
        composer.GlyphAtlas = atlas;
        composer.QuadRenderer = renderer;
        composer.UseQuadGlyphs = true;
        return (composer, atlas, renderer);
    }

    private static SKBitmap Render(TerminalFrameComposer composer, TerminalBuffer buffer, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 26f);
        composer.RenderTo(canvas, buffer, paint, font, CellW, CellH, startRow: 0, endRow: buffer.Rows - 1);
        return bitmap;
    }

    private static int CountNonBackground(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        int count = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x1); x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red != 0 || px.Green != 0 || px.Blue != 0)
                    count++;
            }
        }
        return count;
    }

    private static int CountColor(SKBitmap bitmap, int x0, int y0, int x1, int y1, SKColor color)
    {
        int count = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x1); x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red == color.Red && px.Green == color.Green && px.Blue == color.Blue)
                    count++;
            }
        }
        return count;
    }

    [Fact]
    public void QuadPath_RendersGlyphsColorsAndGeometry()
    {
        // 1. Single glyph in the correct cell.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 3);
                buffer.SetCursor(0, 0);
                buffer.WriteText("W".AsSpan(), CellAttributes.Default);

                using var bitmap = Render(composer, buffer, 3 * (int)CellW, (int)CellH);
                Assert.True(CountNonBackground(bitmap, 0, 0, (int)CellW, (int)CellH) > 0,
                    "glyph should render in cell (0,0)");
                Assert.Equal(0, CountNonBackground(bitmap, (int)CellW, 0, 3 * (int)CellW, (int)CellH));
                Assert.True(atlas.EntryCount >= 1, "atlas should contain the glyph");
            }
        }

        // 2. Multiple cells each render their glyph.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 3);
                buffer.SetCursor(0, 0);
                buffer.WriteText("AB".AsSpan(), CellAttributes.Default);

                using var bitmap = Render(composer, buffer, 3 * (int)CellW, (int)CellH);
                Assert.True(CountNonBackground(bitmap, 0, 0, (int)CellW, (int)CellH) > 0);
                Assert.True(CountNonBackground(bitmap, (int)CellW, 0, 2 * (int)CellW, (int)CellH) > 0,
                    "second cell should render its glyph");
                Assert.Equal(0, CountNonBackground(bitmap, 2 * (int)CellW, 0, 3 * (int)CellW, (int)CellH));
            }
        }

        // 3. Vertex color tints the glyph.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 2);
                var attrs = new CellAttributes { Foreground = SgrColorArgb.FromRgb(0xFF, 0x20, 0x20) };
                buffer.SetCursor(0, 0);
                buffer.WriteText("R".AsSpan(), attrs);

                using var bitmap = Render(composer, buffer, 2 * (int)CellW, (int)CellH);
                Assert.True(CountColor(bitmap, 0, 0, (int)CellW, (int)CellH, new SKColor(0xFF, 0x20, 0x20)) > 0,
                    "vertex color should tint the glyph red");
            }
        }

        // 4. Box-drawing geometry renders via solid quads.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 2);
                buffer.SetCursor(0, 0);
                buffer.WriteText("\u250C".AsSpan(), CellAttributes.Default); // box drawing corner

                using var bitmap = Render(composer, buffer, 2 * (int)CellW, (int)CellH);
                Assert.True(CountNonBackground(bitmap, 0, 0, (int)CellW, (int)CellH) > 0,
                    "box-drawing corner should render via solid quads");
            }
        }

    }

    [Fact]
    public void QuadPath_UnderlinesAndFallbacks()
    {
        // 5. Underline renders as a solid quad in the lower cell band.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 2);
                var attrs = new CellAttributes { UnderlineStyle = UnderlineStyle.Single };
                buffer.SetCursor(0, 0);
                buffer.WriteText("U".AsSpan(), attrs);

                using var bitmap = Render(composer, buffer, 2 * (int)CellW, (int)CellH);
                Assert.True(CountNonBackground(bitmap, 0, (int)(CellH * 0.5), (int)CellW, (int)CellH) > 0,
                    "underline should render as a solid quad in the lower cell band");
            }
        }

        // 6. Complex decorations fall back to the direct path without throwing.
        {
            var (composer, atlas, renderer) = CreateQuadComposer();
            using (composer) using (atlas) using (renderer)
            {
                var buffer = new TerminalBuffer(rows: 1, columns: 2);
                var attrs = new CellAttributes { UnderlineStyle = UnderlineStyle.Dashed };
                buffer.SetCursor(0, 0);
                buffer.WriteText("D".AsSpan(), attrs);

                using var bitmap = Render(composer, buffer, 2 * (int)CellW, (int)CellH);
                Assert.True(CountNonBackground(bitmap, 0, 0, (int)CellW, (int)CellH) > 0,
                    "dashed-underline row should render via the direct fallback");
            }
        }

    }

    [Fact]
    public void QuadPath_MatchesDirectPathCoverage()
    {
        // 7. Quad and direct paths cover the same cell (AA mode differs).
        var quadComposer = CreateQuadComposer();
        var directComposer = new TerminalFrameComposer();
        using (quadComposer.composer)
        using (quadComposer.atlas)
        using (quadComposer.renderer)
        using (directComposer)
        {
            var buffer = new TerminalBuffer(rows: 1, columns: 3);
            buffer.SetCursor(0, 1);
            buffer.WriteText("W".AsSpan(), CellAttributes.Default);

            using var quadBitmap = Render(quadComposer.composer, buffer, 3 * (int)CellW, (int)CellH);
            using var directBitmap = Render(directComposer, buffer, 3 * (int)CellW, (int)CellH);

            Assert.True(CountNonBackground(quadBitmap, (int)CellW, 0, 2 * (int)CellW, (int)CellH) > 0,
                "quad path should cover the middle cell");
            Assert.True(CountNonBackground(directBitmap, (int)CellW, 0, 2 * (int)CellW, (int)CellH) > 0,
                "direct path should cover the middle cell");
            Assert.Equal(0, CountNonBackground(quadBitmap, 0, 0, (int)CellW, (int)CellH));
            Assert.Equal(0, CountNonBackground(quadBitmap, 2 * (int)CellW, 0, 3 * (int)CellW, (int)CellH));
        }
    }
}
