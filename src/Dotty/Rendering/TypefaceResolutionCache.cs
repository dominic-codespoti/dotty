using System;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Remembers the typeface resolved for the configured family list and resolves again only when
/// that string changes. Keeps the typeface object stable across config reloads, zoom and resize,
/// so reference-keyed consumers (glyph atlas service, scene composer row cache) keep hitting.
/// A font installed while Dotty runs is picked up on the next change of <c>font.family</c> or on
/// restart. Not thread-safe: the window host calls it on the UI thread.
/// </summary>
internal sealed class TypefaceResolutionCache
{
    private readonly Func<string?, SKTypeface> _resolve;
    private SKTypeface? _typeface;
    private string? _familyList;

    public TypefaceResolutionCache(Func<string?, SKTypeface> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    public SKTypeface Resolve(string? familyList)
    {
        if (_typeface is null || !string.Equals(_familyList, familyList, StringComparison.Ordinal))
        {
            _typeface = _resolve(familyList);
            _familyList = familyList;
        }

        return _typeface;
    }
}
