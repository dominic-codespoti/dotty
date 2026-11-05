using System;
using Dotty.Rendering.Gpu;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Remembers the fallback chain built for the configured font model and builds a new one only when the
/// <see cref="FontOptions"/> change (value equality). Keeps the chain and its primary typeface object stable across
/// config reloads, zoom and resize, so reference-keyed consumers (glyph atlas service, scene composer row cache)
/// keep hitting. A font installed while Dotty runs is picked up on the next change of a <c>font.*</c> family option
/// or on restart. Not thread-safe: the window host calls it on the UI thread.
/// </summary>
internal sealed class FontChainResolutionCache
{
    private readonly Func<FontOptions, FontFallbackChain> _build;
    private FontFallbackChain? _chain;
    private FontOptions? _options;

    public FontChainResolutionCache(Func<FontOptions, FontFallbackChain> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
    }

    public FontFallbackChain Resolve(FontOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_chain is null || !options.Equals(_options))
        {
            _chain = _build(options);
            _options = options;
        }

        return _chain;
    }
}
