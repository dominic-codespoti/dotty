using System;
using System.IO;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

public sealed class GlyphAtlasColorTests
{
    private const string NotoColorEmojiPath = "/usr/share/fonts/noto/NotoColorEmoji.ttf";

    [Fact]
    public void NotoEmojiUsesPremultipliedColorPageAndMonochromeStillUsesA8()
    {
        if (!File.Exists(NotoColorEmojiPath))
        {
            Assert.Skip($"Color-glyph pixel test requires {NotoColorEmojiPath}");
            return;
        }

        using var colorTypeface = SKTypeface.FromFile(NotoColorEmojiPath);
        Assert.NotNull(colorTypeface);
        using var colorAtlas = new GlyphAtlas(colorTypeface!, 20f, initialSize: 128);
        long bytesBeforeColorGlyph = colorAtlas.SizeBytes;
        Assert.True(colorAtlas.EnsureGlyph(
            new GlyphKey("😀", colorTypeface!, 20f, bold: false), out GlyphInfo colorGlyph));
        Assert.True(colorGlyph.IsColor);
        Assert.True(GlyphAtlas.IntrinsicColorProbeScratchBytes > 0);
        var buffer = new TerminalBuffer(rows: 1, columns: 2);
        buffer.SetCursor(0, 0);
        buffer.WriteText("😀".AsSpan(), CellAttributes.Default);
        var frame = QuadFrameBuilder.Build(buffer, colorAtlas, colorTypeface!, 20f, new FrameGeometry(20f, 20f, 1, 2));
        Assert.True(frame.InstanceCount > 0);
        Assert.True((frame.Instances[0].Flags & CellFlags.ColorGlyph) != 0);
        Assert.Equal(255, frame.Instances[0].FgA);

        SKBitmap? colorPage = colorAtlas.ColorAtlasBitmap;
        Assert.Equal(SKColorType.Rgba8888, colorPage!.ColorType);
        Assert.Equal(SKAlphaType.Premul, colorPage.AlphaType);
        Assert.Equal(128 * 128 + 128 * 128 * 4, colorAtlas.SizeBytes);
        bool hasVisiblePixel = false;
        bool hasChromaticPixel = false;
        bool hasTransparentPixel = false;
        for (int y = 0; y < colorGlyph.Height; y++)
        {
            for (int x = 0; x < colorGlyph.Width; x++)
            {
                SKColor pixel = colorPage!.GetPixel(colorGlyph.X + x, colorGlyph.Y + y);
                hasVisiblePixel |= pixel.Alpha > 0;
                hasChromaticPixel |= pixel.Alpha > 0 &&
                    (System.Math.Abs(pixel.Red - pixel.Green) > 12 ||
                     System.Math.Abs(pixel.Green - pixel.Blue) > 12 ||
                     System.Math.Abs(pixel.Blue - pixel.Red) > 12);
                hasTransparentPixel |= pixel.Alpha == 0;
            }
        }
        Assert.True(hasVisiblePixel, "Color atlas entry must contain nontransparent source pixels.");
        Assert.True(hasChromaticPixel, "Noto's emoji must retain at least one intrinsic colored pixel.");
        Assert.True(hasTransparentPixel, "Glyph bounds must preserve transparent pixels rather than filling a rectangle.");
        Assert.True(colorAtlas.SizeBytes > bytesBeforeColorGlyph);

        using var monoAtlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        Assert.True(monoAtlas.EnsureGlyph(
            new GlyphKey("A", SKTypeface.Default, 16f, bold: false), out GlyphInfo monoGlyph));
        Assert.False(monoGlyph.IsColor);
        Assert.Null(monoAtlas.ColorAtlasBitmap);
        Assert.Equal(128 * 128, monoAtlas.SizeBytes);
    }
}
