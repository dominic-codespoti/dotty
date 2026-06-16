using System;
using SkiaSharp;

namespace Dotty.App.Rendering;

public readonly struct ShapedRun
{
    public string Text { get; }
    public ushort[] GlyphIndices { get; }
    public SKPoint[] Positions { get; }
    public float TotalAdvance { get; }

    public ShapedRun(string text, ushort[] glyphIndices, SKPoint[] positions, float totalAdvance)
    {
        Text = text;
        GlyphIndices = glyphIndices;
        Positions = positions;
        TotalAdvance = totalAdvance;
    }

    public bool IsEmpty => GlyphIndices.Length == 0;
}
