using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Search;
using Dotty.Runtime.Rendering;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public class TabBarSubsystemTests
{
    [Theory]
    [InlineData(1000f, 1, 0)]
    [InlineData(120f, 8, 4)]
    public void TabBarLayout_FirstVisibleTabFillsStripEdgeAndKeepsContentInset(
        float windowWidth, int tabCount, int activeIndex)
    {
        const float barHeight = 48f;
        var layout = TabBarLayout.Calculate(windowWidth, tabCount, activeIndex, barHeight);
        TabLayoutItem firstVisible = default;
        foreach (var tab in layout.Tabs)
        {
            if (tab.TabBounds.Width <= 0f)
                continue;
            firstVisible = tab;
            break;
        }

        Assert.Equal(0f, firstVisible.TabBounds.Left);
        Assert.Equal(0f, firstVisible.TabBounds.Top);
        Assert.Equal(barHeight, firstVisible.TabBounds.Bottom);
        Assert.True(firstVisible.TextBounds.Left > firstVisible.TabBounds.Left);
        Assert.True(firstVisible.TextBounds.Right < firstVisible.CloseButtonBounds.Left);
        Assert.True(firstVisible.CloseButtonBounds.Right <= firstVisible.TabBounds.Right);
        Assert.True(firstVisible.CloseButtonBounds.Top >= firstVisible.TabBounds.Top);
        Assert.True(firstVisible.CloseButtonBounds.Bottom <= firstVisible.TabBounds.Bottom);
        Assert.Equal(TabBarHitType.SelectTab,
            TabBarHitTester.HitTest(0.5f, 1f, windowWidth, tabCount, activeIndex,
                out int hitIndex, barHeight));
        Assert.Equal(firstVisible.Index, hitIndex);
    }

    [Theory]
    [InlineData(1, 0f, 0f)]
    [InlineData(3, 138f, 80f)]
    public void TabBarLayout_NewTabFollowsLastTabWhenWindowGrows(
        int tabCount, float captionWidth, float statusWidth)
    {
        var layout = TabBarLayout.Calculate(1200f, tabCount, 0,
            statusWidth: statusWidth, captionButtonsWidth: captionWidth);
        float lastTabRight = layout.Tabs[tabCount - 1].TabBounds.Right;
        TabRect plus = layout.NewTabButtonBounds;
        Assert.Equal(lastTabRight, plus.Left);
        Assert.Equal(layout.BarBounds.Top, plus.Top);
        Assert.Equal(layout.BarBounds.Bottom, plus.Bottom);
        Assert.Equal(TabBarHitType.NewTab,
            TabBarHitTester.HitTest(plus.Left + plus.Width * 0.5f, 16f,
                1200f, tabCount, 0, out _, statusWidth: statusWidth,
                captionButtonsWidth: captionWidth));
        Assert.Equal(TabBarHitType.NewTab,
            TabBarHitTester.HitTest(lastTabRight, plus.Bottom - 0.5f,
                1200f, tabCount, 0, out _, statusWidth: statusWidth,
                captionButtonsWidth: captionWidth));

        layout = TabBarLayout.Calculate(1600f, tabCount, 0,
            statusWidth: statusWidth, captionButtonsWidth: captionWidth);
        Assert.Equal(lastTabRight, layout.Tabs[tabCount - 1].TabBounds.Right);
        Assert.Equal(plus.Left, layout.NewTabButtonBounds.Left);
        if (statusWidth > 0f)
            Assert.True(layout.NewTabButtonBounds.Right <= layout.StatusBounds.Left);
    }

    [Fact]
    public void TabBarLayout_CompressedTabs_KeepNewTabButtonInsideViewport()
    {
        var layout = TabBarLayout.Calculate(windowWidth: 120f, tabCount: 8, activeIndex: 2);

        Assert.InRange(layout.NewTabButtonBounds.Left, 0f, 120f);
        Assert.InRange(layout.NewTabButtonBounds.Right, 0f, 120f);
        int visibleTabs = 0;
        foreach (var tab in layout.Tabs)
        {
            if (tab.TabBounds.Width <= 0f)
                continue;
            visibleTabs++;
            Assert.True(tab.TabBounds.Right <= layout.NewTabButtonBounds.Left);
        }
        Assert.Equal(1, visibleTabs);
        Assert.True(layout.Tabs[2].TabBounds.Width > 0f);
        Assert.Equal(layout.Tabs[2].TabBounds.Right, layout.NewTabButtonBounds.Left);
        Assert.Equal(layout.BarBounds.Top, layout.NewTabButtonBounds.Top);
        Assert.Equal(layout.BarBounds.Bottom, layout.NewTabButtonBounds.Bottom);
    }

    [Fact]
    public void TabBarLayout_CaptionControlsStayRightWithoutMovingTabsOrPlus()
    {
        const float windowWidth = 1000f;
        const float captionWidth = TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount;
        var nativeLayout = TabBarLayout.Calculate(windowWidth, tabCount: 2, activeIndex: 0);
        float nativeMinimizeWidth = nativeLayout.MinimizeButtonBounds.Width;
        float nativeLastTabRight = nativeLayout.Tabs[1].TabBounds.Right;
        float nativePlusLeft = nativeLayout.NewTabButtonBounds.Left;
        var customLayout = TabBarLayout.Calculate(
            windowWidth, tabCount: 2, activeIndex: 0, captionButtonsWidth: captionWidth);

        Assert.Equal(0f, nativeMinimizeWidth);
        Assert.Equal(windowWidth, customLayout.CloseButtonBounds.Right);
        Assert.Equal(customLayout.MinimizeButtonBounds.Right, customLayout.MaximizeButtonBounds.Left);
        Assert.Equal(customLayout.MaximizeButtonBounds.Right, customLayout.CloseButtonBounds.Left);
        Assert.Equal(nativeLastTabRight, customLayout.Tabs[1].TabBounds.Right);
        Assert.Equal(nativePlusLeft, customLayout.NewTabButtonBounds.Left);
        Assert.True(customLayout.NewTabButtonBounds.Right <= customLayout.MinimizeButtonBounds.Left);
        Assert.Equal(TabBarHitType.Caption,
            TabBarHitTester.HitTest(customLayout.NewTabButtonBounds.Right + 20f,
                16f, windowWidth, 2, 0, out _, captionButtonsWidth: captionWidth));
    }

    [Fact]
    public void TabBarLayout_CustomCaptionButtons_CompressToNarrowWindow()
    {
        const float windowWidth = 40f;
        const float requestedCaptionWidth = TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount;
        var layout = TabBarLayout.Calculate(
            windowWidth, tabCount: 1, activeIndex: 0, captionButtonsWidth: requestedCaptionWidth);
        float closeRight = layout.CloseButtonBounds.Right;
        float minimizeWidth = layout.MinimizeButtonBounds.Width;
        float newTabWidth = layout.NewTabButtonBounds.Width;
        TabBarHitType closeHit = TabBarHitTester.HitTest(
            39f, 10f, windowWidth, tabCount: 1, activeIndex: 0, out _,
            captionButtonsWidth: requestedCaptionWidth);

        Assert.Equal(windowWidth, closeRight);
        Assert.Equal(windowWidth / TabBarLayout.CaptionButtonCount, minimizeWidth);
        Assert.Equal(0f, newTabWidth);
        Assert.Equal(TabBarHitType.Close, closeHit);
    }

    [Fact]
    public void TabBarHitTester_HiddenTabsAndPlusDoNotConsumeStatusClicks()
    {
        var result = TabBarHitTester.HitTest(
            0f, 10f, 200f, tabCount: 1, activeIndex: 0, statusWidth: 200f);
        Assert.IsType<TabBarHitResult.None>(result);
        Assert.Equal(TabBarHitType.None,
            TabBarHitTester.HitTest(0f, 10f, 200f, 1, 0, out _, statusWidth: 200f));
    }

    [Fact]
    public void TabBarHitTester_DoesNotExtendIntoContentBelowStrip()
    {
        const float barHeight = 48f;
        Assert.IsType<TabBarHitResult.None>(
            TabBarHitTester.HitTest(20f, barHeight, 1000f, 1, 0,
                barHeight, captionButtonsWidth: 138f));
        Assert.Equal(TabBarHitType.None,
            TabBarHitTester.HitTest(20f, barHeight, 1000f, 1, 0, out _,
                barHeight, captionButtonsWidth: 138f));
    }

    [Fact]
    public void TabBarHitTester_CustomCaptionArea_ReturnsCaptionAndWindowButtons()
    {
        const float windowWidth = 1000f;
        const float captionWidth = TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount;
        var layout = TabBarLayout.Calculate(
            windowWidth, tabCount: 1, activeIndex: 0, captionButtonsWidth: captionWidth);

        Assert.Equal(
            TabBarHitType.Caption,
            TabBarHitTester.HitTest(400f, 10f, windowWidth, 1, 0, out _,
                captionButtonsWidth: captionWidth));
        Assert.Equal(
            TabBarHitType.Minimize,
            HitCaptionButton(layout.MinimizeButtonBounds));
        Assert.Equal(
            TabBarHitType.Maximize,
            HitCaptionButton(layout.MaximizeButtonBounds));
        Assert.Equal(
            TabBarHitType.Close,
            HitCaptionButton(layout.CloseButtonBounds));

        static TabBarHitType HitCaptionButton(TabRect bounds) =>
            TabBarHitTester.HitTest(
                bounds.Left + bounds.Width * 0.5f,
                bounds.Top + bounds.Height * 0.5f,
                1000f,
                1,
                0,
                out _,
                captionButtonsWidth: 138f);
    }

    [Fact]
    public void TabBarHitTester_ClickingTab_ReturnsSelectTab()
    {
        var result = TabBarHitTester.HitTest(x: 50f, y: 15f, windowWidth: 1000f, tabCount: 3, activeIndex: 0);
        var select = Assert.IsType<TabBarHitResult.SelectTab>(result);
        Assert.Equal(0, select.Index);
    }

    [Fact]
    public void TabBarHitTester_ClickingPlus_ReturnsNewTab()
    {
        var layout = TabBarLayout.Calculate(1000f, 2, 0);
        var plusCenter = (layout.NewTabButtonBounds.Left + layout.NewTabButtonBounds.Right) * 0.5f;

        var result = TabBarHitTester.HitTest(x: plusCenter, y: 15f, windowWidth: 1000f, tabCount: 2, activeIndex: 0);
        Assert.IsType<TabBarHitResult.NewTab>(result);
    }
    [Fact]
    public void ChromePalette_LightTheme_UsesContrastingShadow()
    {
        var palette = ChromeStyleUtils.ResolvePalette(BuiltInThemes.LightPlus);

        Assert.True(
            ChromeStyleUtils.RelativeLuminance(palette.Shadow) <
            ChromeStyleUtils.RelativeLuminance(palette.Canvas));
    }

}

