using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Silk.NET.OpenGL;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// Pixel proofs of the image layer order inside <see cref="SilkTerminalRenderer.Render"/>:
/// Clear, underlay chrome, BelowBackground, cell backgrounds, BelowText, base chrome, glyphs, AboveText, scrollbar chrome.
/// Each proof renders into a hidden GL 3.3 window (skipped when none is available) and probes opaque pixels, so no
/// blending tolerance is involved. Cell size is 40x40 and every image/quad lies inside the first cell(s).
/// </summary>
[Collection(nameof(NativeGlCollection))]
public sealed class SilkTerminalRendererImageLayerTests
{
    private const float CellW = 40f;
    private const float CellH = 40f;
    private const int MinFramebuffer = 80;

    private static readonly (int R, int G, int B, int A) Red = (255, 0, 0, 255);
    private static readonly (int R, int G, int B, int A) Green = (0, 255, 0, 255);
    private static readonly (int R, int G, int B, int A) Blue = (0, 0, 255, 255);
    private static readonly (int R, int G, int B, int A) Yellow = (255, 255, 0, 255);
    private static readonly (int R, int G, int B, int A) Magenta = (255, 0, 255, 255);
    private static readonly (int R, int G, int B, int A) White = (255, 255, 255, 255);
    private static readonly (int R, int G, int B, int A) Black = (0, 0, 0, 255);

    private static ImageGlHost? TryHost()
    {
        ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is not null && (host.Width < MinFramebuffer || host.Height < MinFramebuffer))
        {
            string reason = $"OpenGL window framebuffer {host.Width}x{host.Height} is smaller than {MinFramebuffer}x{MinFramebuffer}";
            host.Dispose();
            Assert.Skip(reason);
            return null;
        }

