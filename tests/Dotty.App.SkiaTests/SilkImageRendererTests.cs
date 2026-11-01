using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Graphics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>Hidden GL 3.3 window for the image GL tests; <see cref="TryCreate"/> skips when unavailable.</summary>
internal sealed class ImageGlHost : IDisposable
{
    private readonly IWindow _window;
    public GL Gl { get; }
    public int Width => _window.FramebufferSize.X;
    public int Height => _window.FramebufferSize.Y;

    private ImageGlHost(IWindow window, GL gl)
    {
        _window = window;
        Gl = gl;
    }

    public static ImageGlHost? TryCreate()
    {
        IWindow? window = null;
        string? skipReason = null;
        try
        {
            global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(128, 128),
                Title = "Dotty image GL test",
                IsVisible = false,
                VSync = false,
                ShouldSwapAutomatically = false,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            };
            window = Window.Create(options);
            window.Initialize();
            GL gl = window.CreateOpenGL();
            var host = new ImageGlHost(window, gl);
            if (host.Width >= 64 && host.Height >= 64)
            {
                gl.Viewport(0, 0, (uint)host.Width, (uint)host.Height);
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                return host;
            }

            skipReason = $"OpenGL window has no usable framebuffer ({host.Width}x{host.Height})";
            host.Dispose();
        }
        catch (Exception exception)
        {
            window?.Dispose();
            skipReason = $"OpenGL 3.3 context unavailable: {exception.GetType().Name}: {exception.Message}";
        }

