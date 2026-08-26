using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// CPU-built quad vertex batch for the GPU-path glyph renderer. Two passes:
/// textured glyph quads (positions + atlas UVs + vertex colors, drawn with the
/// atlas shader under Modulate blending) and solid quads (flat vertex colors,
/// no shader) for underlines/strikethrough/overline and box-drawing/block
/// geometry. SkiaSharp's DrawVertices takes full arrays with no count
/// parameter, so flush copies into exact-size arrays cached per vertex count —
/// steady frames (fixed window, stable content) allocate once and reuse.
/// </summary>
public sealed class QuadGlyphBatch
{
    private SKPoint[] _glyphPos = Array.Empty<SKPoint>();
    private SKPoint[] _glyphUv = Array.Empty<SKPoint>();
    private SKColor[] _glyphCol = Array.Empty<SKColor>();
    private SKPoint[] _solidPos = Array.Empty<SKPoint>();
    private SKColor[] _solidCol = Array.Empty<SKColor>();
    private int _glyphCount;
    private int _solidCount;

    private readonly Dictionary<int, GlyphArrays> _glyphCache = new();
    private readonly Dictionary<int, SolidArrays> _solidCache = new();
    private readonly Dictionary<int, ushort[]> _indexCache = new();

    private struct GlyphArrays
    {
        public SKPoint[] Pos;
        public SKPoint[] Uv;
        public SKColor[] Col;
    }

    private struct SolidArrays
    {
        public SKPoint[] Pos;
        public SKColor[] Col;
    }

    public int GlyphQuadCount => _glyphCount / 4;
    public int SolidQuadCount => _solidCount / 4;

    public void Reset()
    {
        _glyphCount = 0;
        _solidCount = 0;
    }

    private static void Ensure<T>(ref T[] arr, int needed)
    {
        if (arr.Length < needed)
            Array.Resize(ref arr, Math.Max(arr.Length * 2, needed));
    }

    public void AddGlyphQuad(float x, float y, float w, float h, SKRect uv, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref _glyphPos, _glyphCount + 4);
        Ensure(ref _glyphUv, _glyphCount + 4);
        Ensure(ref _glyphCol, _glyphCount + 4);