public class ModalCursorSubsystemTests
{
    [Theory]
    [InlineData(0, TerminalCursorShape.Block, true)]
    [InlineData(1, TerminalCursorShape.Block, true)]
    [InlineData(2, TerminalCursorShape.Block, false)]
    [InlineData(3, TerminalCursorShape.Underline, true)]
    [InlineData(4, TerminalCursorShape.Underline, false)]
    [InlineData(5, TerminalCursorShape.Beam, true)]
    [InlineData(6, TerminalCursorShape.Beam, false)]
    public void Parser_Decscusr_SetsExpectedCursorShapeAndBlinking(int code, TerminalCursorShape expectedShape, bool expectedBlink)
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;

        parser.Feed(Encoding.ASCII.GetBytes($"\x1b[{code} q"));

        Assert.Equal(expectedShape, adapter.Buffer.CursorShape);
        Assert.Equal(expectedBlink, adapter.Buffer.CursorBlinking);
    }
}

public class SearchEngineSubsystemTests
{
    [Fact]
    public void SearchEngine_FindMatches_LocatesSubstringsInVisibleRows()
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;
        parser.Feed(Encoding.UTF8.GetBytes("hello world hello test"));

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: -1);
        var matches = SearchEngine.FindMatches(snapshot, "hello", matchCase: false, regex: false);

        Assert.NotNull(matches);
        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Row);
        Assert.Equal(0, matches[0].StartCol);
        Assert.Equal(5, matches[0].EndCol);
        Assert.Equal(12, matches[1].StartCol);
        Assert.Equal(17, matches[1].EndCol);
    }

    [Fact]
    public void SearchOverlayLayout_CalculatesCorrectBounds()
    {
        var layout = SearchOverlayLayout.Compute(viewportWidth: 1000f, viewportHeight: 600f, query: "search test", activeMatchIndex: 1, totalMatches: 5);
        Assert.True(layout.Width > 200f);
        Assert.True(layout.Height > 20f);
        Assert.True(layout.CloseButtonRect.Width > 0);
    }
}

