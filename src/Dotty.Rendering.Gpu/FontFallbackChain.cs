using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>Result of resolving a grapheme cluster and style to a face.</summary>
public readonly struct FontResolution
{
    public FontResolution(IFontFace face, bool isFallback, bool syntheticBold, bool syntheticItalic)
    {
        Face = face;
        IsFallback = isFallback;
        SyntheticBold = syntheticBold;
        SyntheticItalic = syntheticItalic;
    }

    /// <summary>The face to draw with (the styled face when one is installed).</summary>
    public IFontFace Face { get; }

    /// <summary>False when the face belongs to the primary (first installed) family; true for every other face.</summary>
    public bool IsFallback { get; }

    /// <summary>Bold was requested but no heavier face of the family is installed: embolden the outline.</summary>
    public bool SyntheticBold { get; }

    /// <summary>Italic was requested but no italic/oblique face of the family is installed: skew the outline.</summary>
    public bool SyntheticItalic { get; }

    /// <summary>The Skia typeface of <see cref="Face"/>, or null for a non-Skia (test) face.</summary>
    public SKTypeface? Typeface => (Face as SkiaFontFace)?.Typeface;
}

/// <summary>
/// Ordered font fallback chain. The configured <c>font.family</c> list is the fallback order (Ghostty semantics):
/// every installed family of the list, in order, followed by a built-in tail of symbol, CJK and emoji families,
/// followed by at most <see cref="MaxDynamicFaces"/> faces the system matched for otherwise uncovered scalars.
/// <c>font.symbolMap</c> faces are tried first. Face choice per cluster is <see cref="ClusterFontSelector"/>; bold and
/// italic then use the family's real styled face when installed (for the primary family, the configured
/// <c>font.boldFamily</c>/<c>italicFamily</c>/<c>boldItalicFamily</c> first) and fall back to synthetic emboldening
/// or skew. Every cache is bounded.
/// </summary>
public sealed class FontFallbackChain : IDisposable
{
    /// <summary>Upper bound of faces appended at runtime from system character matching.</summary>
    public const int MaxDynamicFaces = 16;

    /// <summary>Entry bound of the per-(cluster, style) resolution cache.</summary>
    public const int ResolutionCacheCapacity = 4096;

    /// <summary>Entry bound of the symbol-map family cache.</summary>
    public const int FamilyCacheCapacity = 64;

    /// <summary>Families appended after the configured list; skipped when not installed.</summary>
    public static IReadOnlyList<string> TailFamilies { get; } = new[]
    {
        "Symbols Nerd Font Mono",
        "Symbols Nerd Font",
        "Noto Sans CJK SC",
        "Noto Sans CJK JP",
        "Noto Sans CJK KR",
        "Noto Sans CJK TC",
        "Noto Sans SC",
        "Microsoft YaHei",
        "PingFang SC",
        "WenQuanYi Micro Hei",
        "Noto Color Emoji",
        "Apple Color Emoji",
        "Segoe UI Emoji",
        "Noto Sans Symbols 2",
        "Noto Sans Symbols",
    };

    private readonly IFontCatalog _catalog;
    private readonly List<IFontFace> _faces = new();
    private readonly int _configuredCount;
    private readonly LruCache<(string Grapheme, GlyphStyle Style), FontResolution> _resolutions =
        new(ResolutionCacheCapacity);
    private readonly Dictionary<(IFontFace Face, GlyphStyle Style), IFontFace> _styledFaces = new();
    private readonly LruCache<string, IFontFace?> _familyFaces = new(FamilyCacheCapacity, StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private SymbolMap? _symbolMap;

    /// <summary>Builds the chain for <paramref name="options"/>: configured families, then <see cref="TailFamilies"/>.</summary>
    public FontFallbackChain(FontOptions options, IFontCatalog catalog)
        : this(options, catalog, includeTail: true)
    {
    }

    public FontFallbackChain(FontOptions options, IFontCatalog catalog, bool includeTail)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        Options = options;
        _catalog = catalog;
        var regular = options.RegularStyle;
        foreach (string family in options.Families)
            AddConfigured(catalog.MatchFamily(family, regular));
        if (includeTail)
            foreach (string family in TailFamilies)
                AddConfigured(catalog.MatchFamily(family, regular));
        if (_faces.Count == 0)
            _faces.Add(catalog.DefaultFace);
        _configuredCount = _faces.Count;
    }

