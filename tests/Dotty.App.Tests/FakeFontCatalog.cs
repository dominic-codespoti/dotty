using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Rendering.Gpu;

namespace Dotty.App.Tests;

/// <summary>A face with an explicit coverage set; no Skia involved.</summary>
internal sealed class FakeFace : IFontFace
{
    private readonly HashSet<int> _coverage;

    public FakeFace(string family, IEnumerable<int> coverage, int weight = 400, bool italic = false, bool color = false)
    {
        FamilyName = family;
        _coverage = new HashSet<int>(coverage);
        Weight = weight;
        IsItalic = italic;
        IsColor = color;
    }

    public string FamilyName { get; }
    public int Weight { get; }
    public bool IsItalic { get; }
    public bool IsColor { get; }
    public bool HasGlyph(int codepoint) => _coverage.Contains(codepoint);
    public override string ToString() => $"{FamilyName} {Weight}{(IsItalic ? " italic" : "")}";

    /// <summary>Every scalar of every string.</summary>
    public static IEnumerable<int> Scalars(params string[] texts) =>
        texts.SelectMany(t => t.EnumerateRunes().Select(r => r.Value));

    public static IEnumerable<int> Range(int first, int last) => Enumerable.Range(first, last - first + 1);
}

/// <summary>
/// Installed faces by family; <see cref="MatchFamily"/> returns the face of the family whose weight is closest to the
/// request among faces with the requested slant (else any slant), like fontconfig. Unknown families return null.
/// </summary>
internal sealed class FakeFontCatalog : IFontCatalog
{
    private readonly List<FakeFace> _faces = new();
    private readonly List<FakeFace> _system = new();

    public FakeFontCatalog(params FakeFace[] faces) => _faces.AddRange(faces);

    public List<string> FamilyQueries { get; } = new();
    public int CharacterQueries { get; private set; }

    public IFontFace DefaultFace { get; set; } = new FakeFace("Default", FakeFace.Range(0x20, 0x7E));

    public FakeFontCatalog Add(FakeFace face)
    {
        _faces.Add(face);
        return this;
    }

    /// <summary>Faces only <see cref="MatchCharacter"/> finds (system fallback).</summary>
    public FakeFontCatalog AddSystem(FakeFace face)
    {
        _system.Add(face);
        return this;
    }

    public IFontFace? MatchFamily(string family, FontFaceStyle style)
    {
        FamilyQueries.Add(family);
        var faces = _faces.Where(f => string.Equals(f.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (faces.Count == 0)
            return null;
        var slanted = faces.Where(f => f.IsItalic == style.Italic).ToList();
        if (slanted.Count > 0)
            faces = slanted;
        return faces.OrderBy(f => Math.Abs(f.Weight - style.Weight)).First();
    }

    public IFontFace? MatchCharacter(int codepoint, FontFaceStyle style)
    {
        CharacterQueries++;
        return _system.FirstOrDefault(f => f.HasGlyph(codepoint));
    }
}
