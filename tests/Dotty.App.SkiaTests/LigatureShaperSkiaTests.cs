using System;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// W4-C11 against real HarfBuzz on DejaVu Sans Mono, which has no programming ligatures: shaping must keep one
/// glyph per character with increasing clusters, so every cell maps to its nominal glyph.
/// </summary>
public sealed class LigatureShaperSkiaTests
{
    private const string DejaVu = "DejaVu Sans Mono";

    [Theory]
    [InlineData("a -> b")]
    [InlineData("x != y && z === w")]
    [InlineData("<=> |> :: /* */ www")]
    public void DejaVuMonoKeepsOneGlyphPerCell(string text)
    {
        using var typeface = SKTypeface.FromFamilyName(DejaVu);
        if (typeface == null || !string.Equals(typeface.FamilyName, DejaVu, StringComparison.OrdinalIgnoreCase))
            Assert.Skip($"{DejaVu} is not installed");
        using var shaper = new LigatureShaper();
        var glyphs = new ShapedGlyph[64];
        int count = shaper.Shape(text, typeface!, 14f, [new FontFeature("calt", 1)], glyphs);

        Assert.Equal(text.Length, count);
        var nominal = new ushort[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            Assert.Equal(i, glyphs[i].Cluster);
            nominal[i] = shaper.GetNominalGlyph(typeface!, text[i]);
            Assert.Equal(nominal[i], glyphs[i].GlyphId);
            Assert.NotEqual(0, nominal[i]);
            Assert.True(glyphs[i].XAdvance > 0);
        }
        // Monospace: every advance is the same cell width.
        Assert.All(glyphs[..count], g => Assert.Equal(glyphs[0].XAdvance, g.XAdvance, 3));

        var cells = new LigatureCell[text.Length];
        Assert.True(ClusterCellMapper.Map(glyphs.AsSpan(0, count), nominal, cells));
        Assert.All(cells, c => Assert.Equal(LigatureCellKind.None, c.Kind));
    }
}