    /// <summary>A chain over explicit typefaces (primary first) without the built-in tail.</summary>
    public FontFallbackChain(SKTypeface primaryTypeface, IEnumerable<SKTypeface>? fallbacks = null)
    {
        Options = FontOptions.Default;
        _catalog = SkiaFontCatalog.Shared;
        AddConfigured(SkiaFontFace.Wrap(primaryTypeface ?? SKTypeface.Default));
        if (fallbacks != null)
            foreach (var typeface in fallbacks)
                if (typeface != null)
                    AddConfigured(SkiaFontFace.Wrap(typeface));
        _configuredCount = _faces.Count;
    }

    /// <summary>A chain rooted at <paramref name="primaryTypeface"/> followed by the installed <see cref="TailFamilies"/>.</summary>
    public static FontFallbackChain CreateDefault(SKTypeface? primaryTypeface = null)
    {
        var catalog = SkiaFontCatalog.Shared;
        var fallbacks = new List<SKTypeface>();
        foreach (string family in TailFamilies)
            if (catalog.MatchFamily(family, FontFaceStyle.Regular) is SkiaFontFace face)
                fallbacks.Add(face.Typeface);
        return new FontFallbackChain(primaryTypeface ?? SKTypeface.Default, fallbacks);
    }

    public FontOptions Options { get; }

    /// <summary>Parsed <c>font.features</c>, kept for shaping.</summary>
    public IReadOnlyList<FontFeature> Features => Options.Features;

    public IFontFace PrimaryFace => _faces[0];

    /// <summary>The Skia typeface of <see cref="PrimaryFace"/> (<see cref="SKTypeface.Default"/> for a non-Skia face).</summary>
    public SKTypeface PrimaryTypeface => (PrimaryFace as SkiaFontFace)?.Typeface ?? SKTypeface.Default;

    /// <summary>Snapshot of the chain in order (configured, tail, then dynamically matched faces).</summary>
    public IReadOnlyList<IFontFace> Faces
    {
        get { lock (_lock) return _faces.ToArray(); }
    }

    public int DynamicFaceCount
    {
        get { lock (_lock) return _faces.Count - _configuredCount; }
    }

    public int ResolutionCacheCount
    {
        get { lock (_lock) return _resolutions.Count; }
    }