        Assert.Skip(skipReason ?? "OpenGL unavailable");
        return null;
    }

    public void Clear(float r, float g, float b, float a)
    {
        Gl.ClearColor(r, g, b, a);
        Gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    /// <summary>RGBA of the pixel at top-left-origin (x, y).</summary>
    public unsafe (int R, int G, int B, int A) ReadTopLeft(int x, int y)
    {
        Gl.Finish();
        byte* px = stackalloc byte[4];
        Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        Gl.ReadPixels(x, Height - 1 - y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        return (px[0], px[1], px[2], px[3]);
    }

    public void Dispose() => _window.Dispose();
}

internal static class ImageTestData
{
    public static ImagePixels Rgba(int w, int h, params (byte r, byte g, byte b, byte a)[] texels)
    {
        ImagePixels p = ImagePixels.Allocate(w, h, ImagePixelFormat.Rgba8Premultiplied);
        Span<byte> s = p.AsWritableSpan();
        for (int i = 0; i < w * h; i++)
        {
            s[i * 4] = texels[i].r;
            s[i * 4 + 1] = texels[i].g;
            s[i * 4 + 2] = texels[i].b;
            s[i * 4 + 3] = texels[i].a;
        }

        return p;
    }

    public static ImagePixels Rgb(int w, int h, params (byte r, byte g, byte b)[] texels)
    {
        ImagePixels p = ImagePixels.Allocate(w, h, ImagePixelFormat.Rgb8);
        Span<byte> s = p.AsWritableSpan();
        for (int i = 0; i < w * h; i++)
        {
            s[i * 3] = texels[i].r;
            s[i * 3 + 1] = texels[i].g;
            s[i * 3 + 2] = texels[i].b;
        }

        return p;
    }

    /// <summary>Draw list with one run per entry, already ordered by layer (entries must be sorted by layer).</summary>
    public static ImageDrawList List(params (ImagePixels px, float x, float y, float w, float h, ImageLayer layer)[] items)
    {
        var list = new ImageDrawList
        {
            Quads = new ImageQuadInstance[items.Length],
            Draws = new ImageDraw[items.Length],
            QuadCount = items.Length,
            DrawCount = items.Length,
            LayerDrawStart = new int[4],
        };
        for (int i = 0; i < items.Length; i++)
        {
            list.Quads[i] = new ImageQuadInstance
            {
                X = items[i].x,
                Y = items[i].y,
                W = items[i].w,
                H = items[i].h,
                U0 = 0,
                V0 = 0,
                U1 = 1,
                V1 = 1,
                Alpha = 1,
            };
            list.Draws[i] = new ImageDraw(items[i].px, i, 1, items[i].layer);
        }

        for (int layer = 0; layer <= 3; layer++)
        {
            int n = 0;
            while (n < items.Length && (int)items[n].layer < layer)
                n++;
            list.LayerDrawStart[layer] = n;
        }

        return list;
    }
}

[Collection(nameof(NativeGlCollection))]
public sealed class SilkImageRendererTests
{
    private static void Frame(SilkImageRenderer r, ImageDrawList list, int w, int h, params ImageLayer[] layers)
    {
        r.BeginFrame();
        foreach (ImageLayer layer in layers)
            r.DrawLayer(layer, list, w, h);
        r.EndFrame();
    }

    [Fact]
    public void IntegerScale_TexelCornersAreExactAndEdgesStayInsideQuad()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = ImageTestData.Rgba(2, 2,
            (255, 0, 0, 255), (0, 255, 0, 255),
            (0, 0, 255, 255), (255, 255, 0, 255));
        ImageDrawList list = ImageTestData.List((px, 10, 20, 8, 8, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        // 4x magnification: pixel centres within one pixel of a corner are pure texels (clamped).
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(10, 20));
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(11, 21));
        Assert.Equal((0, 255, 0, 255), host.ReadTopLeft(17, 20));
        Assert.Equal((0, 0, 255, 255), host.ReadTopLeft(10, 27));
        Assert.Equal((255, 255, 0, 255), host.ReadTopLeft(17, 27));
        // Just outside the quad stays the clear colour.
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(9, 20));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(18, 27));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(10, 19));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(10, 28));
    }

    [Fact]
    public void OneToOneScale_ReproducesEveryTexelExactly()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = ImageTestData.Rgba(2, 2,
            (10, 20, 30, 255), (40, 50, 60, 255),
            (70, 80, 90, 255), (100, 110, 120, 255));
        ImageDrawList list = ImageTestData.List((px, 5, 5, 2, 2, ImageLayer.BelowText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.BelowText);

        Assert.Equal((10, 20, 30, 255), host.ReadTopLeft(5, 5));
        Assert.Equal((40, 50, 60, 255), host.ReadTopLeft(6, 5));
        Assert.Equal((70, 80, 90, 255), host.ReadTopLeft(5, 6));
        Assert.Equal((100, 110, 120, 255), host.ReadTopLeft(6, 6));
    }

    [Fact]
    public void PremultipliedTexelBlendsOverColouredClear()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        // Premultiplied 50% red.
        using ImagePixels px = ImageTestData.Rgba(1, 1, (128, 0, 0, 128));
        ImageDrawList list = ImageTestData.List((px, 3, 3, 1, 1, ImageLayer.AboveText));

        host.Clear(0, 0, 1, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        var p = host.ReadTopLeft(3, 3);
        Assert.InRange(p.R, 127, 129);
        Assert.Equal(0, p.G);
        Assert.InRange(p.B, 126, 128);   // 255 * (1 - 128/255)
    }

    [Fact]
    public void InstanceAlphaScalesPremultipliedOutput()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = ImageTestData.Rgba(1, 1, (255, 255, 255, 255));
        ImageDrawList list = ImageTestData.List((px, 3, 3, 1, 1, ImageLayer.AboveText));
        list.Quads[0].Alpha = 0.5f;

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        var p = host.ReadTopLeft(3, 3);
        Assert.InRange(p.R, 127, 129);
        Assert.InRange(p.G, 127, 129);
    }

    [Fact]
    public void LayersDrawOnlyTheirOwnRunsAndInLayerOrder()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels red = ImageTestData.Rgba(1, 1, (255, 0, 0, 255));
        using ImagePixels green = ImageTestData.Rgba(1, 1, (0, 255, 0, 255));
        using ImagePixels blue = ImageTestData.Rgba(1, 1, (0, 0, 255, 255));
        // blue below background at (2,2)-(6,6); red below text at (4,4)-(8,8); green above text at (6,6)-(10,10)
        ImageDrawList list = ImageTestData.List(
            (blue, 2, 2, 4, 4, ImageLayer.BelowBackground),
            (red, 4, 4, 4, 4, ImageLayer.BelowText),
            (green, 6, 6, 4, 4, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.BelowBackground, ImageLayer.BelowText, ImageLayer.AboveText);
        Assert.Equal((0, 0, 255, 255), host.ReadTopLeft(3, 3));
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(5, 5));   // red over blue
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(6, 5));   // x=6 is outside blue, inside red
        Assert.Equal((0, 255, 0, 255), host.ReadTopLeft(7, 7));   // green over red
        Assert.Equal((0, 255, 0, 255), host.ReadTopLeft(9, 9));

        // Only the middle layer.
        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.BelowText);
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(3, 3));
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(7, 7));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(9, 9));

        // Empty layers issue no draw and leave pixels alone.
        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height);
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(5, 5));
    }

    [Fact]
    public void SameImageInManyQuadsUsesOneTexture()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = ImageTestData.Rgba(1, 1, (0, 200, 0, 255));
        ImageDrawList list = ImageTestData.List(
            (px, 1, 1, 2, 2, ImageLayer.AboveText),
            (px, 10, 10, 2, 2, ImageLayer.AboveText),
            (px, 20, 20, 2, 2, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        Assert.Equal(1, cache.TextureCount);
        Assert.Equal((0, 200, 0, 255), host.ReadTopLeft(1, 1));
        Assert.Equal((0, 200, 0, 255), host.ReadTopLeft(11, 11));
        Assert.Equal((0, 200, 0, 255), host.ReadTopLeft(21, 21));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(15, 15));
    }

    [Fact]
    public void Rgb8WithWidthNotMultipleOfFourUploadsWithoutRowSkew()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        // 3x2: 9-byte rows (not a multiple of 4).
        using ImagePixels px = ImageTestData.Rgb(3, 2,
            (1, 2, 3), (4, 5, 6), (7, 8, 9),
            (10, 11, 12), (13, 14, 15), (16, 17, 18));
        ImageDrawList list = ImageTestData.List((px, 8, 8, 3, 2, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        Assert.Equal((1, 2, 3, 255), host.ReadTopLeft(8, 8));
        Assert.Equal((4, 5, 6, 255), host.ReadTopLeft(9, 8));
        Assert.Equal((7, 8, 9, 255), host.ReadTopLeft(10, 8));
        Assert.Equal((10, 11, 12, 255), host.ReadTopLeft(8, 9));
        Assert.Equal((13, 14, 15, 255), host.ReadTopLeft(9, 9));
        Assert.Equal((16, 17, 18, 255), host.ReadTopLeft(10, 9));
    }

    [Fact]
    public void SubRectUvSamplesOnlyTheSelectedTexels()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = ImageTestData.Rgba(2, 1, (255, 0, 0, 255), (0, 0, 255, 255));
        ImageDrawList list = ImageTestData.List((px, 4, 4, 2, 2, ImageLayer.AboveText));
        list.Quads[0].U0 = 0.75f;  // right texel only; from its centre onwards (linear filter + clamp => pure)
        list.Quads[0].U1 = 1f;

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        Assert.Equal((0, 0, 255, 255), host.ReadTopLeft(4, 4));
        Assert.Equal((0, 0, 255, 255), host.ReadTopLeft(5, 5));
    }

    [Fact]
    public void EmptyDrawListIssuesNoDrawAndNoUpload()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        host.Clear(0, 0, 0, 1);
        renderer.BeginFrame();
        renderer.DrawLayer(ImageLayer.AboveText, null, host.Width, host.Height);
        renderer.DrawLayer(ImageLayer.AboveText, new ImageDrawList(), host.Width, host.Height);
        renderer.EndFrame();
        Assert.Equal(0, cache.TextureCount);
        Assert.False(renderer.UploadsDeferred);
    }

    [Fact]
    public void UploadBudgetDefersLaterImagesAndNextFrameCompletes()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        // 100-byte budget; each 4x4 RGBA image is 64 bytes.
        using var cache = new SilkImageTextureCache(host.Gl, bindings, uploadBudgetBytes: 100);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels a = Solid(4, 4, 255, 0, 0);
        using ImagePixels b = Solid(4, 4, 0, 255, 0);
        ImageDrawList list = ImageTestData.List(
            (a, 0, 0, 4, 4, ImageLayer.AboveText),
            (b, 10, 0, 4, 4, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);
        Assert.True(renderer.UploadsDeferred);
        Assert.Equal(1, renderer.SkippedDrawsLastFrame);
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(1, 1));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(11, 1));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);
        Assert.False(renderer.UploadsDeferred);
        Assert.Equal((255, 0, 0, 255), host.ReadTopLeft(1, 1));
        Assert.Equal((0, 255, 0, 255), host.ReadTopLeft(11, 1));
    }

    [Fact]
    public void LargeImageWithMipmapsStillCoversItsQuad()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        using ImagePixels px = Solid(512, 512, 30, 60, 90);   // mip chain path
        ImageDrawList list = ImageTestData.List((px, 4, 4, 32, 32, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        Assert.Equal((30, 60, 90, 255), host.ReadTopLeft(10, 10));
        Assert.Equal((30, 60, 90, 255), host.ReadTopLeft(35, 35));
        Assert.Equal((0, 0, 0, 255), host.ReadTopLeft(36, 36));
        Assert.True(cache.EstimatedTextureBytes > 512L * 512 * 4);   // base + mip estimate
    }

    [Fact]
    public void ImageWiderThanMaxTextureSizeIsDownscaledAndStillDraws()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        var bindings = new DirectGlBindingCache(host.Gl);
        using var cache = new SilkImageTextureCache(host.Gl, bindings);
        using var renderer = new SilkImageRenderer(host.Gl, cache, bindings);
        int max = cache.MaxTextureSize;
        if (max > 32768)
        {
            Assert.Skip($"GL_MAX_TEXTURE_SIZE {max} too large to exercise the oversize path cheaply");
            return;
        }

        using ImagePixels px = Solid(max + 1, 1, 50, 100, 150);
        ImageDrawList list = ImageTestData.List((px, 4, 4, 16, 4, ImageLayer.AboveText));

        host.Clear(0, 0, 0, 1);
        Frame(renderer, list, host.Width, host.Height, ImageLayer.AboveText);

        Assert.True(cache.Contains(px.Serial));
        Assert.Equal(max * 1 * 4, cache.EstimatedTextureBytes);   // uploaded as max x 1
        Assert.Equal((50, 100, 150, 255), host.ReadTopLeft(10, 5));
    }

    private static ImagePixels Solid(int w, int h, byte r, byte g, byte b)
    {
        ImagePixels p = ImagePixels.Allocate(w, h, ImagePixelFormat.Rgba8Premultiplied);
        Span<byte> s = p.AsWritableSpan();
        for (int i = 0; i < w * h; i++)
        {
            s[i * 4] = r;
            s[i * 4 + 1] = g;
            s[i * 4 + 2] = b;
            s[i * 4 + 3] = 255;
        }

        return p;
    }
}
