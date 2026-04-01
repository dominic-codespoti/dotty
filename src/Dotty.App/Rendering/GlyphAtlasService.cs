using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Shared glyph atlas service that manages font caches across all terminal instances.
/// This reduces memory usage by sharing glyph atlases between tabs instead of duplicating them.
/// </summary>
public static class GlyphAtlasService
{
    // Key: (TypefaceName, TextSize, rasterizationOptions hash)
    private static readonly Dictionary<string, GlyphAtlas> _atlases = new();
    private static readonly object _lock = new();
    
    /// <summary>
    /// Gets or creates a shared glyph atlas for the given font configuration.
    /// Multiple terminals with the same font settings will share the same atlas.
    /// </summary>
    public static GlyphAtlas GetOrCreateAtlas(SKTypeface typeface, float textSize, GlyphRasterizationOptions options)
    {
        if (typeface == null) typeface = SKTypeface.Default;
        if (textSize <= 0) textSize = 12f;
        
        var key = GenerateKey(typeface, textSize, options);
        
        lock (_lock)
        {
            if (!_atlases.TryGetValue(key, out var atlas))
            {
                atlas = new GlyphAtlas(typeface, textSize, options);
                atlas.PreloadCommonGlyphs();
                _atlases[key] = atlas;
            }
            
            return atlas;
        }
    }
    
    /// <summary>
    /// Clears all shared atlases and releases their memory.
    /// Call this when changing global font settings or on application shutdown.
    /// </summary>
    public static void ClearAllAtlases()
    {
        lock (_lock)
        {
            foreach (var atlas in _atlases.Values)
            {
                try { atlas.Dispose(); } catch { }
            }
            _atlases.Clear();
        }
    }
    
    /// <summary>
    /// Returns the number of currently cached atlases.
    /// </summary>
    public static int AtlasCount
    {
        get
        {
            lock (_lock) return _atlases.Count;
        }
    }
    
    private static string GenerateKey(SKTypeface typeface, float textSize, GlyphRasterizationOptions options)
    {
        // Round text size to avoid creating separate atlases for nearly identical sizes
        var roundedSize = Math.Round(textSize, 1);
        // Use FamilyName as Name property doesn't exist
        var familyName = typeface?.FamilyName ?? "Default";
        return $"{familyName}:{roundedSize:F1}:{options.GetHashCode()}";
    }
}