        return host;
    }

    /// <summary>A background-only cell (no glyph): painted by pass 0 only.</summary>
    private static CellInstance BackgroundCell(int col, int row, byte r, byte g, byte b) => new()
    {
        Col = (ushort)col,
        Row = (ushort)row,
        FgR = 255, FgG = 255, FgB = 255, FgA = 255,
        BgR = r, BgG = g, BgB = b, BgA = 255,
    };

    /// <summary>A cell with a white glyph over a red background.</summary>
    private static CellInstance GlyphOnRed(GlyphInfo glyph) => new()
    {
        Col = 0,
        Row = 0,
        GlyphX = checked((short)glyph.X),
        GlyphY = checked((short)glyph.Y),
        GlyphW = checked((short)glyph.Width),
        GlyphH = checked((short)glyph.Height),
        OffX = checked((short)glyph.LeftBearing),
        OffY = checked((short)(glyph.BaselineOffset + glyph.TopBearing)),
        FgR = 255, FgG = 255, FgB = 255, FgA = 255,
        BgR = 255, BgG = 0, BgB = 0, BgA = 255,
    };

    private static ChromeQuadInstance Chrome(float x, float y, float size, float r, float g, float b) => new()
    {
        X = x, Y = y, W = size, H = size, Radius = 0, Blur = 0,
        TopR = r, TopG = g, TopB = b, TopA = 1,
        BottomR = r, BottomG = g, BottomB = b, BottomA = 1,
    };

    private static void Draw(
        ImageGlHost host, SilkTerminalRenderer renderer, GlyphAtlas atlas,
        CellInstance[] cells, ChromeQuadInstance[] chrome, int scrollbarChromeStart, ImageDrawList? images) =>
        renderer.Render(
            cells,
            chrome,
            atlas.Width,
            atlas.Height,
            host.Width,
            host.Height,
            CellW,
            CellH,
            clearColor: SgrColorArgb.FromRgb(0, 0, 0),
            frameCaptured: true,
            scrollbarChromeStart: scrollbarChromeStart,
            images: images);

    private static unsafe byte[] ReadFramebuffer(ImageGlHost host)
    {
        host.Gl.Finish();
        byte[] data = new byte[checked(host.Width * host.Height * 4)];
        host.Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = data)
        {
            host.Gl.ReadPixels(0, 0, (uint)host.Width, (uint)host.Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        return data;
    }

    private static (int R, int G, int B, int A) At(byte[] frame, ImageGlHost host, int x, int y)
    {
        int offset = ((host.Height - 1 - y) * host.Width + x) * 4;
        return (frame[offset], frame[offset + 1], frame[offset + 2], frame[offset + 3]);
    }

    /// <summary>
    /// Finds one fully covered glyph pixel (opaque white) and one background-only pixel (opaque red) inside the first
    /// cell of a baseline frame rendered without images.
    /// </summary>
    private static ((int X, int Y) Glyph, (int X, int Y) Background) FindProbes(byte[] baseline, ImageGlHost host)
    {
        (int, int)? glyph = null, background = null;
        for (int y = 0; y < (int)CellH; y++)
        {
            for (int x = 0; x < (int)CellW; x++)
            {
                var pixel = At(baseline, host, x, y);
                if (glyph is null && pixel == White)
                    glyph = (x, y);
                else if (background is null && pixel == Red)
                    background = (x, y);
            }
        }

        Assert.True(glyph is not null, "The baseline frame has no fully covered glyph pixel inside the first cell.");
        Assert.True(background is not null, "The baseline frame has no background-only pixel inside the first cell.");
        return (glyph!.Value, background!.Value);
    }

    private static ImageDrawList CellImage(ImagePixels pixels, ImageLayer layer, float width = CellW) =>
        ImageTestData.List((pixels, 0f, 0f, width, CellH, layer));

    [Fact]
    public void BelowBackgroundImageIsUnderCellBackgroundsButOverTheClearColour()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        using ImagePixels green = ImageTestData.Rgba(1, 1, (0, 255, 0, 255));
        // The image spans cells 0 and 1; only cell 0 has a (red) background.
        ImageDrawList images = CellImage(green, ImageLayer.BelowBackground, width: 2 * CellW);

        Draw(host, renderer, atlas, new[] { BackgroundCell(0, 0, 255, 0, 0) }, Array.Empty<ChromeQuadInstance>(), -1, images);

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Red, At(frame, host, 20, 20));    // cell background over the image
        Assert.Equal(Green, At(frame, host, 60, 20));  // no background: the image is visible over the clear colour
        Assert.Equal(Black, At(frame, host, 60, 60));  // outside the image: the clear colour
    }

    [Fact]
    public void BelowTextImageIsOverBackgroundsAndUnderGlyphs()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 32f, initialSize: 256);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("I", SKTypeface.Default, 32f, GlyphStyle.Bold), out GlyphInfo glyph));
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        CellInstance[] cells = { GlyphOnRed(glyph) };
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));

        Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, null);
        var (glyphAt, backgroundAt) = FindProbes(ReadFramebuffer(host), host);

        Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, CellImage(blue, ImageLayer.BelowText));

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Blue, At(frame, host, backgroundAt.X, backgroundAt.Y)); // above the red cell background
        Assert.Equal(White, At(frame, host, glyphAt.X, glyphAt.Y));          // below the glyph
    }

    [Fact]
    public void AboveTextImageIsOverGlyphs()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 32f, initialSize: 256);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("I", SKTypeface.Default, 32f, GlyphStyle.Bold), out GlyphInfo glyph));
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        CellInstance[] cells = { GlyphOnRed(glyph) };
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));

        Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, null);
        var (glyphAt, backgroundAt) = FindProbes(ReadFramebuffer(host), host);

        Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, CellImage(blue, ImageLayer.AboveText));

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Blue, At(frame, host, glyphAt.X, glyphAt.Y));
        Assert.Equal(Blue, At(frame, host, backgroundAt.X, backgroundAt.Y));
    }

    [Fact]
    public void BaseChromeSuchAsTheImeCaretIsAboveBelowTextImages()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));
        ChromeQuadInstance[] chrome = { Chrome(10, 10, 12, 1, 1, 0) }; // base chrome: no scrollbar start

        Draw(host, renderer, atlas, new[] { BackgroundCell(0, 0, 255, 0, 0) }, chrome, -1, CellImage(blue, ImageLayer.BelowText));

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Yellow, At(frame, host, 16, 16)); // chrome over the image
        Assert.Equal(Blue, At(frame, host, 36, 36));   // image over the red background, outside the chrome
    }

    [Fact]
    public void AboveTextImageIsOverBaseChromeAndUnderScrollbarChrome()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));
        ChromeQuadInstance[] chrome =
        {
            Chrome(10, 10, 12, 1, 1, 0),   // index 0: base chrome (yellow)
            Chrome(24, 24, 12, 1, 0, 1),   // index 1: scrollbar chrome (magenta)
        };

        Draw(host, renderer, atlas, new[] { BackgroundCell(0, 0, 255, 0, 0) }, chrome, 1, CellImage(blue, ImageLayer.AboveText));

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Blue, At(frame, host, 16, 16));     // image over base chrome
        Assert.Equal(Magenta, At(frame, host, 30, 30));  // scrollbar chrome over the image
        Assert.Equal(Blue, At(frame, host, 4, 4));       // image over the red background
    }

    [Fact]
    public void UnderlayChromeIsBelowEverythingAndBaseChromeKeepsItsLayer()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));
        ChromeQuadInstance underlay = Chrome(0, 0, 80, 0, 1, 0); // index 0: underlay (green), 80x80
        ChromeQuadInstance[] chrome =
        {
            underlay,
            Chrome(10, 10, 12, 1, 1, 0),   // index 1: base chrome (yellow), drawn from the offset range
        };

        renderer.Render(
            new[] { BackgroundCell(0, 0, 255, 0, 0) },
            chrome,
            atlas.Width,
            atlas.Height,
            host.Width,
            host.Height,
            CellW,
            CellH,
            clearColor: SgrColorArgb.FromRgb(0, 0, 0),
            frameCaptured: true,
            images: CellImage(blue, ImageLayer.BelowBackground, width: 2 * CellW),
            underlayChromeCount: 1);

        byte[] frame = ReadFramebuffer(host);
        Assert.Equal(Yellow, At(frame, host, 16, 16));  // base chrome over the cell background
        Assert.Equal(Red, At(frame, host, 30, 30));     // cell background over the underlay and the image
        Assert.Equal(Blue, At(frame, host, 60, 20));    // below-background image over the underlay
        Assert.Equal(Green, At(frame, host, 60, 60));   // underlay over the clear colour
    }

    [Fact]
    public void FramesWithoutImagesAgeTheTextureCacheSoUnusedTexturesAreEvicted()
    {
        using ImageGlHost? host = TryHost();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas);
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));
        CellInstance[] cells = { BackgroundCell(0, 0, 255, 0, 0) };

        Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, CellImage(blue, ImageLayer.AboveText));
        Assert.True(renderer.EstimatedImageTextureBytes > 0);
        Assert.False(renderer.ImageUploadsDeferred);

        // The image is not referenced any more (a hidden tab/pane): the cache ages once per rendered frame, so
        // after SilkImageTextureCache.DefaultMaxIdleFrames further frames the texture must be gone.
        for (int i = 0; i < SilkImageTextureCache.DefaultMaxIdleFrames + 5; i++)
            Draw(host, renderer, atlas, cells, Array.Empty<ChromeQuadInstance>(), -1, null);

        Assert.Equal(0L, renderer.EstimatedImageTextureBytes);
    }
}
