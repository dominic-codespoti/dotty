using System;
using System.Collections.Generic;
using System.Threading;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Sprite entries (decorations, procedural glyphs) share the A8 page with font glyphs, so they draw in the ordinary
/// glyph pass. They are keyed by <see cref="SpriteKey"/>, published with the glyph snapshot and dropped by
/// <see cref="Rebuild"/> like every other entry.
/// </summary>
public sealed partial class GlyphAtlas
{
    private sealed class SpriteMapSnapshot
    {
        public static readonly SpriteMapSnapshot Empty = new(new Dictionary<SpriteKey, GlyphInfo>());
        private readonly Dictionary<SpriteKey, GlyphInfo> _entries;

        public SpriteMapSnapshot(Dictionary<SpriteKey, GlyphInfo> entries) => _entries = entries;
        public bool TryGetValue(in SpriteKey key, out GlyphInfo info) => _entries.TryGetValue(key, out info);
    }

    private readonly Dictionary<SpriteKey, GlyphInfo> _spriteMap = new();
    private SpriteMapSnapshot _publishedSprites = SpriteMapSnapshot.Empty;
    private bool _spriteMapDirty;

    /// <summary>Number of sprite entries (diagnostics, tests).</summary>
    public int SpriteCount
    {
        get
        {
            lock (_lock)
                return _spriteMap.Count;
        }
    }

    /// <summary>
    /// Looks a sprite up: lock-free on the published snapshot, then on the working map (entries added this frame).
    /// <see cref="GlyphInfo.LeftBearing"/>/<see cref="GlyphInfo.TopBearing"/> hold the sprite's offset from the cell's
    /// top-left corner.
    /// </summary>
    public bool TryGetSprite(in SpriteKey key, out GlyphInfo info)
    {
        if (Volatile.Read(ref _publishedSprites).TryGetValue(key, out info))
            return true;
        lock (_lock)
            return _spriteMap.TryGetValue(key, out info);
    }

    /// <summary>
    /// Packs <paramref name="sprite"/> under <paramref name="key"/> (or returns the existing entry). Returns false
    /// when the page is full, which schedules a rebuild exactly like a glyph miss.
    /// </summary>
    public unsafe bool AddSprite(in SpriteKey key, SpriteBitmap sprite, out GlyphInfo info, out bool added)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        added = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_spriteMap.TryGetValue(key, out info))
                return true;
            if (sprite.Width > MaxGlyphDimension || sprite.Height > MaxGlyphDimension)
                return false;
            if (!TryPlace(sprite.Width, sprite.Height, color: false, out int x, out int y))
            {
                Volatile.Write(ref _needsRebuild, true);
                return false;
            }

            _canvas.Flush();
            byte* pixels = (byte*)_bitmap.GetPixels();
            int rowBytes = _bitmap.RowBytes;
            fixed (byte* source = sprite.Pixels)
            {
                for (int row = 0; row < sprite.Height; row++)
                {
                    Buffer.MemoryCopy(source + row * sprite.Width, pixels + (long)(y + row) * rowBytes + x,
                        sprite.Width, sprite.Width);
                }
            }
            _bitmap.NotifyPixelsChanged();

            info = new GlyphInfo(x, y, sprite.Width, sprite.Height, sprite.Width, 0f, sprite.Left, sprite.Top);
            _spriteMap[key] = info;
            Volatile.Write(ref _contentVersion, unchecked(_contentVersion + 1));
            _dirtyRegions.Add(new AtlasDirtyRegion(x, y, sprite.Width, sprite.Height));
            _spriteMapDirty = true;
            added = true;
            return true;
        }
    }

    /// <summary>Called under the lock by <see cref="PublishPendingGlyphs"/>.</summary>
    private void PublishPendingSpritesLocked()
    {
        if (!_spriteMapDirty)
            return;
        Volatile.Write(ref _publishedSprites, new SpriteMapSnapshot(new Dictionary<SpriteKey, GlyphInfo>(_spriteMap)));
        _spriteMapDirty = false;
    }

    /// <summary>Called under the lock by <see cref="Rebuild"/>.</summary>
    private void ClearSpritesLocked()
    {
        _spriteMap.Clear();
        _publishedSprites = SpriteMapSnapshot.Empty;
        _spriteMapDirty = false;
    }
}
