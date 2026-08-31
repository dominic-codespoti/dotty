using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Maintains an ordered fallback chain of <see cref="SKTypeface"/> instances (e.g. Primary Monospace
/// -> Symbols Nerd Font Mono -> Noto Sans CJK / Noto Color Emoji / system fallbacks).
/// Resolves the appropriate typeface for a grapheme or codepoint to ensure seamless multi-font fallback.
/// </summary>
public sealed class FontFallbackChain : IDisposable
{
    private static readonly string[] MonospaceFallbackNames =
    {
        "JetBrains Mono",
        "JetBrainsMono Nerd Font Mono",
        "JetBrainsMono NF",
        "Fira Code",
        "FiraCode Nerd Font",
        "Cascadia Code",
        "Cascadia Mono",
        "SF Mono",
        "Ubuntu Mono",
        "Consolas",
        "Liberation Mono",
        "DejaVu Sans Mono",
        "Courier New",
        "monospace"
    };

    private static readonly string[] SymbolFallbackNames =
    {
        "Symbols Nerd Font Mono",
        "Symbols Nerd Font",
        "Nerd Font Symbols",
        "Powerline Symbols"
    };

    private static readonly string[] CjkFallbackNames =
    {
        "Noto Sans CJK SC",
        "Noto Sans CJK TC",
        "Noto Sans CJK JP",
        "Noto Sans CJK KR",
        "Noto Sans SC",
        "Noto Sans TC",
        "Noto Sans JP",
        "Noto Sans KR",
        "Microsoft YaHei",
        "PingFang SC",
        "WenQuanYi Micro Hei"
    };

    private static readonly string[] EmojiFallbackNames =
    {
        "Noto Color Emoji",
        "Apple Color Emoji",
        "Segoe UI Emoji",
        "Twitter Color Emoji",
        "EmojiOne Color"
    };

    private readonly List<SKTypeface> _typefaces = new();
    private readonly bool _ownsTypefaces;
    private readonly Dictionary<string, SKTypeface> _resolutionCache = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the primary (index 0) typeface in the chain.
    /// </summary>
    public SKTypeface PrimaryTypeface => _typefaces.Count > 0 ? _typefaces[0] : SKTypeface.Default;

    /// <summary>
    /// Gets the list of typefaces in the chain.
    /// </summary>
    public IReadOnlyList<SKTypeface> Typefaces => _typefaces;

    /// <summary>
    /// Initializes a new fallback chain with the specified primary typeface and optional fallback typefaces.
    /// </summary>
    public FontFallbackChain(SKTypeface primaryTypeface, IEnumerable<SKTypeface>? fallbacks = null, bool ownsTypefaces = false)
    {
        _ownsTypefaces = ownsTypefaces;
        var primary = primaryTypeface ?? SKTypeface.Default;
        _typefaces.Add(primary);

        if (fallbacks != null)
        {
            foreach (var fb in fallbacks)
            {
                if (fb != null && !ContainsTypeface(fb))
                {
                    _typefaces.Add(fb);
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new fallback chain with a list of typefaces. The first typeface is treated as primary.
    /// </summary>
    public FontFallbackChain(IEnumerable<SKTypeface> typefaces, bool ownsTypefaces = false)
    {
        _ownsTypefaces = ownsTypefaces;
        if (typefaces != null)
        {
            foreach (var tf in typefaces)
            {
                if (tf != null && !ContainsTypeface(tf))
                {
                    _typefaces.Add(tf);
                }
            }
        }

        if (_typefaces.Count == 0)
        {
            _typefaces.Add(SKTypeface.Default);
        }
    }

    private bool ContainsTypeface(SKTypeface tf)
    {
        for (int i = 0; i < _typefaces.Count; i++)
        {
            if (ReferenceEquals(_typefaces[i], tf) ||
                string.Equals(_typefaces[i].FamilyName, tf.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates a default fallback chain rooted at the given primary typeface, automatically
    /// discovering available system fallbacks (Nerd Font Symbols, CJK, Emoji, and generic monospace).
    /// </summary>
    public static FontFallbackChain CreateDefault(SKTypeface? primaryTypeface = null)
    {
        var primary = primaryTypeface ?? SKTypeface.Default;
        var fallbacks = new List<SKTypeface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(primary.FamilyName))
        {
            seen.Add(primary.FamilyName);
        }

        void AddFamilies(string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (!seen.Add(name)) continue;

                try
                {
                    var matched = SKFontManager.Default.MatchFamily(name);
                    if (matched != null)
                    {
                        if (!seen.Add(matched.FamilyName))
                        {
                            // Already have a typeface with this family name
                            continue;
                        }
                        fallbacks.Add(matched);
                    }
                }
                catch
                {
                    // Ignore font discovery errors
                }
            }
        }

        // 1. Symbol and Nerd Font fallbacks (for icons, Powerline, git branch glyphs)
        AddFamilies(SymbolFallbackNames);

        // 2. Monospace coding fallbacks
        AddFamilies(MonospaceFallbackNames);

        // 3. CJK font fallbacks
        AddFamilies(CjkFallbackNames);

        // 4. Emoji font fallbacks
        AddFamilies(EmojiFallbackNames);

        return new FontFallbackChain(primary, fallbacks);
    }

    /// <summary>
    /// Adds a fallback typeface to the end of the chain.
    /// </summary>
    public void AddFallback(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        lock (_lock)
        {
            if (!ContainsTypeface(typeface))
            {
                _typefaces.Add(typeface);
                _resolutionCache.Clear();
            }
        }
    }

    /// <summary>
    /// Resolves the matching <see cref="SKTypeface"/> for the given grapheme.
    /// Checks if the primary typeface contains the glyph (or measures > 0 without fallback missing character);
    /// if not, checks fallback typefaces in order, and finally queries <see cref="SKFontManager.Default.MatchCharacter"/>.
    /// </summary>
    public SKTypeface ResolveTypefaceForGrapheme(string grapheme, bool bold = false)
    {
        if (string.IsNullOrEmpty(grapheme))
        {
            return PrimaryTypeface;
        }

        lock (_lock)
        {
            if (_resolutionCache.TryGetValue(grapheme, out var cached))
            {
                return cached;
            }

            var resolved = ResolveTypefaceCore(grapheme);
            _resolutionCache[grapheme] = resolved;
            return resolved;
        }
    }

    private SKTypeface ResolveTypefaceCore(string grapheme)
    {
        int firstRune = GetFirstCodepoint(grapheme);

        // 1. Check primary typeface
        if (_typefaces.Count > 0 && TypefaceContainsGrapheme(_typefaces[0], grapheme, firstRune))
        {
            return _typefaces[0];
        }

        // 2. Check fallback typefaces in order
        for (int i = 1; i < _typefaces.Count; i++)
        {
            if (TypefaceContainsGrapheme(_typefaces[i], grapheme, firstRune))
            {
                return _typefaces[i];
            }
        }

        // 3. Dynamic system fallback query for the rune if not found in explicit chain
        if (firstRune > 0)
        {
            try
            {
                var systemMatch = SKFontManager.Default.MatchCharacter(firstRune);
                if (systemMatch != null)
                {
                    if (!ContainsTypeface(systemMatch))
                    {
                        _typefaces.Add(systemMatch);
                    }
                    return systemMatch;
                }
            }
            catch
            {
                // Fall back to primary if system match fails
            }
        }

        return PrimaryTypeface;
    }

    private static int GetFirstCodepoint(string grapheme)
    {
        if (string.IsNullOrEmpty(grapheme)) return 0;
        if (System.Text.Rune.TryGetRuneAt(grapheme, 0, out var rune))
        {
            return rune.Value;
        }
        return (int)grapheme[0];
    }

    private static bool TypefaceContainsGrapheme(SKTypeface typeface, string grapheme, int firstRune)
    {
        if (typeface == null) return false;

        // Fast path for single character / single codepoint using ContainsGlyph
        if (firstRune > 0)
        {
#pragma warning disable CS0618
            if (!typeface.ContainsGlyph(firstRune))
            {
                return false;
            }
#pragma warning restore CS0618
        }

        // For multi-char graphemes or verification, ensure all codepoints exist in typeface
        for (int i = 0; i < grapheme.Length;)
        {
            if (!System.Text.Rune.TryGetRuneAt(grapheme, i, out var rune))
            {
                i++;
                continue;
            }
            int cp = rune.Value;
            i += rune.Utf16SequenceLength;

#pragma warning disable CS0618
            if (!typeface.ContainsGlyph(cp))
            {
                return false;
            }
#pragma warning restore CS0618
        }

        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _resolutionCache.Clear();

            if (_ownsTypefaces)
            {
                for (int i = 0; i < _typefaces.Count; i++)
                {
                    _typefaces[i].Dispose();
                }
            }
            _typefaces.Clear();
        }
    }
}
