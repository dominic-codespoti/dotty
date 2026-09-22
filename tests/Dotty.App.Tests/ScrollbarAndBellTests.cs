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
        Span<ChromeQuadInstance> destination = stackalloc ChromeQuadInstance[2];
        int written = ScrollbarQuadBuilder.Build(
            paneX: 0f,
            paneY: 0f,
            paneWidth: 800f,
            paneHeight: 480f,
            scrollbackCount: 0,
            scrollOffset: 0,
            cellHeight: 20f,
            theme: BuiltInThemes.DarkPlus,
            destination: destination);

        Assert.Equal(0, written);
    }

    [Fact]
    public void ScrollbarQuadBuilder_WhenScrollbackExists_EmitsSlimProportionalThumb()
    {
        Span<ChromeQuadInstance> destination = stackalloc ChromeQuadInstance[2];
        int written = ScrollbarQuadBuilder.Build(
            paneX: 0f,
            paneY: 0f,
            paneWidth: 800f,
            paneHeight: 480f,
            scrollbackCount: 100,
            scrollOffset: 50,
            cellHeight: 20f,
            theme: BuiltInThemes.DarkPlus,
            destination: destination);

        Assert.Equal(1, written);
        Assert.InRange(destination[0].X, 790f, 796f);
        Assert.InRange(destination[0].W, 2f, 5f);
        Assert.InRange(destination[0].Y, 180f, 230f);
        Assert.True(destination[0].H > 0f && destination[0].H < 480f);
        Assert.True(destination[0].Radius > 0f);
    }

    [Fact]
    public void ScrollbarQuadBuilder_WhenHoveredOrDragging_EmitsTrackAndAccentThumb()
    {
        Span<ChromeQuadInstance> destination = stackalloc ChromeQuadInstance[2];
        int written = ScrollbarQuadBuilder.Build(
            paneX: 0f,
            paneY: 0f,
            paneWidth: 800f,
            paneHeight: 480f,
            scrollbackCount: 100,
            scrollOffset: 50,
            cellHeight: 20f,
            theme: BuiltInThemes.DarkPlus,
            destination: destination,
            isHoveredOrDragging: true);

        Assert.Equal(2, written);
        Assert.Equal(480f, destination[0].H);
        Assert.True(destination[0].W > destination[1].W);
        Assert.True(destination[1].W > 4f);
        Assert.True(destination[1].TopA >= 0.99f);
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
