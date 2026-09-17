using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class SilkTerminalRendererGlyphTests
{
    private const int RequestedFramebufferWidth = 128;
    private const int RequestedFramebufferHeight = 128;
    private const float CellWidth = 64f;
    private const float CellHeight = 48f;

    [Fact]
    public unsafe void GlyphPassStillRendersAfterChromeDraw()
    {
        IWindow? window = null;
        GL? gl = null;
        try
        {
            global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(RequestedFramebufferWidth, RequestedFramebufferHeight),
                Title = "Dotty glyph regression test",
                IsVisible = false,
                VSync = false,
                ShouldSwapAutomatically = false,
                API = new GraphicsAPI(
                    ContextAPI.OpenGL,
                    ContextProfile.Core,
                    ContextFlags.Default,
                    new APIVersion(3, 3)),
            };
            window = Window.Create(options);
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
            if (framebufferWidth < (int)CellWidth + 1 || framebufferHeight < (int)CellHeight + 1)
            {
                Assert.Skip($"OpenGL window has no usable framebuffer ({framebufferWidth}x{framebufferHeight})");
                return;
            }

            using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
            var glyphKey = new GlyphKey("A", SKTypeface.Default, 16f, bold: false);
            Assert.True(atlas.EnsureGlyph(glyphKey, out GlyphInfo glyph));

            var cell = new CellInstance
            {
                Col = 0,
                Row = 0,
                GlyphX = checked((short)glyph.X),
                GlyphY = checked((short)glyph.Y),
                GlyphW = checked((short)glyph.Width),
                GlyphH = checked((short)glyph.Height),
                OffX = checked((short)glyph.LeftBearing),
                OffY = checked((short)(glyph.BaselineOffset + glyph.TopBearing)),
                FgR = 255,
                FgG = 255,
                FgB = 255,
                Flags = 0,
                // The clear colour is black and this alpha is zero, so the
                // background pass cannot change any pixel in the cell.
                BgR = 0,
                BgG = 0,
                BgB = 0,
                BgA = 0,
            };

            // Keep chrome visibly drawable but wholly outside the first cell.
            var chrome = new ChromeQuadInstance
            {
                X = framebufferWidth - 24,
                Y = framebufferHeight - 24,
                W = 16,
                H = 16,
                Radius = 0,
                Blur = 0,
                TopR = 1,
                TopG = 0,
                TopB = 0,
                TopA = 1,
                BottomR = 1,
                BottomG = 0,
                BottomB = 0,
                BottomA = 1,
            };

            using var renderer = new SilkTerminalRenderer(gl, atlas);
            renderer.Render(
                new[] { cell },
                new[] { chrome },
                atlas.Width,
                atlas.Height,
                framebufferWidth,
                framebufferHeight,
                CellWidth,
                CellHeight,
                underlineY: 0.85f,
                strikeY: 0.7f,
                lineHalf: 0.04f,
                clearColor: SgrColorArgb.FromRgb(0, 0, 0),
                frameCaptured: true);

            gl.Finish();
            byte[] pixels = new byte[checked(framebufferWidth * framebufferHeight * 4)];
            fixed (byte* pixelPtr = pixels)
            {
                gl.ReadPixels(
                    0,
                    0,
                    (uint)framebufferWidth,
                    (uint)framebufferHeight,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    pixelPtr);
            }

            int changedPixels = 0;
            for (int topY = 0; topY < (int)CellHeight; topY++)
            {
                int glY = framebufferHeight - 1 - topY;
                for (int x = 0; x < (int)CellWidth; x++)
                {
                    int offset = checked((glY * framebufferWidth + x) * 4);
                    if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0)
                    {
                        changedPixels++;
                    }
                }
            }

            Assert.True(
                changedPixels > 0,
                $"Expected glyph coverage inside the first {CellWidth}x{CellHeight} cell after chrome, but found no changed RGB pixels.");
        }
        finally
        {
            window.Dispose();
        }
    }
}
