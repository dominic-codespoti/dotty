namespace Dotty.Rendering.Gpu;

/// <summary>Weight (CSS/OpenType 1-1000) and slant of a requested or matched face.</summary>
public readonly record struct FontFaceStyle(int Weight, bool Italic)
{
    public const int RegularWeight = 400;
    public const int BoldWeight = 700;

    public static FontFaceStyle Regular => new(RegularWeight, false);
}

/// <summary>Glyph coverage of one face: what <see cref="ClusterFontSelector"/> needs to pick a face.</summary>
public interface IGlyphCoverage
{
    /// <summary>Whether the face's character map has a glyph for the scalar.</summary>
    bool HasGlyph(int codepoint);

    /// <summary>Whether the face carries colour glyph tables (an emoji face).</summary>
    bool IsColor { get; }
}

/// <summary>One installed face as seen by the fallback chain.</summary>
public interface IFontFace : IGlyphCoverage
{
    string FamilyName { get; }
    int Weight { get; }
    bool IsItalic { get; }
}

/// <summary>
/// Font lookup seam of <see cref="FontFallbackChain"/>: the production catalog is
/// <see cref="SkiaFontCatalog"/>; tests use fakes so chain logic does not depend on installed fonts.
/// Lookups return the same face object for the same installed face.
/// </summary>
public interface IFontCatalog
{
    /// <summary>The face used when no configured family is installed.</summary>
    IFontFace DefaultFace { get; }

    /// <summary>The installed face of <paramref name="family"/> closest to <paramref name="style"/>, or null when the family is not installed.</summary>
    IFontFace? MatchFamily(string family, FontFaceStyle style);

    /// <summary>A system face that has a glyph for <paramref name="codepoint"/>, or null.</summary>
    IFontFace? MatchCharacter(int codepoint, FontFaceStyle style);
}