public class FontFallbackSubsystemTests
{
    [Fact]
    public void FontFallbackChain_ResolvesFallbackForMissingGlyph()
    {
        var primary = SKTypeface.Default;
        var chain = new FontFallbackChain(primary);

        var resolved = chain.ResolveTypefaceForGrapheme("A", bold: false);
        Assert.NotNull(resolved);
    }
}

public class HyperlinkSubsystemTests
{
    [Fact]
    public void HyperlinkScanner_FindsImplicitHttpUrls()
    {
        var parser = new BasicAnsiParser();
        var adapter = new TerminalAdapter(24, 80);
        parser.Handler = adapter;
        parser.Feed(Encoding.UTF8.GetBytes("Check https://github.com/dotty for updates"));

        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: -1);
        var links = HyperlinkScanner.ScanRow(snapshot, 0);

        Assert.Single(links);
        Assert.Equal("https://github.com/dotty", links[0].Url);
        Assert.Equal(6, links[0].StartCol);
        Assert.Equal(29, links[0].EndCol);

        var linkAtCol10 = HyperlinkScanner.FindLinkAt(snapshot, row: 0, col: 10);
        Assert.NotNull(linkAtCol10);
        Assert.Equal("https://github.com/dotty", linkAtCol10.Value.Url);

        var linkAtCol2 = HyperlinkScanner.FindLinkAt(snapshot, row: 0, col: 2);
        Assert.Null(linkAtCol2);
    }
}
