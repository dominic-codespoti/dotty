using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// The render-recovery rebuild (plan-11 WI-5a): the host disposes a failed <see cref="SilkTerminalRenderer"/> and
/// creates a new one on the same context and atlas; the new one must draw without any state from the old one.
/// Also covers <see cref="SilkTerminalRenderer.ReadBackBuffer"/>, the <c>SCREENSHOT:</c> capture hook.
/// </summary>
[Collection(nameof(NativeGlCollection))]
public sealed class SilkTerminalRendererRebuildTests
{
    private const int RequestedSize = 128;
    private const float CellWidth = 32f;
    private const float CellHeight = 32f;

    [Fact]
    public void RecreatedRendererDrawsAgainOnTheSameContext()
    {
        IWindow? window = null;
        GL gl;
        try
        {
            global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            window = Window.Create(WindowOptions.Default with
            {
                Size = new Vector2D<int>(RequestedSize, RequestedSize),
                Title = "Dotty renderer rebuild test",
                IsVisible = false,
                VSync = false,
                ShouldSwapAutomatically = false,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
            });
            window.Initialize();
            gl = window.CreateOpenGL();
        }
        catch (Exception exception)
        {
            window?.Dispose();
            Assert.Skip($"OpenGL 3.3 context unavailable: {exception.GetType().Name}: {exception.Message}");
            return;
        }

        try
        {
            int width = window.FramebufferSize.X;
            int height = window.FramebufferSize.Y;
            if (width < (int)CellWidth || height < (int)CellHeight)
            {
                Assert.Skip($"OpenGL window has no usable framebuffer ({width}x{height})");
                return;
            }

            using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
            var pixels = new byte[checked(width * height * 4)];

            var first = new SilkTerminalRenderer(gl, atlas);
            Draw(first, atlas, width, height, red: 255, green: 0);
            first.ReadBackBuffer(width, height, pixels);
            Assert.Equal((byte)255, CellCenterPixel(pixels, width, height)[0]);
            first.Dispose();

            using var rebuilt = new SilkTerminalRenderer(gl, atlas);
            Draw(rebuilt, atlas, width, height, red: 0, green: 255);
            rebuilt.ReadBackBuffer(width, height, pixels);

            byte[] center = CellCenterPixel(pixels, width, height);
            Assert.Equal(new byte[] { 0, 255, 0 }, center[..3]);
            // Outside the cell only the black clear colour remains.
            Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(pixels, width, height, width - 2, height - 2)[..3]);
        }
        finally
        {
            window.Dispose();
        }
    }

    private static void Draw(SilkTerminalRenderer renderer, GlyphAtlas atlas, int width, int height, byte red, byte green)
    {
        // A background-only cell: no glyph rectangle, opaque background.
        var cell = new CellInstance
        {
            Col = 0,
            Row = 0,
            BgR = red,
            BgG = green,
            BgB = 0,
            BgA = 255,
        };
        renderer.Render(
            new[] { cell },
            ReadOnlySpan<ChromeQuadInstance>.Empty,
            atlas.Width,
            atlas.Height,
            width,
            height,
            CellWidth,
            CellHeight,
            underlineY: 0.85f,
            strikeY: 0.7f,
            lineHalf: 0.04f,
            clearColor: SgrColorArgb.FromRgb(0, 0, 0),
            frameCaptured: true);
    }

    private static byte[] CellCenterPixel(byte[] bottomUp, int width, int height) =>
        Pixel(bottomUp, width, height, (int)(CellWidth / 2), (int)(CellHeight / 2));

    // (x, topY) in window coordinates; the readback is bottom row first.
    private static byte[] Pixel(byte[] bottomUp, int width, int height, int x, int topY)
    {
        int offset = checked(((height - 1 - topY) * width + x) * 4);
        return bottomUp[offset..(offset + 4)];
    }
}
