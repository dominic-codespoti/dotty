using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

// GlyphAtlasService is process-wide shared state. Run these tests alone so
// reference counts and eviction are deterministic.
public sealed class GlyphAtlasTests
{
    public GlyphAtlasTests()
    {
        GlyphAtlasService.ClearAllAtlases();
    }

    private static SKTypeface Font => SKTypeface.Default;

    private static bool RegionHasCoverage(GlyphAtlas atlas, GlyphInfo info)
    {
        var bitmap = atlas.AtlasBitmap; // shared; caller must NOT dispose
        unsafe
        {
            var p = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            for (int y = info.Y; y < info.Y + info.Height; y++)
            {
                var row = p + (nint)y * rowBytes;
                for (int x = info.X; x < info.X + info.Width; x++)
                {
                    if (row[x] != 0) return true;
                }
            }
        }
        return false;
    }

    [Fact]
    public void EnsureGlyph_RasterizesCoverage_WithPlacementMetadata()
    {
        using var atlas = new GlyphAtlas(Font, 16f);
        var key = new GlyphKey("W", Font, 16f, bold: false);

        Assert.True(atlas.EnsureGlyph(key, out var info));
        Assert.True(info.Width > 0 && info.Height > 0);
        Assert.True(info.Advance > 0);
        Assert.True(info.BaselineOffset > 0, "baseline offset should be positive (cell top -> baseline)");
        Assert.True(RegionHasCoverage(atlas, info), "expected non-zero coverage in the placed region");
    }

    [Fact]
    public void TryGetGlyph_Missing_ReturnsFalse()
    {
        using var atlas = new GlyphAtlas(Font, 16f);
        Assert.False(atlas.TryGetGlyph(new GlyphKey("ZZZ", Font, 16f, false), out _));
    }

    [Fact]
    public void SameKey_ReturnsSameEntry()
    {
        using var atlas = new GlyphAtlas(Font, 16f);
        var key = new GlyphKey("=>", Font, 16f, bold: false);
        Assert.True(atlas.EnsureGlyph(key, out var first));
        Assert.True(atlas.EnsureGlyph(key, out var second));
        Assert.Equal(first, second);
        Assert.Equal(1, atlas.EntryCount);
    }

    [Fact]
    public void Key_HasNoColorComponent_AndComparesByTypefaceSizeBold()
    {
        var a = new GlyphKey("W", Font, 16f, bold: false);
        var b = new GlyphKey("W", Font, 16f, bold: false);
        var c = new GlyphKey("W", Font, 17f, bold: false);
        var d = new GlyphKey("W", Font, 16f, bold: true);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        // The API surface has no foreground color — the atlas is coverage-only.
        Assert.Equal(4, typeof(GlyphKey).GetFields().Length);
    }

