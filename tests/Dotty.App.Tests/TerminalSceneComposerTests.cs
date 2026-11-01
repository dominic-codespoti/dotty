using Dotty.Silk;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using Dotty.Runtime.Config;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Search;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using RuntimeSearchMatch = Dotty.Runtime.Search.SearchMatch;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalSceneComposerTests
{
    private static PaddingUserConfig NoPadding() => new()
    {
        Left = 0,
        Top = 0,
        Right = 0,
        Bottom = 0
    };


    [Fact]
    public void Compose_WritesGlyphInstancesForActivePane()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        tab.Session.Adapter.Buffer.SetCursor(0, 0);
        tab.Session.Adapter.Buffer.WriteText("hello".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;

        var frame = composer.Compose(
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
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);

        Assert.True(frame.InstanceCount > 0);
        Assert.Contains(frame.Instances.AsSpan(0, frame.InstanceCount).ToArray(), instance => instance.Row == 0);
    }

    [Fact]
    public void Compose_CachedRowsMatchFullRebuildAcrossWritesScrollsAndOverlays()
    {
        const int rows = 8;
        const int columns = 24;
        using var tab = new TerminalTab(rows: rows, columns: columns);
        var pane = tab.ActivePane;
        var buffer = pane.Session.Adapter.Buffer;
        var line = new char[columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
                line[column] = (char)('A' + (row + column) % 26);
            buffer.SetCursor(row, 0);
            buffer.WriteText(line, CellAttributes.Default);
        }

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 64);
        var cachedComposer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var selectionColor = new SgrColorArgb(0x803385DB);
        var noSearch = new SearchOverlayRenderState(false, string.Empty, -1, 0, null);

        CellInstance[] Render(TerminalSceneComposer composer, bool showCursor, SearchOverlayRenderState search)
        {
            var frame = composer.Compose(
                tab,
                manager,
                theme,
                new SgrColorArgb(theme.Foreground),
                selectionColor,
                columns * 10,
                rows * 20,
                10,
                20,
                1,
                rows,
                columns,
                NoPadding(),
                showTabBar: false,
                cursorVisible: showCursor,
                scrollbarHovered: false,
                scrollbarDragging: false,
                searchOverlay: search,
                activeContextMenu: null);
            return frame.AsSpan().ToArray();
        }

        void Compare(bool showCursor = false, SearchOverlayRenderState? search = null)
        {
            var cached = Render(cachedComposer, showCursor, search ?? noSearch);
            var rebuilt = Render(new TerminalSceneComposer(atlas, SKTypeface.Default, 14f), showCursor, search ?? noSearch);
            Assert.True(cached.AsSpan().SequenceEqual(rebuilt),
                "The row-cached composition must be byte-for-byte equal to a fresh full composition.");
        }

        Compare();
        Compare();
        Compare();

        var random = new Random(0x5A17);
        Span<char> cell = stackalloc char[1];
        for (int update = 0; update < 24; update++)
        {
            int row = random.Next(rows);
            int column = random.Next(columns);
            buffer.SetCursor(row, column);
            cell[0] = (char)('a' + random.Next(26));
            buffer.WriteText(cell, CellAttributes.Default);
            Compare();
        }

        pane.Session.Parser.Feed("\x1b[2;7r\x1b[5;1H\x1b[S"u8);
        Compare();
        pane.Session.Parser.Feed("\x1b[2;7r\x1b[4;1H\x1b[T"u8);
        Compare();
        pane.Session.Parser.Feed("\x1b[3;6r\x1b[4;1H\x1b[L"u8);
        Compare();
        pane.Session.Parser.Feed("\x1b[3;6r\x1b[4;1H\x1b[M"u8);
        Compare();
        pane.Session.Parser.Feed("\x1b[r\x1b[8;1H\x1b[S"u8);
        Compare();
        pane.Session.Parser.Feed("\x1b[r\x1b[1;1H\x1b[T"u8);
        Compare();

        pane.Selection.StartSelection(1, 2);
        pane.Selection.UpdateSelection(4, 9);
        buffer.SetCursor(2, 5);
        var searchMatches = new[]
        {
            new RuntimeSearchMatch(1, 3, 8, IsActive: false),
            new RuntimeSearchMatch(4, 6, 12, IsActive: true)
        };
        var search = new SearchOverlayRenderState(true, "needle", 1, searchMatches.Length, searchMatches);
        Compare(showCursor: true, search: search);
        pane.Selection.StartSelection(2, 4, Dotty.Runtime.Selection.SelectionMode.Block);
        pane.Selection.UpdateSelection(6, 13);
        buffer.SetCursor(6, 15);
        Compare(showCursor: true, search: search);
        pane.Selection.ClearSelection();
        buffer.SetCursor(0, 0);
        Compare(showCursor: true, search: search);

        int widthBeforeAtlasGrowth = atlas.Width;
        int heightBeforeAtlasGrowth = atlas.Height;
        for (int i = 0; i < rows * columns; i++)
        {
            string glyph = char.ConvertFromUtf32(0x2800 + i);
            buffer.SetCursor(i / columns, i % columns);
            buffer.WriteText(glyph.AsSpan(), CellAttributes.Default);
        }
        Compare(showCursor: true, search: search);
        Assert.True(atlas.Width > widthBeforeAtlasGrowth || atlas.Height > heightBeforeAtlasGrowth,
            "The unique glyph batch must grow the deliberately tiny test atlas.");
    }


    [Fact]
    public void Compose_OneLineScrollMeasuresRowReuseAgainstFullRebuild()
    {
        const int rows = 60;
        const int columns = 200;
        const int frames = 12;
        using var tab = new TerminalTab(rows: rows, columns: columns);
        var buffer = tab.ActivePane.Session.Adapter.Buffer;
        var line = new char[columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
                line[column] = (char)('a' + (row + column) % 26);
            buffer.SetCursor(row, 0);
            buffer.WriteText(line, CellAttributes.Default);
        }

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 1024);
        var cachedComposer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var fullComposer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var padding = NoPadding();
        var noSearch = new SearchOverlayRenderState(false, string.Empty, -1, 0, null);

        TerminalSceneFrame Compose(TerminalSceneComposer composer, int height) =>
            composer.Compose(
                tab,
                manager,
                theme,
                new SgrColorArgb(theme.Foreground),
                new SgrColorArgb(0x803385DB),
                columns * 10,
                height,
                10,
                20,
                1,
                rows,
                columns,
                padding,
                showTabBar: false,
                cursorVisible: false,
                scrollbarHovered: false,
                scrollbarDragging: false,
                searchOverlay: noSearch,
                activeContextMenu: null);

        for (int i = 0; i < 4; i++)
        {
            Compose(cachedComposer, rows * 20);
            Compose(fullComposer, rows * 20 + (i & 1));
        }

        long cachedTicks = 0;
        long rebuiltTicks = 0;
        long hitsBefore = cachedComposer.CachedRowHits;
        var watch = new Stopwatch();
        for (int frameIndex = 0; frameIndex < frames; frameIndex++)
        {
            buffer.ScrollUpLines(1);
            buffer.SetCursor(rows - 1, 0);
            for (int column = 0; column < columns; column++)
                line[column] = (char)('a' + (frameIndex + column) % 26);
            buffer.WriteText(line, CellAttributes.Default);

            watch.Restart();
            var cached = Compose(cachedComposer, rows * 20);
            watch.Stop();
            cachedTicks += watch.ElapsedTicks;

            // Alternating an unused framebuffer-height pixel invalidates the
            // row cache while leaving the single pane's cell coordinates intact.
            watch.Restart();
            var rebuilt = Compose(fullComposer, rows * 20 + (frameIndex & 1));
            watch.Stop();
            rebuiltTicks += watch.ElapsedTicks;

            Assert.True(cached.AsSpan().SequenceEqual(rebuilt.AsSpan()));
        }

        Assert.True(cachedComposer.CachedRowHits - hitsBefore >= (rows - 1) * frames);
        double cachedMs = cachedTicks * 1000d / Stopwatch.Frequency / frames;
        double rebuiltMs = rebuiltTicks * 1000d / Stopwatch.Frequency / frames;
        Console.WriteLine($"200x60 one-line scroll: cached {cachedMs:F2} ms/frame; full rebuild {rebuiltMs:F2} ms/frame.");
    }

    [Fact]
    public void Compose_RendersEachLeafUsingItsPreservedScrollOffset()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 20);
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        for (int row = 0; row < 4; row++)
        {
            first.Session.Adapter.Buffer.SetCursor(row, 0);
            first.Session.Adapter.Buffer.WriteText($"first {row}".AsSpan(), CellAttributes.Default);
            second.Session.Adapter.Buffer.SetCursor(row, 0);
            second.Session.Adapter.Buffer.WriteText($"second {row}".AsSpan(), CellAttributes.Default);
        }
        first.Session.Adapter.Buffer.ScrollUpLines(2);
        second.Session.Adapter.Buffer.ScrollUpLines(2);
        first.ScrollTo(1, first.Session.Adapter.Buffer.ScrollbackCount);
        second.ScrollTo(2, second.Session.Adapter.Buffer.ScrollbackCount);

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;

        tab.PaneTree.ActivePane = first;
        var firstActiveFrame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            selectionColor: new SgrColorArgb(0x803385DB),
            framebufferWidth: 400,
            framebufferHeight: 80,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: 4,
            columns: 40,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);
        var firstActiveInstances = firstActiveFrame.AsSpan().ToArray();

        tab.PaneTree.ActivePane = second;
        var secondActiveFrame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            selectionColor: new SgrColorArgb(0x803385DB),
            framebufferWidth: 400,
            framebufferHeight: 80,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: 4,
            columns: 40,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);

        Assert.Equal(firstActiveInstances.Length, secondActiveFrame.InstanceCount);
        Assert.True(firstActiveInstances.AsSpan().SequenceEqual(secondActiveFrame.AsSpan()));
    }

    [Fact]
    public void Compose_HidesLiveCursorWhilePaneIsScrolledBack()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        var pane = tab.ActivePane;
        for (int row = 0; row < 4; row++)
        {
            pane.Session.Adapter.Buffer.SetCursor(row, 0);
            pane.Session.Adapter.Buffer.WriteText($"line {row}".AsSpan(), CellAttributes.Default);
        }

        pane.Session.Adapter.Buffer.ScrollUpLines(1);
        pane.Session.Adapter.Buffer.SetCursor(0, 0);
        pane.ScrollTo(1, pane.Session.Adapter.Buffer.ScrollbackCount);

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;

        CellInstance[] ComposeInstances(bool cursorVisible)
        {
            var frame = composer.Compose(
                tab,
                manager,
                theme,
                new SgrColorArgb(theme.Foreground),
                selectionColor: new SgrColorArgb(0x803385DB),
                framebufferWidth: 200,
                framebufferHeight: 80,
                cellWidth: 10,
                cellHeight: 20,
                scale: 1,
                rows: 4,
                columns: 20,
                padding: NoPadding(),
                showTabBar: false,
                cursorVisible: cursorVisible,
                scrollbarHovered: false,
                scrollbarDragging: false,
                searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
                activeContextMenu: null);
            return frame.AsSpan().ToArray();
        }

        var withoutCursor = ComposeInstances(cursorVisible: false);
        var withCursor = ComposeInstances(cursorVisible: true);

        Assert.Equal(withoutCursor.Length, withCursor.Length);
        Assert.True(withoutCursor.AsSpan().SequenceEqual(withCursor));
    }

    [Fact]
    public void Compose_SearchHighlightsTranslateAndClipVisibleMatches()
    {
        using var tab = new TerminalTab(rows: 4, columns: 8);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var matches = new[]
        {
        new RuntimeSearchMatch(0, -2, 2, IsActive: false),
        new RuntimeSearchMatch(1, 2, 20, IsActive: true),
        new RuntimeSearchMatch(-1, 0, 2, IsActive: false),
        new RuntimeSearchMatch(8, 0, 2, IsActive: false)
    };

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            80,
            80,
            10,
            20,
            1,
            4,
            8,
            NoPadding(),
            false,
            false,
            false,
            false,
            new SearchOverlayRenderState(true, "needle", 1, matches.Length, matches),
            null);

        var regularColumns = new List<ushort>();
        var activeColumns = new List<ushort>();
        foreach (var instance in frame.AsSpan())
        {
            bool isRegularHighlight = instance.Flags == CellFlags.InverseVideo
                && instance.BgA == 255
                && instance.BgR == SearchQuadBuilder.MatchBackground.R
                && instance.BgG == SearchQuadBuilder.MatchBackground.G
                && instance.BgB == SearchQuadBuilder.MatchBackground.B
                && instance.Row == 0;
            bool isActiveHighlight = instance.Flags == CellFlags.InverseVideo
                && instance.BgA == 255
                && instance.BgR == SearchQuadBuilder.ActiveMatchBackground.R
                && instance.BgG == SearchQuadBuilder.ActiveMatchBackground.G
                && instance.BgB == SearchQuadBuilder.ActiveMatchBackground.B
                && instance.Row == 1;

            if (isRegularHighlight)
                regularColumns.Add(instance.Col);
            else if (isActiveHighlight)
                activeColumns.Add(instance.Col);
        }

        Assert.Equal(new ushort[] { 0, 1 }, regularColumns);
        Assert.Equal(new ushort[] { 2, 3, 4, 5, 6, 7 }, activeColumns);
    }

    [Fact]
    public void Compose_SearchHighlightsTranslateScrollbackRows()
    {
        using var tab = new TerminalTab(rows: 4, columns: 8);
        var pane = tab.ActivePane;
        for (int row = 0; row < 4; row++)
        {
            pane.Session.Adapter.Buffer.SetCursor(row, 0);
            pane.Session.Adapter.Buffer.WriteText($"line {row}".AsSpan(), CellAttributes.Default);
        }
        pane.Session.Adapter.Buffer.ScrollUpLines(1);
        pane.ScrollTo(1, pane.Session.Adapter.Buffer.ScrollbackCount);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var matches = new[] { new RuntimeSearchMatch(0, 1, 2, IsActive: false) };

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            160,
            80,
            10,
            20,
            1,
            4,
            8,
            NoPadding(),
            false,
            false,
            false,
            false,
            new SearchOverlayRenderState(true, "needle", 0, matches.Length, matches),
            null);

        int highlightCount = 0;
        foreach (var instance in frame.AsSpan())
        {
            if (instance.Flags == CellFlags.InverseVideo
                && instance.BgR == SearchQuadBuilder.MatchBackground.R
                && instance.BgG == SearchQuadBuilder.MatchBackground.G
                && instance.BgB == SearchQuadBuilder.MatchBackground.B)
            {
                Assert.Equal((ushort)1, instance.Row);
                Assert.Equal((ushort)1, instance.Col);
                highlightCount++;
            }
        }

        Assert.Equal(1, highlightCount);
    }

    [Fact]
    public void Compose_SearchHighlightsOnlyActivePaneAtGlobalSplitOffset()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 8);
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        tab.PaneTree.ActivePane = second;
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var matches = new[] { new RuntimeSearchMatch(0, 0, 1, IsActive: true) };

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            160,
            80,
            10,
            20,
            1,
            4,
            16,
            NoPadding(),
            false,
            false,
            false,
            false,
            new SearchOverlayRenderState(true, "needle", 0, matches.Length, matches),
            null);

        int expectedColumn = (int)Math.Round(second.Bounds.X / 10f);
        int highlightCount = 0;
        foreach (var instance in frame.AsSpan())
        {
            if (instance.Flags == CellFlags.InverseVideo
                && instance.BgR == SearchQuadBuilder.ActiveMatchBackground.R
                && instance.BgG == SearchQuadBuilder.ActiveMatchBackground.G
                && instance.BgB == SearchQuadBuilder.ActiveMatchBackground.B)
            {
                Assert.Equal((ushort)expectedColumn, instance.Col);
                Assert.Equal((ushort)0, instance.Row);
                highlightCount++;
            }
        }

        Assert.Equal(1, highlightCount);
    }

    [Fact]
    public void Compose_SelectionOverlayUsesProvidedTranslucentColor()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        tab.Session.Adapter.Buffer.SetCursor(0, 0);
        tab.Session.Adapter.Buffer.WriteText("selected".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager(SilentPty.Create);
        var selection = tab.ActivePane.Selection;
        selection.StartSelection(0, 0);
        selection.UpdateSelection(0, 7);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        const byte alpha = 96;
        var selectionColor = new SgrColorArgb((uint)(alpha << 24 | 0x12 << 16 | 0x34 << 8 | 0x56));

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            selectionColor,
            400,
            160,
            10,
            20,
            1,
            4,
            20,
            NoPadding(),
            false,
            false,
            false,
            false,
            new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            null);

        Assert.Contains(frame.Instances.AsSpan(0, frame.InstanceCount).ToArray(), instance =>
            instance.BgR == 0x12 && instance.BgG == 0x34 && instance.BgB == 0x56 && instance.BgA == alpha);
    }

    [Fact]
    public void Compose_SelectionOverlayMapsNegativeScrollbackRowsToVisibleRows()
    {
        using var tab = new TerminalTab(rows: 4, columns: 8);
        var pane = tab.ActivePane;
        for (int row = 0; row < 4; row++)
        {
            pane.Session.Adapter.Buffer.SetCursor(row, 0);
            pane.Session.Adapter.Buffer.WriteText($"line {row}".AsSpan(), CellAttributes.Default);
        }

        pane.Session.Adapter.Buffer.ScrollUpLines(1);
        pane.ScrollTo(1, pane.Session.Adapter.Buffer.ScrollbackCount);
        pane.Selection.StartSelection(-1, 7);
        pane.Selection.UpdateSelection(-1, 7);

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var selectionColor = new SgrColorArgb(0xFF112233);
        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            selectionColor,
            framebufferWidth: 160,
            framebufferHeight: 80,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: 4,
            columns: 8,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);

        Assert.Contains(frame.AsSpan().ToArray(), instance =>
            instance.Col == 7 && instance.Row == 0
            && instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33);
    }

    [Fact]
    public void Compose_RendersIndependentSelectionsForInactiveSplitPanes()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 8);
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        first.Session.Adapter.Buffer.SetCursor(0, 0);
        first.Session.Adapter.Buffer.WriteText("first".AsSpan(), CellAttributes.Default);
        second.Session.Adapter.Buffer.SetCursor(1, 0);
        second.Session.Adapter.Buffer.WriteText("second".AsSpan(), CellAttributes.Default);
        first.Selection.StartSelection(0, 0);
        second.Selection.StartSelection(1, 1);

        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var selectionColor = new SgrColorArgb(0xFF112233);

        TerminalSceneFrame ComposeFor(LeafPane activePane)
        {
            tab.PaneTree.ActivePane = activePane;
            return composer.Compose(
                tab,
                manager,
                theme,
                new SgrColorArgb(theme.Foreground),
                selectionColor,
                framebufferWidth: 160,
                framebufferHeight: 80,
                cellWidth: 10,
                cellHeight: 20,
                scale: 1,
                rows: 4,
                columns: 16,
                padding: NoPadding(),
                showTabBar: false,
                cursorVisible: false,
                scrollbarHovered: false,
                scrollbarDragging: false,
                searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
                activeContextMenu: null);
        }

        var firstFrame = ComposeFor(first);
        int firstColumn = (int)Math.Round(first.Bounds.X / 10f);
        int secondColumn = (int)Math.Round(second.Bounds.X / 10f);
        var firstInstances = firstFrame.AsSpan().ToArray();
        Assert.Contains(firstInstances, instance =>
            instance.Col == firstColumn && instance.Row == 0
            && instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33);
        Assert.Contains(firstInstances, instance =>
            instance.Col == secondColumn + 1 && instance.Row == 1
            && instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33);

        var secondFrame = ComposeFor(second);
        int selectedCount = 0;
        foreach (var instance in secondFrame.AsSpan())
        {
            if (instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33)
                selectedCount++;
        }

        Assert.Equal(2, selectedCount);
        var secondInstances = secondFrame.AsSpan().ToArray();
        Assert.Contains(secondInstances, instance =>
            instance.Col == firstColumn && instance.Row == 0
            && instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33);
        Assert.Contains(secondInstances, instance =>
            instance.Col == secondColumn + 1 && instance.Row == 1
            && instance.BgR == 0x11 && instance.BgG == 0x22 && instance.BgB == 0x33);
    }

    [Fact]
    public void Compose_SplitTabEmitsConfiguredDividerAsUnderlayChrome()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 8);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var panes = UserConfigService.Current.Panes;
        const float scale = 1.5f;
        float thickness = Dotty.Runtime.Rendering.PaneDecorationBuilder.ResolveThickness(panes.DividerThickness, scale);

        TerminalSceneFrame Compose() => composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            framebufferWidth: 480,
            framebufferHeight: 160,
            cellWidth: 10,
            cellHeight: 20,
            scale: scale,
            rows: 4,
            columns: 32,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);

        Assert.Equal(0, Compose().UnderlayChromeCount);

        var left = tab.ActivePane;
        var right = tab.PaneTree.Split(left, SplitDirection.Vertical);
        var frame = Compose();

        var split = (SplitPaneNode)tab.PaneTree.Root;
        Assert.Equal(thickness, split.DividerBounds.Width);
        Assert.Equal(thickness, right.Bounds.X - left.Bounds.Right);
        if (thickness == 0f)
        {
            Assert.Equal(0, frame.UnderlayChromeCount);
            return;
        }

        int expectedQuads = panes.ActiveBorder ? 2 : 1;
        Assert.Equal(expectedQuads, frame.UnderlayChromeCount);
        Assert.InRange(frame.UnderlayChromeCount, 0, frame.ChromeQuadCount);
        var divider = frame.ChromeQuads[0];
        Assert.Equal(MathF.Floor(split.DividerBounds.X + 0.5f), divider.X);
        Assert.Equal(thickness, divider.W);
        Assert.Equal(160f, divider.H);
    }

    private static TerminalSceneFrame ComposePlain(
        TerminalSceneComposer composer, TerminalTab tab, TerminalTabManager manager, IColorScheme theme,
        int width, int height, int rows, int columns) => composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            framebufferWidth: width,
            framebufferHeight: height,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: rows,
            columns: columns,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null);

    private static void FeedVt(LeafPane pane, string text) =>
        pane.Session.Parser.Feed(System.Text.Encoding.UTF8.GetBytes(text));

    private static bool HasRgb(ChromeQuadInstance quad, uint rgb) =>
        quad.TopR == ((rgb >> 16) & 0xFF) / 255f
        && quad.TopG == ((rgb >> 8) & 0xFF) / 255f
        && quad.TopB == (rgb & 0xFF) / 255f
        && quad.TopA == 1f;

    private static int CountUnderlay(TerminalSceneFrame frame, uint rgb)
    {
        int count = 0;
        for (int i = 0; i < frame.UnderlayChromeCount; i++)
        {
            if (HasRgb(frame.ChromeQuads[i], rgb))
                count++;
        }
        return count;
    }

    [Fact]
    public void Compose_Osc11PaintsOnlyItsOwnPaneBackgroundUntilOsc111()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 8);
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        const uint paneBackground = 0x204060;

        var plain = ComposePlain(composer, tab, manager, theme, 160, 80, 4, 16);
        Assert.Equal(0, CountUnderlay(plain, paneBackground));
        int decorationQuads = plain.UnderlayChromeCount;

        FeedVt(second, "\u001b]11;#204060\u0007");
        var frame = ComposePlain(composer, tab, manager, theme, 160, 80, 4, 16);

        Assert.Equal(1, CountUnderlay(frame, paneBackground));
        Assert.Equal(decorationQuads + 1, frame.UnderlayChromeCount);
        ChromeQuadInstance quad = default;
        for (int i = 0; i < frame.UnderlayChromeCount; i++)
        {
            if (HasRgb(frame.ChromeQuads[i], paneBackground))
                quad = frame.ChromeQuads[i];
        }
        // Covers the second pane (and its cell grid), never a cell of the first pane.
        Assert.True(quad.X <= second.Bounds.X);
        Assert.True(quad.X >= first.Bounds.X + first.Columns * 10f);
        Assert.True(quad.X + quad.W >= second.Bounds.Right - 0.5f);
        Assert.Equal(0f, quad.Y);
        Assert.Equal(80f, quad.H);
        // Pane backgrounds come first so dividers and the active ring stay visible on top of them.
        Assert.True(HasRgb(frame.ChromeQuads[0], paneBackground));

        FeedVt(second, "\u001b]111\u0007");
        var reset = ComposePlain(composer, tab, manager, theme, 160, 80, 4, 16);
        Assert.Equal(0, CountUnderlay(reset, paneBackground));
        Assert.Equal(decorationQuads, reset.UnderlayChromeCount);
    }

    [Fact]
    public void Compose_ReverseVideoSwapsEffectiveDefaultColorsIncludingOscOverrides()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 20);
        var pane = tab.ActivePane;
        pane.Session.Adapter.Buffer.SetCursor(0, 0);
        pane.Session.Adapter.Buffer.WriteText("hi".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        uint themeForeground = theme.Foreground & 0xFFFFFF;
        uint themeBackground = theme.Background & 0xFFFFFF;
        Assert.NotEqual(themeForeground, themeBackground);

        CellInstance GlyphAtOrigin(TerminalSceneFrame frame)
        {
            foreach (var instance in frame.AsSpan())
            {
                if (instance.Row == 0 && instance.Col == 0)
                    return instance;
            }
            throw new Xunit.Sdk.XunitException("no instance at (0, 0)");
        }

        static uint Fg(CellInstance instance) => (uint)(instance.FgR << 16 | instance.FgG << 8 | instance.FgB);

        var normal = ComposePlain(composer, tab, manager, theme, 200, 80, 4, 20);
        Assert.Equal(0, normal.UnderlayChromeCount);
        Assert.Equal(themeForeground, Fg(GlyphAtOrigin(normal)));

        FeedVt(pane, "\u001b[?5h");
        var reversed = ComposePlain(composer, tab, manager, theme, 200, 80, 4, 20);
        Assert.Equal(1, reversed.UnderlayChromeCount);
        Assert.True(HasRgb(reversed.ChromeQuads[0], themeForeground));
        Assert.Equal(new[] { 0f, 0f, 200f, 80f },
            new[] { reversed.ChromeQuads[0].X, reversed.ChromeQuads[0].Y, reversed.ChromeQuads[0].W, reversed.ChromeQuads[0].H });
        Assert.Equal(themeBackground, Fg(GlyphAtOrigin(reversed)));

        FeedVt(pane, "\u001b]10;#102030\u0007\u001b]11;#405060\u0007");
        var overridden = ComposePlain(composer, tab, manager, theme, 200, 80, 4, 20);
        Assert.True(HasRgb(overridden.ChromeQuads[0], 0x102030));
        Assert.Equal(0x405060u, Fg(GlyphAtOrigin(overridden)));

        FeedVt(pane, "\u001b[?5l");
        var restored = ComposePlain(composer, tab, manager, theme, 200, 80, 4, 20);
        Assert.True(HasRgb(restored.ChromeQuads[0], 0x405060));
        Assert.Equal(0x102030u, Fg(GlyphAtOrigin(restored)));
    }

    [Fact]
    public void Compose_HoveredScrollbarAddsTrackAndAccentChrome()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        var pane = tab.ActivePane;
        for (int row = 0; row < 4; row++)
        {
            pane.Session.Adapter.Buffer.SetCursor(row, 0);
            pane.Session.Adapter.Buffer.WriteText($"line {row}".AsSpan(), CellAttributes.Default);
        }
        pane.Session.Adapter.Buffer.ScrollUpLines(2);
        Assert.Equal(2, pane.Session.Adapter.Buffer.ScrollbackCount);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            200,
            80,
            10,
            20,
            1,
            4,
            20,
            NoPadding(),
            false,
            false,
            true,
            false,
            new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            null);

        Assert.Equal(2, frame.ChromeQuadCount);
        Assert.Equal(0, frame.ScrollbarChromeStart);
        Assert.Equal(-1, frame.MenuChromeStart);
        Assert.Equal(80f, frame.ChromeQuads[frame.ScrollbarChromeStart].H);
        Assert.True(frame.ChromeQuads[0].W > frame.ChromeQuads[1].W);
        Assert.True(frame.ChromeQuads[1].W > 4f);
    }

    [Fact]
    public void Compose_ContextMenuRecordsOverlayRangesAfterBaseScene()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var menu = new ContextMenuModel(
            x: 40f,
            y: 20f,
            items: new List<ContextMenuItem>
            {
                new("copy", "Copy", "Ctrl+C", null),
                ContextMenuItem.Separator(),
                new("close", "Close", "Ctrl+W", null)
            });

        var frame = composer.Compose(
            tab,
            manager,
            theme,
            new SgrColorArgb(theme.Foreground),
            new SgrColorArgb(0x803385DB),
            framebufferWidth: 400,
            framebufferHeight: 160,
            cellWidth: 10,
            cellHeight: 20,
            scale: 1,
            rows: 4,
            columns: 20,
            padding: NoPadding(),
            showTabBar: false,
            cursorVisible: false,
            scrollbarHovered: false,
            scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: menu);

        Assert.InRange(frame.MenuInstanceStart, 0, frame.InstanceCount - 1);
        Assert.InRange(frame.MenuChromeStart, 0, frame.ChromeQuadCount - 1);
        Assert.True(frame.MenuInstanceStart < frame.InstanceCount);
        Assert.True(frame.MenuChromeStart < frame.ChromeQuadCount);
    }


    [Theory]
    [InlineData("DarkPlus")]
    [InlineData("LightPlus")]
    public void Compose_PreeditRendersOpaqueThemeColouredCellsWrapsAndShiftsWithoutChangingTerminal(string themeName)
    {
        using var tab = new TerminalTab(rows: 4, columns: 10);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = themeName == "DarkPlus" ? BuiltInThemes.DarkPlus : BuiltInThemes.LightPlus;
        var selection = new SgrColorArgb(0x803385DB);
        tab.Session.Adapter.Buffer.SetCursor(0, 2);

        TerminalSceneFrame Compose(ImePreeditState? preedit) => composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), selection,
            framebufferWidth: 100, framebufferHeight: 80, cellWidth: 10, cellHeight: 20, scale: 1,
            rows: 4, columns: 10, padding: NoPadding(), showTabBar: false, cursorVisible: false,
            scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null, preeditState: preedit);

        // The empty terminal has no opaque cell backgrounds, so BgA == 255 identifies the preedit cells.
        static (int Col, int Row)[] PreeditCells(TerminalSceneFrame frame) =>
            frame.AsSpan().ToArray().Where(i => i.BgA == 255).Select(i => ((int)i.Col, (int)i.Row)).ToArray();

        // Focused clause 界 (UTF-16 1..2) in selection colours; the rest underlined in the theme colours.
        var composed = new ImePreeditState("A界e\u0301🙂", 4, 1, 1);
        var frame = Compose(composed);
        var cells = frame.AsSpan().ToArray().Where(i => i.BgA == 255).ToArray();

        Assert.Equal(new[] { (2, 0), (3, 0), (5, 0), (6, 0) }, PreeditCells(frame));
        var foreground = new SgrColorArgb(theme.Foreground);
        var background = new SgrColorArgb(theme.Background);
        Assert.All(cells, c => Assert.Equal((foreground.R, foreground.G, foreground.B, (byte)255), (c.FgR, c.FgG, c.FgB, c.FgA)));
        Assert.Equal((selection.R, selection.G, selection.B), (cells[1].BgR, cells[1].BgG, cells[1].BgB));
        Assert.True((cells[1].Flags & CellFlags.Underline) == 0);
        Assert.All(new[] { cells[0], cells[2], cells[3] }, c =>
        {
            Assert.Equal((background.R, background.G, background.B), (c.BgR, c.BgG, c.BgB));
            Assert.True((c.Flags & CellFlags.Underline) != 0);
        });
        Assert.True((cells[1].Flags & CellFlags.WideCell) != 0);
        Assert.True((cells[3].Flags & CellFlags.WideCell) != 0);
        Assert.Equal(2, tab.Session.Adapter.Buffer.CursorCol);
        Assert.True(frame.ImeCaretBoundsValid);
        Assert.Equal(60f, frame.ImeCaretBounds.Left);

        // At the row end the text wraps onto the next row instead of being clipped.
        tab.Session.Adapter.Buffer.SetCursor(0, 8);
        frame = Compose(new ImePreeditState("A界B", 3, 0, 0));
        Assert.Equal(new[] { (8, 0), (0, 1), (2, 1) }, PreeditCells(frame));
        Assert.Equal((30f, 20f), (frame.ImeCaretBounds.Left, frame.ImeCaretBounds.Top));
        Assert.Equal(8, tab.Session.Adapter.Buffer.CursorCol);

        // On the last row the wrapped text is shifted up over the previous row.
        tab.Session.Adapter.Buffer.SetCursor(3, 8);
        frame = Compose(new ImePreeditState("A界B", 3, 0, 0));
        Assert.Equal(new[] { (8, 2), (0, 3), (2, 3) }, PreeditCells(frame));
        Assert.Equal((30f, 60f), (frame.ImeCaretBounds.Left, frame.ImeCaretBounds.Top));
        Assert.Equal(3, tab.Session.Adapter.Buffer.CursorRow);

        frame = Compose(null);
        Assert.Empty(PreeditCells(frame));
        Assert.True(frame.ImeCaretBoundsValid);
        Assert.Equal(80f, frame.ImeCaretBounds.Left);
    }
}