    /// <summary>
    /// Optional per-range overrides checked before the chain. Replacing the map with different entries clears the
    /// resolution cache and raises <see cref="SymbolMapChanged"/> so atlases drop glyphs drawn with the old faces.
    /// </summary>
    public SymbolMap? SymbolMap
    {
        get { lock (_lock) return _symbolMap; }
        set
        {
            lock (_lock)
            {
                if (_symbolMap?.HasSameEntries(value) ?? value == null)
                    return;
                _symbolMap = value;
                _resolutions.Clear();
            }
            SymbolMapChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SymbolMapChanged;

    /// <summary>Resolves the face, fallback classification and synthetic flags for a cluster in a style.</summary>
    public FontResolution Resolve(string grapheme, GlyphStyle style = GlyphStyle.Regular)
    {
        style &= GlyphStyle.Bold | GlyphStyle.Italic;
        if (string.IsNullOrEmpty(grapheme))
            return new FontResolution(PrimaryFace, false, false, false);
        lock (_lock)
        {
            if (_resolutions.TryGetValue((grapheme, style), out var cached))
                return cached;
            var resolved = ResolveCore(grapheme, style);
            _resolutions.Set((grapheme, style), resolved);
            return resolved;
        }
    }

    private FontResolution ResolveCore(string grapheme, GlyphStyle style)
    {
        var candidates = new List<IFontFace>(_faces.Count + 2);
        int baseCp = ClusterFontSelector.GetCoverageBase(grapheme);
        if (_symbolMap != null && baseCp > 0 && _symbolMap.TryResolveFamilies(baseCp, out var families))
        {
            foreach (string family in families)
                if (FamilyFace(family) is { } face && !candidates.Contains(face))
                    candidates.Add(face);
        }
        int symbolCount = candidates.Count;
        candidates.AddRange(_faces);

        int index = ClusterFontSelector.Select(grapheme, candidates, symbolCount);
        IFontFace regular;
        if (index >= 0)
        {
            regular = candidates[index];
        }
        else
        {
            regular = MatchDynamic(grapheme, baseCp) ?? PrimaryFace;
        }

        bool isFallback = !IsPrimaryFamily(regular);
        if (style == GlyphStyle.Regular)
            return new FontResolution(regular, isFallback, false, false);

        var styled = StyledFace(regular, style, isFallback);
        if (!ReferenceEquals(styled, regular) && !ClusterFontSelector.Covers(styled, grapheme))
            styled = regular;
        bool syntheticBold = (style & GlyphStyle.Bold) != 0 && styled.Weight <= regular.Weight && styled.Weight < Options.BoldWeight;
        bool syntheticItalic = (style & GlyphStyle.Italic) != 0 && !styled.IsItalic;
        return new FontResolution(styled, isFallback, syntheticBold, syntheticItalic);
    }

    private bool IsPrimaryFamily(IFontFace face) =>
        ReferenceEquals(face, _faces[0]) || string.Equals(face.FamilyName, _faces[0].FamilyName, StringComparison.OrdinalIgnoreCase);

    private IFontFace? MatchDynamic(string grapheme, int baseCp)
    {
        if (baseCp <= 0)
            return null;
        var match = _catalog.MatchCharacter(baseCp, Options.RegularStyle);
        if (match == null || !ClusterFontSelector.Covers(match, grapheme))
            return null;
        foreach (var face in _faces)
            if (ReferenceEquals(face, match) || SameFace(face, match))
                return face;
        if (_faces.Count - _configuredCount < MaxDynamicFaces)
            _faces.Add(match);
        return match;
    }

    private IFontFace StyledFace(IFontFace regular, GlyphStyle style, bool isFallback)
    {
        if (_styledFaces.TryGetValue((regular, style), out var cached))
            return cached;
        var want = Options.StyleFor(style);
        IFontFace? styled = null;
        if (!isFallback)
        {
            foreach (string family in Options.FamiliesFor(style))
                if ((styled = _catalog.MatchFamily(family, want)) != null)
                    break;
        }
        if (styled == null)
        {
            var match = _catalog.MatchFamily(regular.FamilyName, want);
            if (match != null
                && string.Equals(match.FamilyName, regular.FamilyName, StringComparison.OrdinalIgnoreCase)
                && Improves(match, regular, want))
            {
                styled = match;
            }
        }
        styled ??= regular;
        // Bounded by the chain: at most three styles per configured, dynamic or symbol-map face.
        _styledFaces[(regular, style)] = styled;
        return styled;
    }

    // A real styled face is heavier when bold is wanted or slanted when italic is wanted, without losing the
    // other requested attribute that the regular face already had.
    private static bool Improves(IFontFace candidate, IFontFace regular, FontFaceStyle want)
    {
        bool heavier = want.Weight > regular.Weight && candidate.Weight > regular.Weight;
        bool slanted = want.Italic && candidate.IsItalic && !regular.IsItalic;
        return heavier || slanted;
    }

    private static bool SameFace(IFontFace a, IFontFace b) =>
        string.Equals(a.FamilyName, b.FamilyName, StringComparison.OrdinalIgnoreCase)
        && a.Weight == b.Weight && a.IsItalic == b.IsItalic;

    private IFontFace? FamilyFace(string family)
    {
        if (_familyFaces.TryGetValue(family, out var face))
            return face;
        face = _catalog.MatchFamily(family, Options.RegularStyle);
        _familyFaces.Set(family, face);
        return face;
    }

    private void AddConfigured(IFontFace? face)
    {
        if (face == null)
            return;
        foreach (var existing in _faces)
            if (ReferenceEquals(existing, face) || SameFace(existing, face))
                return;
        _faces.Add(face);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _resolutions.Clear();
            _styledFaces.Clear();
            _familyFaces.Clear();
        }
    }
}
