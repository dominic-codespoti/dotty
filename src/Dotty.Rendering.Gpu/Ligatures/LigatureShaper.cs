using System;
using System.Collections.Generic;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// <see cref="IRunShaper"/> over HarfBuzz: left-to-right Latin runs with <c>calt</c>, <c>liga</c> and <c>clig</c>
/// enabled, then the <c>font.features</c> settings (a later setting of a tag wins, so <c>-liga</c> turns it off).
/// HarfBuzz fonts are created once per typeface instance and reused; the buffer is reused across calls.
/// Not thread-safe.
/// </summary>
public sealed class LigatureShaper : IRunShaper, IDisposable
{
    private static readonly FontFeature[] DefaultFeatures = [new("calt", 1), new("liga", 1), new("clig", 1)];

    private readonly Dictionary<SKTypeface, FaceEntry> _faces = new(ReferenceEqualityComparer.Instance);
    private readonly HarfBuzzSharp.Buffer _buffer = new();
    private Feature[] _features = Array.Empty<Feature>();
    private FontFeature[] _featureSource = Array.Empty<FontFeature>();

    private sealed class FaceEntry : IDisposable
    {
        public required Face Face;
        public required HarfBuzzSharp.Font Font;
        public required int UnitsPerEm;
        public readonly ushort[] Nominal = new ushort[128];
        public readonly bool[] NominalKnown = new bool[128];

        public void Dispose()
        {
            Font.Dispose();
            Face.Dispose();
        }
    }

    public int Shape(ReadOnlySpan<char> text, SKTypeface face, float textSize, ReadOnlySpan<FontFeature> features, Span<ShapedGlyph> output)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (text.IsEmpty)
            return 0;
        var entry = GetFace(face);
        var hbFeatures = GetFeatures(features);

        _buffer.ClearContents();
        _buffer.AddUtf16(text);
        _buffer.Direction = Direction.LeftToRight;
        _buffer.Script = Script.Latin;
        _buffer.Language = Language.Default;
        entry.Font.Shape(_buffer, hbFeatures);

        var infos = _buffer.GetGlyphInfoSpan();
        var positions = _buffer.GetGlyphPositionSpan();
        float scale = textSize / entry.UnitsPerEm;
        int count = Math.Min(infos.Length, output.Length);
        for (int i = 0; i < count; i++)
        {
            output[i] = new ShapedGlyph(
                (ushort)infos[i].Codepoint,
                (int)infos[i].Cluster,
                positions[i].XAdvance * scale,
                positions[i].XOffset * scale,
                -positions[i].YOffset * scale);
        }
        return infos.Length;
    }

    public ushort GetNominalGlyph(SKTypeface face, char c)
    {
        ArgumentNullException.ThrowIfNull(face);
        var entry = GetFace(face);
        if (c < 128 && entry.NominalKnown[c])
            return entry.Nominal[c];
        ushort glyph = entry.Font.TryGetNominalGlyph(c, out uint g) ? (ushort)g : (ushort)0;
        if (c < 128)
        {
            entry.Nominal[c] = glyph;
            entry.NominalKnown[c] = true;
        }
        return glyph;
    }

    private FaceEntry GetFace(SKTypeface typeface)
    {
        if (_faces.TryGetValue(typeface, out var entry))
            return entry;
        using var blob = typeface.OpenStream(out int index).ToHarfBuzzBlob();
        var face = new Face(blob, index);
        var font = new HarfBuzzSharp.Font(face);
        int upem = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1000;
        font.SetScale(upem, upem);
        entry = new FaceEntry { Face = face, Font = font, UnitsPerEm = upem };
        _faces[typeface] = entry;
        return entry;
    }

    // Recomputed only when the caller hands a different feature array; steady-state frames allocate nothing.
    private Feature[] GetFeatures(ReadOnlySpan<FontFeature> features)
    {
        if (_features.Length == DefaultFeatures.Length + features.Length && features.SequenceEqual(_featureSource))
            return _features;
        var result = new Feature[DefaultFeatures.Length + features.Length];
        for (int i = 0; i < DefaultFeatures.Length; i++)
            result[i] = ToHarfBuzz(DefaultFeatures[i]);
        for (int i = 0; i < features.Length; i++)
            result[DefaultFeatures.Length + i] = ToHarfBuzz(features[i]);
        _featureSource = features.ToArray();
        _features = result;
        return result;
    }

    private static Feature ToHarfBuzz(FontFeature feature) => new(Tag.Parse(feature.Tag), feature.Value);

    public void Dispose()
    {
        foreach (var entry in _faces.Values)
            entry.Dispose();
        _faces.Clear();
        _buffer.Dispose();
    }
}