        float u0 = uv.Left, v0 = uv.Top, u1 = uv.Right, v1 = uv.Bottom;
        int i = _glyphCount;
        _glyphPos[i] = new SKPoint(x, y);
        _glyphUv[i] = new SKPoint(u0, v0);
        _glyphCol[i] = color;
        _glyphPos[i + 1] = new SKPoint(x + w, y);
        _glyphUv[i + 1] = new SKPoint(u1, v0);
        _glyphCol[i + 1] = color;
        _glyphPos[i + 2] = new SKPoint(x + w, y + h);
        _glyphUv[i + 2] = new SKPoint(u1, v1);
        _glyphCol[i + 2] = color;
        _glyphPos[i + 3] = new SKPoint(x, y + h);
        _glyphUv[i + 3] = new SKPoint(u0, v1);
        _glyphCol[i + 3] = color;
        _glyphCount += 4;
    }

    public void AddSolidQuad(float x, float y, float w, float h, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref _solidPos, _solidCount + 4);
        Ensure(ref _solidCol, _solidCount + 4);

        int i = _solidCount;
        _solidPos[i] = new SKPoint(x, y);
        _solidCol[i] = color;
        _solidPos[i + 1] = new SKPoint(x + w, y);
        _solidCol[i + 1] = color;
        _solidPos[i + 2] = new SKPoint(x + w, y + h);
        _solidCol[i + 2] = color;
        _solidPos[i + 3] = new SKPoint(x, y + h);
        _solidCol[i + 3] = color;
        _solidCount += 4;
    }

    /// <summary>
    /// Draws both passes. <paramref name="glyphPaint"/> must carry the atlas
    /// shader (sample coverage); solid quads are drawn with a plain paint and
    /// vertex colors directly. The atlas image is supplied by the renderer and
    /// must match the current atlas generation.
    /// </summary>

    /// <summary>
    /// Appends one cached row's vertices (QuadRowCache entry) with the row's
    /// absolute Y offset applied. Bulk copy — no per-quad branching.
    /// </summary>
    public void AppendGlyphRow(ReadOnlySpan<SKPoint> pos, ReadOnlySpan<SKPoint> uv, ReadOnlySpan<SKColor> col, float yOffset)
    {
        int count = pos.Length;
        if (count == 0) return;
        Ensure(ref _glyphPos, _glyphCount + count);
        Ensure(ref _glyphUv, _glyphCount + count);
        Ensure(ref _glyphCol, _glyphCount + count);

        int baseIdx = _glyphCount;
        for (int i = 0; i < count; i++)
        {
            _glyphPos[baseIdx + i] = new SKPoint(pos[i].X, pos[i].Y + yOffset);
            _glyphUv[baseIdx + i] = uv[i];
            _glyphCol[baseIdx + i] = col[i];
        }
        _glyphCount += count;
    }

    public void AppendSolidRow(ReadOnlySpan<SKPoint> pos, ReadOnlySpan<SKColor> col, float yOffset)
    {
        int count = pos.Length;
        if (count == 0) return;
        Ensure(ref _solidPos, _solidCount + count);
        Ensure(ref _solidCol, _solidCount + count);

        int baseIdx = _solidCount;
        for (int i = 0; i < count; i++)
        {
            _solidPos[baseIdx + i] = new SKPoint(pos[i].X, pos[i].Y + yOffset);
            _solidCol[baseIdx + i] = col[i];
        }
        _solidCount += count;
    }
    public void Flush(SKCanvas canvas, SKPaint? glyphPaint, SKPaint solidPaint)
    {
        if (_solidCount > 0)
        {
            var arrays = GetSolidArrays();
            canvas.DrawVertices(SKVertexMode.Triangles, arrays.Pos, null, arrays.Col, SKBlendMode.Modulate, GetQuadIndices(_solidCount), solidPaint);
        }

        if (_glyphCount > 0 && glyphPaint != null)
        {
            var arrays = GetGlyphArrays();
            canvas.DrawVertices(
                SKVertexMode.Triangles, arrays.Pos, arrays.Uv, arrays.Col,
                SKBlendMode.Modulate, indices: GetQuadIndices(_glyphCount), glyphPaint);
        }
    }

    /// <summary>
    /// Quad index buffer: (0,1,2)(0,2,3) per 4-vertex quad. REQUIRED — with
    /// indices: null, Triangles mode treats the vertex array as consecutive
    /// triples, so each quad's 4th vertex wraps into the next quad's first
    /// two and draws a stray triangle spanning from one row's end to the
    /// next row's start (diagonal streak artifacts, driver-dependent:
    /// visible on radeonsi, harmless on llvmpipe). Cached per vertex count
    /// like the vertex arrays.
    /// </summary>
    private ushort[] GetQuadIndices(int vertexCount)
    {
        if (_indexCache.TryGetValue(vertexCount, out var cached)) return cached;

        int quadCount = vertexCount / 4;
        var indices = new ushort[quadCount * 6];
        for (int q = 0; q < quadCount; q++)
        {
            int b = q * 4;
            int o = q * 6;
            indices[o] = (ushort)b;
            indices[o + 1] = (ushort)(b + 1);
            indices[o + 2] = (ushort)(b + 2);
            indices[o + 3] = (ushort)b;
            indices[o + 4] = (ushort)(b + 2);
            indices[o + 5] = (ushort)(b + 3);
        }
        _indexCache[vertexCount] = indices;
        return indices;
    }

    private GlyphArrays GetGlyphArrays()
    {
        if (_glyphCache.TryGetValue(_glyphCount, out var cached))
        {
            Array.Copy(_glyphPos, cached.Pos, _glyphCount);
            Array.Copy(_glyphUv, cached.Uv, _glyphCount);
            Array.Copy(_glyphCol, cached.Col, _glyphCount);
            return cached;
        }

        cached = new GlyphArrays
        {
            Pos = new SKPoint[_glyphCount],
            Uv = new SKPoint[_glyphCount],
            Col = new SKColor[_glyphCount],
        };
        Array.Copy(_glyphPos, cached.Pos, _glyphCount);
        Array.Copy(_glyphUv, cached.Uv, _glyphCount);
        Array.Copy(_glyphCol, cached.Col, _glyphCount);
        _glyphCache[_glyphCount] = cached;
        return cached;
    }

    private SolidArrays GetSolidArrays()
    {
        if (_solidCache.TryGetValue(_solidCount, out var cached))
        {
            Array.Copy(_solidPos, cached.Pos, _solidCount);
            Array.Copy(_solidCol, cached.Col, _solidCount);
            return cached;
        }

        cached = new SolidArrays
        {
            Pos = new SKPoint[_solidCount],
            Col = new SKColor[_solidCount],
        };
        Array.Copy(_solidPos, cached.Pos, _solidCount);
        Array.Copy(_solidCol, cached.Col, _solidCount);
        _solidCache[_solidCount] = cached;
        return cached;
    }
}
