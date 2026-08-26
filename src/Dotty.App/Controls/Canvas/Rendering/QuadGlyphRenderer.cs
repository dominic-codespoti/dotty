using System;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Owns the GPU-plan glyph rendering state that depends on the atlas: the
/// atlas SKImage (recreated when the atlas grows — the old bitmap is disposed),
/// the coverage-sampling paint, and the flush paints. Drains the composer's
/// quad batch each frame via <see cref="Flush"/>.
///
/// Coverage sampling uses an RGBA8 twin of the A8 atlas (RGB=white, A=coverage)
/// sampled by the standard image shader under Modulate blending: texel
/// (1,1,1,cov) × vertex fg = fg with coverage alpha. An earlier
/// SKRuntimeEffect that replicated A8 .a in the fragment shader silently drew
/// nothing on hardware GL (radeonsi/XWayland, measured 2026-08-26) while
/// working on software GL — the plain image-shader path is portable.
/// </summary>
public sealed class QuadGlyphRenderer : IDisposable
{
    private readonly GlyphAtlas _atlas;
    private readonly QuadGlyphBatch _batch = new();
    // DrawVertices modulates vertex colors by the paint's color when no shader
    // is set; white lets the vertex colors pass through.
    private readonly SKPaint _solidPaint = new() { IsAntialias = false, Color = SKColors.White };
    private SKImage? _atlasImage;
    private SKBitmap? _atlasRgba; // RGBA twin pixels (ContentVersion-keyed)
    private int _atlasPixelVersion = -1;
    private SKPaint? _glyphPaint;

    public QuadGlyphBatch Batch => _batch;
    public GlyphAtlas Atlas => _atlas;

    public QuadGlyphRenderer(GlyphAtlas atlas)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
    }

    /// <summary>
    /// Recreates the atlas SKImage when the atlas bitmap was replaced by
    /// growth (generation bump). The caller (UI thread) serializes with atlas
    /// mutation; the image shares the bitmap's pixels.
    /// </summary>
    private int _diagRebuilds;

    private void EnsureAtlasImage()
    {
        // Two-tier freshness:
        //   - RGBA pixels: rebuilt only when ContentVersion changes (per
        //     placed glyph — rare after the charset warms up).
        //   - SKImage: recreated EVERY frame. A cached SKImage's GPU texture
        //     does not survive the lease/context cycle on hardware GL
        //     (radeonsi/XWayland — cached images render blank; measured
        //     2026-08-26). FromBitmap is a 16 MB memcpy (~2 ms); replacing it
        //     with direct texture sub-updates is the follow-up.
        if (_atlasRgba == null || _atlasPixelVersion != _atlas.ContentVersion)
        {
            _atlasImage?.Dispose();
            _atlasRgba?.Dispose();
            (_atlasImage, _atlasRgba) = BuildRgbaTwin(_atlas.AtlasBitmap);
            _atlasPixelVersion = _atlas.ContentVersion;
            _glyphPaint?.Dispose();
            _glyphPaint = null;
            return;
        }
        // Fresh image each frame via FromPixelCopy: it allocates NEW native
        // pixels, so Skia's texture cache sees a fresh pixel-ref. Reusing one
        // SKBitmap/ SKImage across leases left a failed first upload cached
        // (blank glyphs on hardware GL); a fresh bitmap each frame worked but
        // paid the A8→RGBA fill every frame. FromPixelCopy = pure 16 MB memcpy.
        _atlasImage?.Dispose();
        var info = new SKImageInfo(_atlasRgba.Width, _atlasRgba.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        _atlasImage = SKImage.FromPixelCopy(info, _atlasRgba.GetPixels());
        _glyphPaint?.Dispose();
        _glyphPaint = null;
    }

    /// <summary>
    /// A8 coverage → RGBA8 twin (RGB=white, A=coverage). Built on the CPU once
    /// per atlas generation; the GPU samples it as a plain RGBA texture.
    /// </summary>
    private static unsafe (SKImage Image, SKBitmap Source) BuildRgbaTwin(SKBitmap a8)
    {
        int w = a8.Width, h = a8.Height;
        var rgba = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        byte* src = (byte*)a8.GetPixels().ToPointer();
        byte* dst = (byte*)rgba.GetPixels().ToPointer();
        long n = (long)w * h;
        for (long i = 0; i < n; i++)
        {
            dst[i * 4] = 0xFF;
            dst[i * 4 + 1] = 0xFF;
            dst[i * 4 + 2] = 0xFF;
            dst[i * 4 + 3] = src[i];
        }
        // The image references the bitmap's pixels: both stay alive until the
        // next rebuild disposes them.
        return (SKImage.FromBitmap(rgba), rgba);
    }

    private SKPaint GetGlyphPaint()
    {
        EnsureAtlasImage();
        if (_glyphPaint != null) return _glyphPaint;

        // Plain image shader: texel (1,1,1,cov) × vertex fg under Modulate.
        _glyphPaint = new SKPaint
        {
            Shader = _atlasImage!.ToShader(),
            IsAntialias = false,
        };
        return _glyphPaint;
    }

    public void Flush(SKCanvas canvas)
    {
        if (_batch.GlyphQuadCount == 0 && _batch.SolidQuadCount == 0) return;
        _batch.Flush(canvas, GetGlyphPaint(), _solidPaint);
    }

    public void Dispose()
    {
        _atlasImage?.Dispose();
        _atlasImage = null;
        _atlasRgba?.Dispose();
        _atlasRgba = null;
        _glyphPaint?.Dispose();
        _glyphPaint = null;
        _solidPaint.Dispose();
    }
}
