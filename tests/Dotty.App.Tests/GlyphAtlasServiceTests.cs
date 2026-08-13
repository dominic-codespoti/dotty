using Dotty.App.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

// GlyphAtlasService is process-wide shared state; other test classes use it
// concurrently. Run these tests alone so reference counts and eviction are
// deterministic.
[CollectionDefinition("GlyphAtlas", DisableParallelization = true)]
public class GlyphAtlasCollectionDefinition
{
}

[Collection("GlyphAtlas")]
public sealed class GlyphAtlasServiceTests
{
    public GlyphAtlasServiceTests()
    {
        // Safe because this collection is serialized (DisableParallelization):
        // no other test class can be using the shared service concurrently.
        GlyphAtlasService.ClearAllAtlases();
    }

    // NOTE: these tests must not call GlyphAtlasService.ClearAllAtlases():
    // other test classes use shared atlases concurrently, and disposing an
    // atlas while another thread draws from it segfaults. Each test acquires
    // and releases only the references it owns; eviction bounds the rest.

    [Fact]
    public void AcquireAndRelease_ReferenceCountTracksMountedViews()
    {
        var options = new GlyphRasterizationOptions();

        var atlas = GlyphAtlasService.GetOrCreateAtlas(SKTypeface.Default, 16f, options);
        Assert.Equal(1, GlyphAtlasService.AtlasCount);

        // Two views share the same atlas; same identity returned on re-query.
        var atlas2 = GlyphAtlasService.GetOrCreateAtlas(SKTypeface.Default, 16f, options);
        Assert.Same(atlas, atlas2);

        GlyphAtlasService.AcquireAtlas(atlas);
        GlyphAtlasService.AcquireAtlas(atlas);
        GlyphAtlasService.ReleaseAtlas(atlas);
        Assert.Equal(1, GlyphAtlasService.AtlasCount); // referenced: not evicted

        GlyphAtlasService.ReleaseAtlas(atlas);
        GlyphAtlasService.ReleaseAtlas(atlas);
        Assert.Equal(1, GlyphAtlasService.AtlasCount); // zero refs, under budget
    }

    [Fact]
    public void SizeBytes_TracksTextureGrowth()
    {
        var atlas = GlyphAtlasService.GetOrCreateAtlas(SKTypeface.Default, 20f, new GlyphRasterizationOptions());

        long initial = atlas.SizeBytes;
        Assert.True(initial > 0);
        Assert.Equal((long)atlas.AtlasBitmap.Width * atlas.AtlasBitmap.Height * 4, initial);
    }

    [Fact]
    public void Eviction_UnreferencedAtlasesAreEvictedUnderBudget()
    {
        // Fill the cache past the 32 MB budget with distinct font sizes.
        // Each 1024x1024 RGBA atlas is 4 MB; 10 atlases exceed the budget.
        var atlases = new System.Collections.Generic.List<GlyphAtlas>();
        for (int i = 0; i < 10; i++)
        {
            var atlas = GlyphAtlasService.GetOrCreateAtlas(
                SKTypeface.Default,
                10f + i,
                new GlyphRasterizationOptions());
            atlases.Add(atlas);
        }

        // None referenced: total should now exceed the budget.
        long total = GlyphAtlasService.TotalBytes;
        Assert.True(total >= GlyphAtlasService.MaxTotalBytes,
            $"Expected cache over budget, got {total} bytes");

        // A release triggers eviction of LRU unreferenced atlases.
        GlyphAtlasService.ReleaseAtlas(atlases[0]);
        Assert.True(GlyphAtlasService.TotalBytes <= GlyphAtlasService.MaxTotalBytes,
            $"Cache not bounded after eviction: {GlyphAtlasService.TotalBytes} bytes");
        Assert.True(GlyphAtlasService.AtlasCount < 10,
            "Expected at least one atlas evicted");
    }

    [Fact]
    public void Eviction_ReferencedAtlasIsNeverEvicted()
    {
        var atlases = new System.Collections.Generic.List<GlyphAtlas>();
        for (int i = 0; i < 10; i++)
        {
            var atlas = GlyphAtlasService.GetOrCreateAtlas(
                SKTypeface.Default,
                30f + i,
                new GlyphRasterizationOptions());
            atlases.Add(atlas);
        }

        // Keep the first atlas referenced; trigger an eviction pass by
        // releasing a different (already unreferenced) atlas.
        GlyphAtlasService.AcquireAtlas(atlases[0]);
        GlyphAtlasService.ReleaseAtlas(atlases[1]); // triggers eviction pass
        Assert.True(GlyphAtlasService.TotalBytes <= GlyphAtlasService.MaxTotalBytes);

        // The referenced atlas must still be alive and usable.
        var stillAlive = GlyphAtlasService.GetOrCreateAtlas(
            SKTypeface.Default,
            30f,
            new GlyphRasterizationOptions());
        Assert.Same(atlases[0], stillAlive);
        Assert.True(stillAlive.TryGetGlyph(new GlyphKey("A"), out _));

        GlyphAtlasService.ReleaseAtlas(atlases[0]);
    }
}
