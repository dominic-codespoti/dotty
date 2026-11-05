using System;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Search;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>W4-C8: row caches key on the atlas layout epoch, and a full atlas is rebuilt at a frame boundary.</summary>
public sealed class TerminalSceneComposerAtlasEpochTests
{
    private const int Rows = 4;
    private const int Columns = 20;

    private static TerminalSceneFrame Compose(TerminalSceneComposer composer, TerminalTab tab, TerminalTabManager manager)
    {
        var theme = BuiltInThemes.DarkPlus;
        return composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), new SgrColorArgb(0x803385DB),
            framebufferWidth: Columns * 10, framebufferHeight: Rows * 20, cellWidth: 10, cellHeight: 20, scale: 1,
            rows: Rows, columns: Columns,
            padding: new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 },
            showTabBar: false, cursorVisible: false, scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null), activeContextMenu: null);
    }

    private static void Write(TerminalTab tab, int row, string text)
    {
        var buffer = tab.ActivePane.Session.Adapter.Buffer;
        buffer.SetCursor(row, 0);
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
    }

    [Fact]
    public void RowCacheSurvivesANewGlyphInAnotherRow()
    {
        using var tab = new TerminalTab(rows: Rows, columns: Columns);
        Write(tab, 0, "abc");
        Write(tab, 1, "def");
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        Compose(composer, tab, manager);
        Compose(composer, tab, manager);

        int entries = atlas.EntryCount;
        int epoch = atlas.LayoutEpoch;
        Write(tab, 2, "XYZ");                                   // glyphs the atlas has not seen
        Compose(composer, tab, manager);
        Assert.True(atlas.EntryCount > entries);
        Assert.Equal(epoch, atlas.LayoutEpoch);

        long hits = composer.CachedRowHits;
        var cached = Compose(composer, tab, manager);
        Assert.True(composer.CachedRowHits - hits >= 3, "rows 0-2 should be reused, not recomposed");

        var fresh = Compose(new TerminalSceneComposer(atlas, SKTypeface.Default, 14f), tab, manager);
        Assert.True(cached.AsSpan().SequenceEqual(fresh.AsSpan()));
    }

    [Fact]
    public void RebuildInvalidatesRowCachesAndTheFrameReensuresItsGlyphs()
    {
        using var tab = new TerminalTab(rows: Rows, columns: Columns);
        Write(tab, 0, "hello");
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var before = Compose(composer, tab, manager);
        Compose(composer, tab, manager);

        atlas.Rebuild();
        Assert.Equal(0, atlas.EntryCount);
        long hits = composer.CachedRowHits;
        var after = Compose(composer, tab, manager);
        Assert.Equal(0, composer.CachedRowHits - hits);
        Assert.True(atlas.EntryCount >= 4);                      // h, e, l, o placed again
        Assert.Equal(before.InstanceCount, after.InstanceCount);
    }

    [Fact]
    public void FullAtlasIsRebuiltAtTheNextFrameBoundary()
    {
        using var tab = new TerminalTab(rows: Rows, columns: Columns);
        Write(tab, 0, "hello");
        using var manager = new TerminalTabManager(SilentPty.Create);
        var font = SKTypeface.Default;
        using var atlas = new GlyphAtlas(font, 14f, initialSize: 64);
        var composer = new TerminalSceneComposer(atlas, font, 14f);

        // Fill the atlas with large glyphs until a placement fails (every glyph stays under the 512 px glyph cap).
        for (int i = 0; atlas.EnsureGlyph(new GlyphKey($"g{i}", font, 160f, GlyphStyle.Regular), out _); i++)
            Assert.True(i < 2000, "atlas never filled");
        Assert.True(atlas.NeedsRebuild, "full atlas should request a rebuild");

        int epoch = atlas.LayoutEpoch;
        var frame = Compose(composer, tab, manager);
        Assert.Equal(1, atlas.RebuildCount);
        Assert.Equal(epoch + 1, atlas.LayoutEpoch);
        Assert.False(atlas.NeedsRebuild);
        Assert.True(atlas.TryGetGlyph(new GlyphKey("h", font, 14f, GlyphStyle.Regular), out _), "visible glyph re-ensured");
        Assert.True(frame.InstanceCount > 0, "frame has instances");

        Compose(composer, tab, manager);
        Assert.Equal(1, atlas.RebuildCount);                     // not full any more: no further rebuild
    }
}
