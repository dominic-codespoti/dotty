using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

[Collection(nameof(NativeGlCollection))]
public sealed class SilkDecorationRenderingTests
{
    private const int CellWidth = 16;
    private const int CellHeight = 32;
    private const float TextSize = 16f;

    [Fact]
    public unsafe void UnderlineSprite_DrawsInTheSgr58ColourOnItsMetricRows()
    {
        IWindow? window = null;
        GL? gl = null;
        try
        {
            global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            window = Window.Create(WindowOptions.Default with
            {
                Size = new Vector2D<int>(64, 64),
                Title = "Dotty decoration test",
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
            int framebufferWidth = window.FramebufferSize.X;
            int framebufferHeight = window.FramebufferSize.Y;
            if (framebufferWidth < 2 * CellWidth || framebufferHeight < CellHeight)
            {
                Assert.Skip($"OpenGL window has no usable framebuffer ({framebufferWidth}x{framebufferHeight})");
                return;
            }

            // Underline rows 26-27 (2 px), drawn on two blank underlined cells in SGR 58 green.
            var sprites = CellSpriteMetrics.Create(CellWidth, CellHeight, 24, 26, 2, 16, 2, 0, 2);
            var buffer = new TerminalBuffer(rows: 1, columns: 2);
            buffer.SetCursor(0, 0);
            buffer.WriteText("  ".AsSpan(), new CellAttributes
            {
                UnderlineStyle = UnderlineStyle.Single,
                UnderlineColor = SgrColorArgb.FromRgb(0, 255, 0),
            });
            using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize, initialSize: 128);
            var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize,
                new FrameGeometry(CellWidth, CellHeight, 1, 2), textOptions: new TextAttributeOptions(0.5f, true, Sprites: sprites));
            Assert.Equal(2, result.InstanceCount);

            using var renderer = new SilkTerminalRenderer(gl, atlas);
            renderer.Render(result.AsSpan(), ReadOnlySpan<ChromeQuadInstance>.Empty, atlas.Width, atlas.Height,
                framebufferWidth, framebufferHeight, CellWidth, CellHeight,
                clearColor: SgrColorArgb.FromRgb(0, 0, 0), frameCaptured: true);
            gl.Finish();

            byte[] pixels = new byte[checked(framebufferWidth * framebufferHeight * 4)];
            fixed (byte* pixelPtr = pixels)
                gl.ReadPixels(0, 0, (uint)framebufferWidth, (uint)framebufferHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixelPtr);

            for (int topY = 0; topY < CellHeight; topY++)
            {
                bool underlineRow = topY is 26 or 27;
                int glY = framebufferHeight - 1 - topY;
                for (int x = 0; x < 2 * CellWidth; x++)
                {
                    int offset = (glY * framebufferWidth + x) * 4;
                    var rgb = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
                    Assert.True(underlineRow ? rgb == (0, 255, 0) : rgb == (0, 0, 0), $"pixel ({x},{topY}) = {rgb}");
                }
            }
        }
        finally
        {
            window.Dispose();
        }
    }
}
