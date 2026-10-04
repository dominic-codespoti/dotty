using System;
using System.IO;
using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

[CollectionDefinition(nameof(NativeGlCollection), DisableParallelization = true)]
public sealed class NativeGlCollection { }

[Collection(nameof(NativeGlCollection))]
public sealed class SilkColorGlyphRenderingTests
{
    private const string NotoColorEmojiPath = "/usr/share/fonts/noto/NotoColorEmoji.ttf";
    private const int FramebufferWidth = 128;
    private const int FramebufferHeight = 128;
    private const int CellWidth = 64;
    private const int CellHeight = 48;

    [Fact]
    public unsafe void IntrinsicColorSurvivesGreenForegroundTintAlphaAndViewportClipping()
    {
        if (!File.Exists(NotoColorEmojiPath))
        {
            Assert.Skip($"Color-glyph rendering test requires {NotoColorEmojiPath}");
            return;
        }

        IWindow? window = null;
        GL? gl = null;
        try
        {
            global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            window = Window.Create(WindowOptions.Default with
            {
                Size = new Vector2D<int>(FramebufferWidth, FramebufferHeight),
                Title = "Dotty color glyph regression test",
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
            int width = window!.FramebufferSize.X;
            int height = window.FramebufferSize.Y;
            if (width < CellWidth + 1 || height < CellHeight + 1)
            {
                Assert.Skip($"OpenGL window has no usable framebuffer ({width}x{height})");
                return;
            }

            using var typeface = SKTypeface.FromFile(NotoColorEmojiPath);
            Assert.NotNull(typeface);
            using var atlas = new GlyphAtlas(typeface!, 20f, initialSize: 128);
            Assert.True(atlas.EnsureGlyph(new GlyphKey("😀", typeface!, 20f, bold: false), out GlyphInfo glyph));
            Assert.True(glyph.IsColor);
            Assert.True(glyph.Width < CellWidth && glyph.Height < CellHeight, "Test emoji must fit in the first terminal cell.");

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
                FgR = 0,
                FgG = 255,
                FgB = 0,
                FgA = 128,
                BgR = 0,
                BgG = 0,
                BgB = 0,
                BgA = 255,
                Flags = CellFlags.ColorGlyph,
            };
            int clipWidth = CellWidth + Math.Max(1, glyph.Width / 2);
            if (clipWidth >= width)
            {
                Assert.Skip($"OpenGL framebuffer is too narrow for viewport clipping ({width}px; need more than {clipWidth}px)");
                return;
            }
            var clippedCell = cell;
            clippedCell.Col = 1;

            using var renderer = new SilkTerminalRenderer(gl!, atlas);
            renderer.Render(new[] { cell, clippedCell }, Array.Empty<ChromeQuadInstance>(),
                atlas.Width, atlas.Height, clipWidth, height, CellWidth, CellHeight,
                underlineY: 0.85f, strikeY: 0.7f, lineHalf: 0.04f,
                clearColor: SgrColorArgb.FromRgb(0, 0, 0), frameCaptured: true);
            Assert.Equal((long)atlas.Width * atlas.Height, renderer.R8TexturePayloadBytes);
            Assert.Equal((long)atlas.ColorWidth * atlas.ColorHeight * 4, renderer.Rgba8TexturePayloadBytes);
            gl!.Finish();

            byte[] pixels = new byte[checked(width * height * 4)];
            fixed (byte* pixelPtr = pixels)
            {
                gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixelPtr);
            }

            bool hasIntrinsicRed = false;
            bool hasBlendedPixel = false;
            int maxChannel = 0;
            for (int topY = 0; topY < CellHeight; topY++)
            {
                int glY = height - 1 - topY;
                for (int x = 0; x < CellWidth; x++)
                {
                    int offset = (glY * width + x) * 4;
                    byte r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
                    int maximum = Math.Max(r, Math.Max(g, b));
                    maxChannel = Math.Max(maxChannel, maximum);
                    hasBlendedPixel |= maximum > 8;
                    hasIntrinsicRed |= r > g + 8;
                }
            }

            Assert.True(hasBlendedPixel, "The intrinsic-color glyph must contribute visible pixels.");
            Assert.True(hasIntrinsicRed, "A green foreground must not replace intrinsic red/yellow emoji layers.");
            Assert.InRange(maxChannel, 9, 140);

            // Compare framebuffer pixels against real RGBA page pixels using the
            // existing GL_ONE / GL_ONE_MINUS_SRC_ALPHA blend equation.
            bool matchesAtlasComposite = false;
            SKBitmap colorPage = atlas.ColorAtlasBitmap!;
            int drawX = (int)MathF.Floor(cell.OffX + 0.5f);
            int drawY = (int)MathF.Floor(cell.OffY + 0.5f);
            for (int sourceY = 0; sourceY < glyph.Height && !matchesAtlasComposite; sourceY++)
            {
                for (int sourceX = 0; sourceX < glyph.Width && !matchesAtlasComposite; sourceX++)
                {
                    SKColor source = colorPage.GetPixel(glyph.X + sourceX, glyph.Y + sourceY);
                    if (source.Alpha < 200 || source.Red <= source.Green + 20) continue;
                    int expectedR = (source.Red * source.Alpha * cell.FgA + 32512) / 65025;
                    int expectedG = (source.Green * source.Alpha * cell.FgA + 32512) / 65025;
                    int expectedB = (source.Blue * source.Alpha * cell.FgA + 32512) / 65025;
                    for (int orientation = 0; orientation < 2 && !matchesAtlasComposite; orientation++)
                    {
                        int sourceRow = orientation == 0 ? sourceY : glyph.Height - 1 - sourceY;
                        int topY = drawY + sourceRow;
                        int x = drawX + sourceX;
                        if (x < 0 || x >= CellWidth || topY < 0 || topY >= CellHeight) continue;
                        int offset = ((height - 1 - topY) * width + x) * 4;
                        matchesAtlasComposite =
                            Math.Abs(pixels[offset] - expectedR) <= 4 &&
                            Math.Abs(pixels[offset + 1] - expectedG) <= 4 &&
                            Math.Abs(pixels[offset + 2] - expectedB) <= 4;
                    }
                }
            }
            Assert.True(matchesAtlasComposite, "Rendered RGB must equal a premultiplied atlas pixel modulated by foreground alpha and blended over black.");

            bool hasPartiallyClippedGlyph = false;
            for (int topY = 0; topY < CellHeight; topY++)
            {
                int glY = height - 1 - topY;
                for (int x = CellWidth; x < clipWidth; x++)
                {
                    int offset = (glY * width + x) * 4;
                    hasPartiallyClippedGlyph |= pixels[offset] > 8 || pixels[offset + 1] > 8 || pixels[offset + 2] > 8;
                }
            }
            Assert.True(hasPartiallyClippedGlyph, "The duplicate glyph must be visible up to the viewport's right clipping edge.");
            for (int topY = 0; topY < height; topY++)
            {
                int glY = height - 1 - topY;
                for (int x = clipWidth; x < width; x++)
                {
                    int offset = (glY * width + x) * 4;
                    Assert.Equal((byte)0, pixels[offset]);
                    Assert.Equal((byte)0, pixels[offset + 1]);
                    Assert.Equal((byte)0, pixels[offset + 2]);
                }
            }
        }
        finally
        {
            window!.Dispose();
        }
    }
}
