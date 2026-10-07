using System;
using System.Collections.Generic;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Dotty.Rendering.Gpu;

public sealed class TextShaper : IDisposable
{
    private readonly Dictionary<SKTypeface, SKShaper> _shaperCache = new();

    public ShapedRun Shape(string text, SKTypeface typeface, float textSize)
        => Shape(text, typeface, textSize, null);

    /// <summary>
    /// Shapes with optional OpenType features. Features apply per call; the
    /// shaper cache stays keyed by typeface only.
    /// </summary>
    public ShapedRun Shape(string text, SKTypeface typeface, float textSize, HarfBuzzSharp.Feature[]? features)
    {
        if (!_shaperCache.TryGetValue(typeface, out var shaper))
        {
            shaper = new SKShaper(typeface);
            _shaperCache[typeface] = shaper;
        }

        using var font = new SKFont(typeface, textSize);

        SKShaper.Result result;
        if (features is { Length: > 0 })
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf8(text);
            buffer.GuessSegmentProperties();
            using var hbFont = new HarfBuzzSharp.Font(GetFace(typeface));
            hbFont.Shape(buffer, features);
            result = shaper.Shape(buffer, font);
        }
        else
        {
            result = shaper.Shape(text, font);
        }

        var indices = new ushort[result.Codepoints.Length];
        for (int i = 0; i < result.Codepoints.Length; i++)
            indices[i] = (ushort)result.Codepoints[i];

        return new ShapedRun(text, indices, result.Points, result.Width);
    }

    private static HarfBuzzSharp.Face GetFace(SKTypeface typeface)
    {
        using var asset = typeface.OpenStream(out _);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var memory = new System.IO.MemoryStream();
            unsafe
            {
                fixed (byte* ptr = chunk)
                {
                    int n;
                    while ((n = asset.Read((IntPtr)ptr, chunk.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                            memory.WriteByte(ptr[i]);
                    }
                }
            }
            var blob = HarfBuzzSharp.Blob.FromStream(new System.IO.MemoryStream(memory.ToArray(), writable: false));
            return new HarfBuzzSharp.Face(blob, 0);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public void ClearFontCache()
    {
        foreach (var shaper in _shaperCache.Values)
            shaper.Dispose();
        _shaperCache.Clear();
    }

    public void Dispose()
    {
        ClearFontCache();
    }
}
