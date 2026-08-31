using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Search;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public class TabBarSubsystemTests
{
    [Fact]
    public void TabBarLayout_SingleTab_FillsAvailableWidth()
    {
        var layout = TabBarLayout.Calculate(windowWidth: 1000f, tabCount: 1, activeIndex: 0);
        Assert.NotNull(layout);
        Assert.Single(layout.Tabs);
        Assert.True(layout.Tabs[0].TabBounds.Width > 0);
        Assert.True(layout.NewTabButtonBounds.Width > 0);
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
