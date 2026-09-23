using System;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Rendering;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class RenderingAllocationTests
{
    private static PaddingUserConfig NoPadding() => new()
    {
        Left = 0,
        Top = 0,
        Right = 0,
        Bottom = 0
    };

    [Fact]
    public void Compose_UnchangedSceneAllocatesNothingAfterWarmup()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        tab.Session.Adapter.Buffer.SetCursor(0, 0);
        tab.Session.Adapter.Buffer.WriteText("steady state".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager();
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var padding = NoPadding();
        var overlay = new SearchOverlayRenderState(false, string.Empty, -1, 0, null);
        var menu = new ContextMenuModel(30, 20, new[]
        {
            new ContextMenuItem("copy", "Copy", shortcut: "Ctrl+C"),
            new ContextMenuItem("paste", "Paste")
        })
        { IsVisible = true };

        for (int i = 0; i < 8; i++)
            Compose();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            Compose();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Compose() => composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            selectionColor: new SgrColorArgb(0x803385DB),
            framebufferWidth: 400,
            framebufferHeight: 160,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: 4,
            columns: 20,
            padding: padding,
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: overlay,
            activeContextMenu: menu);
    }

    [Fact]
    public void TabBarBuild_WithStableTitleAndStatusAllocatesNothingAfterWarmup()
    {
        using var manager = new TerminalTabManager();
        var tab = manager.CreateTab(cols: 20, rows: 4);
        tab.Title = "Stable title";
        var typeface = SKTypeface.Default;
        using var atlas = new GlyphAtlas(typeface, 14f, initialSize: 256);
        var titles = new StableTitles("Stable title");
        var theme = BuiltInThemes.DarkPlus;
        var instances = new CellInstance[128];
        var chrome = new ChromeQuadInstance[64];
        const string statusText = "ready";

        for (int i = 0; i < 8; i++)
            Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            Build();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Build() => TabBarQuadBuilder.Build(
            manager,
            atlas,
            typeface,
            14f,
            theme,
            640,
            10,
            20,
            instances,
            chrome,
            out _,
            titles: titles,
            status: statusText.AsSpan());
    }

    [Fact]
    public void TabBarBuild_AlternatingActiveAndHoveredTabsAllocatesNothingAfterWarmup()
    {
        using var manager = new TerminalTabManager();
        manager.CreateTab(cols: 20, rows: 4);
        manager.CreateTab(cols: 20, rows: 4);
        manager.CreateTab(cols: 20, rows: 4);
        var typeface = SKTypeface.Default;
        using var atlas = new GlyphAtlas(typeface, 14f, initialSize: 256);
        var theme = BuiltInThemes.DarkPlus;
        // A fixed title source keeps the measurement independent of titles the
        // platform PTY may set asynchronously (ConPTY emits one at startup).
        var titles = new StableTitles("Tab");
        var instances = new CellInstance[256];
        var chrome = new ChromeQuadInstance[96];

        for (int i = 0; i < 3; i++)
        {
            for (int hover = 0; hover < 3; hover++)
                Build(i, hover);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int repetition = 0; repetition < 20; repetition++)
        {
            int active = repetition % 3;
            int hovered = (repetition / 3) % 3;
            Build(active, hovered);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Build(int activeIndex, int hoveredIndex)
        {
            manager.SelectTab(activeIndex);
            TabBarQuadBuilder.Build(
                manager,
                atlas,
                typeface,
                14f,
                theme,
                640,
                10,
                20,
                instances,
                chrome,
                out _,
                hoveredTabIndex: hoveredIndex,
                hoveredHitType: TabBarHitType.SelectTab,
                titles: titles);
        }
    }

    [Fact]
    public void CenteredOffset_ComputesWithoutAllocationsAfterWarmup()
    {
        var typeface = SKTypeface.Default;
        for (int i = 0; i < 8; i++)
            _ = ChromeStyleUtils.ComputeCenteredOffsetY(typeface, 14f, 0, 20f, 0f, 32f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            _ = ChromeStyleUtils.ComputeCenteredOffsetY(typeface, 14f, 0, 20f, 0f, 32f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void MeasureCell_WithStableInputsAllocatesNothingAfterWarmup()
    {
        var typeface = SKTypeface.Default;
        for (int i = 0; i < 8; i++)
            _ = FontMetricsService.MeasureCell(typeface, 14f, 1.2, 1f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            _ = FontMetricsService.MeasureCell(typeface, 14f, 1.2, 1f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }


    private sealed class StableTitles(string title) : ITabTitleSource
    {
        public ReadOnlySpan<char> GetTitle(TerminalTab tab, int index) => title.AsSpan();
    }
}
