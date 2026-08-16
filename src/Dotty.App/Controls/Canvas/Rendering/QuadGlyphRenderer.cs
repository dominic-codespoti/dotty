using System;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Owns the GPU-plan glyph rendering state that depends on the atlas: the
/// atlas SKImage (recreated when the atlas grows — the old bitmap is disposed),
/// the coverage-sampling shader, and the flush paints. Drains the composer's
/// quad batch each frame via <see cref="Flush"/>.
/// The shader extracts the A8 coverage and returns it in all channels; the
/// DrawVertices Modulate blend multiplies it by the per-vertex fg color.
/// </summary>
public sealed class QuadGlyphRenderer : IDisposable
{
    private const string CoverageShader = @"
        uniform shader atlas;
        half4 main(float2 coord) {
            half4 t = atlas.eval(coord);
            return half4(t.a);   // A8 texture: coverage replicated
        }";

    private readonly GlyphAtlas _atlas;
    private readonly QuadGlyphBatch _batch = new();
    // DrawVertices modulates vertex colors by the paint's color when no shader
    // is set; white lets the vertex colors pass through.
    private readonly SKPaint _solidPaint = new() { IsAntialias = false, Color = SKColors.White };
    private SKImage? _atlasImage;
    private int _atlasGeneration = -1;
    private SKRuntimeEffect? _effect;
    private SKPaint? _glyphPaint;
    private SKRuntimeEffectChildren? _children;

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
    private void EnsureAtlasImage()
    {
        if (_atlasImage != null && _atlasGeneration == _atlas.Generation) return;
        _atlasImage?.Dispose();
        _atlasImage = SKImage.FromBitmap(_atlas.AtlasBitmap);
        _atlasGeneration = _atlas.Generation;
        _glyphPaint?.Dispose();
        _glyphPaint = null;
        _children?.Dispose();
        _children = null;
    }

    private SKPaint GetGlyphPaint()
    {
        EnsureAtlasImage();
        if (_glyphPaint != null) return _glyphPaint;

        _effect ??= SKRuntimeEffect.CreateShader(CoverageShader, out _);
        var uniforms = new SKRuntimeEffectUniforms(_effect);
        _children = new SKRuntimeEffectChildren(_effect);
        _children["atlas"] = _atlasImage!.ToShader();
        // The paint takes ownership of the shader. The children wrapper is
        // retained as a field and disposed AFTER the paint: its native child
        // shaders are referenced by the effect shader the paint owns, and a
        // premature finalizer dispose would free them mid-draw (SIGABRT).
        _glyphPaint = new SKPaint
        {
            Shader = _effect.ToShader(uniforms, _children),
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
        _glyphPaint?.Dispose();
        _glyphPaint = null;
        _children?.Dispose();
        _children = null;
        _effect?.Dispose();
        _effect = null;
        _solidPaint.Dispose();
    }
}