    [Fact]
    public void Bold_ProducesDistinctEntry_WithBolderCoverage()
    {
        using var atlas = new GlyphAtlas(Font, 16f);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("W", Font, 16f, bold: false), out var plain));
        Assert.True(atlas.EnsureGlyph(new GlyphKey("W", Font, 16f, bold: true), out var bold));

        Assert.True(plain.X != bold.X || plain.Y != bold.Y, "bold must be a distinct atlas entry");
        Assert.Equal(2, atlas.EntryCount);
        // Synthetic stroke must change the rasterization (the deleted atlas
        // keyed on Bold without applying it — this asserts it now applies).
        int plainPixels = CountCoverage(atlas, plain);
        int boldPixels = CountCoverage(atlas, bold);
        Assert.True(plainPixels != boldPixels,
            $"bold ({boldPixels}) rasterization must differ from plain ({plainPixels})");
    }

    private static int CountCoverage(GlyphAtlas atlas, GlyphInfo info)
    {
        var bitmap = atlas.AtlasBitmap; // shared; caller must NOT dispose
        int count = 0;
        unsafe
        {
            var p = (byte*)bitmap.GetPixels();
            int rowBytes = bitmap.RowBytes;
            for (int y = info.Y; y < info.Y + info.Height; y++)
            {
                var row = p + (nint)y * rowBytes;
                for (int x = info.X; x < info.X + info.Width; x++)
                {
                    if (row[x] != 0) count++;
                }
            }
        }
        return count;
    }

    [Fact]
    public void WideGlyph_AdvanceIsWiderThanNarrow()
    {
        // The default typeface usually lacks CJK; resolve one that contains 世.
        // Not disposed: fonts from SKFontManager may alias shared native font
        // state, and disposing under concurrent SkiaSharp tests aborts (SIGABRT).
        var cjkFont = SKFontManager.Default.MatchCharacter(0x4E16);
        if (cjkFont == null)
        {
            Assert.Skip("No CJK-capable font installed");
        }
        using var wideAtlas = new GlyphAtlas(Font, 16f);
        Assert.True(wideAtlas.EnsureGlyph(new GlyphKey("i", Font, 16f, false), out var narrow));
        Assert.True(wideAtlas.EnsureGlyph(new GlyphKey("世", cjkFont, 16f, false), out var wide));

        // A CJK glyph occupies the full em box (~1 x textSize); a narrow
        // Latin glyph is well under it.
        Assert.True(wide.Advance >= 16f * 0.9f,
            $"wide advance ({wide.Advance}) should be ~1 em at 16px");
        Assert.True(wide.Advance > narrow.Advance * 1.4f,
            $"wide advance ({wide.Advance}) should exceed narrow ({narrow.Advance})");
        Assert.True(wide.Width > narrow.Width, "wide glyph bounds should be wider");
    }

    [Fact]
    public void Packing_NoOverlappingRects()
    {
        using var atlas = new GlyphAtlas(Font, 16f, initialSize: 256);
        const string glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*()";
        var placed = new List<GlyphInfo>();

        foreach (char c in glyphs)
        {
            Assert.True(atlas.EnsureGlyph(new GlyphKey(c.ToString(), Font, 16f, false), out var info));
            placed.Add(info);
        }

        Assert.Equal(glyphs.Length, atlas.EntryCount);
        for (int i = 0; i < placed.Count; i++)
        {
            for (int j = i + 1; j < placed.Count; j++)
            {
                var a = placed[i];
                var b = placed[j];
                bool overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width &&
                                a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Assert.False(overlaps, $"entries {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Overflow_GrowsAtlas_PreservingEntries()
    {
        using var atlas = new GlyphAtlas(Font, 16f, initialSize: 64);
        const string glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        foreach (char c in glyphs)
        {
            Assert.True(atlas.EnsureGlyph(new GlyphKey(c.ToString(), Font, 16f, false), out _),
                $"glyph '{c}' should place (atlas may need to grow)");
        }

        Assert.True(atlas.Width > 64, "atlas should have grown past its initial size");
        Assert.Equal(glyphs.Length, atlas.EntryCount);
        foreach (char c in glyphs)
        {
            Assert.True(atlas.TryGetGlyph(new GlyphKey(c.ToString(), Font, 16f, false), out var info));
            Assert.True(RegionHasCoverage(atlas, info), $"glyph '{c}' coverage should survive growth");
        }
    }

    [Fact]
    public void FullAtlas_ReturnsFalse_LeavingAtlasValid()
    {
        // 256 px glyphs (~180x380 px each): 4096^2 holds ~290; 500 overflows.
        using var atlas = new GlyphAtlas(Font, 256f, initialSize: 64);
        bool filled = false;
        for (int i = 0; i < 500; i++)
        {
            if (!atlas.EnsureGlyph(new GlyphKey($"g{i}", Font, 256f, false), out _))
            {
                filled = true;
                // Filled: subsequent requests must also fail, existing entries intact.
                Assert.False(atlas.EnsureGlyph(new GlyphKey("overflow", Font, 256f, false), out _));
                Assert.True(atlas.TryGetGlyph(new GlyphKey("g0", Font, 256f, false), out _));
                break;
            }
        }
        Assert.True(filled, "atlas never filled within 500 glyphs at 256px");
    }

    [Fact]
    public void Service_GetOrCreate_SharesByTypefaceAndSize()
    {
        var a1 = GlyphAtlasService.GetOrCreateAtlas(Font, 16f);
        var a2 = GlyphAtlasService.GetOrCreateAtlas(Font, 16f);
        var a3 = GlyphAtlasService.GetOrCreateAtlas(Font, 20f);

        Assert.Same(a1, a2);
        Assert.NotSame(a1, a3);
        Assert.Equal(2, GlyphAtlasService.AtlasCount);
    }

    [Fact]
    public void Service_EvictsUnreferencedOverBudget_KeepsReferenced()
    {
        // 2048^2 A8 = 4 MB each; 9 atlases = 36 MB > 32 MB budget.
        var kept = GlyphAtlasService.GetOrCreateAtlas(Font, 100f, initialSize: 2048);
        GlyphAtlasService.AcquireAtlas(kept);
        var others = new List<GlyphAtlas>();
        for (int i = 0; i < 9; i++)
        {
            var a = GlyphAtlasService.GetOrCreateAtlas(Font, 110f + i, initialSize: 2048);
            GlyphAtlasService.AcquireAtlas(a);
            others.Add(a);
        }

        // Release all but `kept`; eviction must drop unreferenced atlases
        // until under budget, never the referenced one.
        foreach (var a in others)
            GlyphAtlasService.ReleaseAtlas(a);

        Assert.True(GlyphAtlasService.TotalBytes <= GlyphAtlasService.MaxTotalBytes,
            $"total {GlyphAtlasService.TotalBytes} should be within budget");
        Assert.True(GlyphAtlasService.AtlasCount < 10, "unreferenced atlases should have been evicted");

        // The referenced atlas survives and still works.
        Assert.True(kept.EnsureGlyph(new GlyphKey("W", Font, 100f, false), out _));
        GlyphAtlasService.ReleaseAtlas(kept);
    }

    [Fact]
    public void ConcurrentEnsureGlyph_NoCorruption()
    {
        using var atlas = new GlyphAtlas(Font, 16f, initialSize: 256);
        Parallel.For(0, 400, i =>
        {
            var key = new GlyphKey(((char)('a' + (i % 26))).ToString(), Font, 16f, (i % 3) == 0);
            Assert.True(atlas.EnsureGlyph(key, out _));
        });

        Assert.Equal(26 * 2, atlas.EntryCount);
        // Spot-check a few placements for validity (bounds within the atlas).
        for (int i = 0; i < 26; i++)
        {
            Assert.True(atlas.TryGetGlyph(new GlyphKey(((char)('a' + i)).ToString(), Font, 16f, false), out var info));
            Assert.InRange(info.X, 0, atlas.Width - 1);
            Assert.InRange(info.Y, 0, atlas.Height - 1);
        }
    }
}
