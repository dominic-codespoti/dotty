using System;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Scrollbar;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScrollbarAndBellTests
{
    [Fact]
    public void ScrollbarQuadBuilder_WhenNoScrollback_EmitsZeroQuads()
    {
        Span<CellInstance> destination = stackalloc CellInstance[64];
        int written = ScrollbarQuadBuilder.Build(
            startColOffset: 0,
            startRowOffset: 1,
            paneCols: 80,
            paneRows: 24,
            scrollbackCount: 0,
            scrollOffset: 0,
            theme: BuiltInThemes.DarkPlus,
            destination: destination);

        Assert.Equal(0, written);
    }

    [Fact]
    public void ScrollbarQuadBuilder_WhenScrollbackExists_EmitsThumbQuadsInRightmostColumn()
    {
        Span<CellInstance> destination = stackalloc CellInstance[64];
        int written = ScrollbarQuadBuilder.Build(
            startColOffset: 0,
            startRowOffset: 0,
            paneCols: 80,
            paneRows: 24,
            scrollbackCount: 100,
            scrollOffset: 50,
            theme: BuiltInThemes.DarkPlus,
            destination: destination);

        Assert.True(written > 0);
        for (int i = 0; i < written; i++)
        {
            Assert.Equal(79, destination[i].Col); // Rightmost column (80 - 1)
            Assert.True(destination[i].Row >= 0 && destination[i].Row < 24);
            Assert.True(destination[i].BgA > 0);
        }
    }
    [Fact]
    public void ScrollbarQuadBuilder_WhenHoveredOrDragging_EmitsTrackGrooveAndProminentThumb()
    {
        Span<CellInstance> destination = stackalloc CellInstance[128];
        int written = ScrollbarQuadBuilder.Build(
            startColOffset: 0,
            startRowOffset: 0,
            paneCols: 80,
            paneRows: 24,
            scrollbackCount: 100,
            scrollOffset: 50,
            theme: BuiltInThemes.DarkPlus,
            destination: destination,
            isHoveredOrDragging: true);

        Assert.True(written >= 24); // Contains track groove + thumb
        bool hasHighAlphaThumb = false;
        bool hasTrackGroove = false;
        for (int i = 0; i < written; i++)
        {
            if (destination[i].BgA >= 200) hasHighAlphaThumb = true;
            if (destination[i].BgA == 50) hasTrackGroove = true;
        }

        Assert.True(hasHighAlphaThumb);
        Assert.True(hasTrackGroove);
    }

    [Fact]
    public void TerminalAdapter_OnBell_RaisesBellEvent()
    {
        var adapter = new TerminalAdapter();
        bool bellFired = false;
        adapter.Bell += () => bellFired = true;

        adapter.OnBell();

        Assert.True(bellFired);
    }

    [Fact]
    public void TabManager_InactiveTabReceivesBell_SetsHasBellAlert()
    {
        using var manager = new TerminalTabManager();
        var tab1 = manager.CreateTab(cols: 80, rows: 24);
        var tab2 = manager.CreateTab(cols: 80, rows: 24);

        manager.SelectTab(tab1);
        Assert.False(tab2.HasBellAlert);

        tab2.Session.Adapter.OnBell();
        Assert.True(tab2.HasBellAlert);

        manager.SelectTab(tab2);
        Assert.False(tab2.HasBellAlert);
    }
}
