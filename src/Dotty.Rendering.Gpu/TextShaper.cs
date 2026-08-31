using System;
using System.Collections.Generic;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Dotty.Rendering.Gpu;

public sealed class TextShaper : IDisposable
{
    private readonly Dictionary<SKTypeface, SKShaper> _shaperCache = new();

    public ShapedRun Shape(string text, SKTypeface typeface, float textSize)
    {
        if (!_shaperCache.TryGetValue(typeface, out var shaper))
        {
            shaper = new SKShaper(typeface);
            _shaperCache[typeface] = shaper;
        }

        using var font = new SKFont(typeface, textSize);

        var result = shaper.Shape(text, font);

        var indices = new ushort[result.Codepoints.Length];
        for (int i = 0; i < result.Codepoints.Length; i++)
            indices[i] = (ushort)result.Codepoints[i];

        return new ShapedRun(text, indices, result.Points, result.Width);
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
