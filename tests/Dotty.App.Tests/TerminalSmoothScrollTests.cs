using System.Collections.Generic;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Config;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Search;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalSmoothScrollTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SmoothScrollComposesDisjointOutgoingAndIncomingRegions(bool scrollUp)
    {
        using var tab = new TerminalTab(rows: 4, columns: 8);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        using var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var noSearch = new SearchOverlayRenderState(false, string.Empty, -1, 0, null);

        if (scrollUp)
            tab.Session.Parser.Feed("A\r\nB\r\nC\r\nD\x1b[?4h\x1b[4;1H\r\nE"u8);
        else
            tab.Session.Parser.Feed("A\r\nB\r\nC\r\nD\x1b[?4h\x1b[1T"u8);
        var adapter = tab.Session.Adapter;
        Assert.True(adapter.SmoothScrollActive);
        Assert.NotNull(adapter.SmoothScrollOutgoingSnapshot);
        Assert.StartsWith("A", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));

        TerminalSceneFrame Render() => composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), new SgrColorArgb(0x803385DB),
            80, 80, 10, 20, 1, 4, 8,
            new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 }, showTabBar: false,
            cursorVisible: false, scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: noSearch, activeContextMenu: null);

        _ = Render();
        long animationStartMs = adapter.SmoothScrollStartedAtMs;
        Assert.True(adapter.AdvanceSmoothScrollFrame(animationStartMs + 10));
        var moving = Render();
        var runs = moving.AsTransformSpan().ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.True(run.Count > 0));
        Assert.Contains(runs, run => run.PixelOffsetY != 0f);
        if (scrollUp)
        {
            Assert.Contains(runs, run => run.PixelOffsetY < 0f
                && run.ClipTop == 0f && run.ClipBottom > 0f && run.ClipBottom < 20f);
            Assert.Contains(runs, run => run.PixelOffsetY > 0f
                && run.ClipTop > 0f && run.ClipBottom == 80f);
        }
        else
        {
            Assert.Contains(runs, run => run.PixelOffsetY > 0f
                && run.ClipTop > 0f && run.ClipBottom == 80f);
            Assert.Contains(runs, run => run.PixelOffsetY < 0f
                && run.ClipTop == 0f && run.ClipBottom < 80f);
        }

        // BuildRow already supplies the snapshot row; pane placement must not add that row a second time.
        ushort outgoingRow = (ushort)(scrollUp ? 0 : 3);
        foreach (ref readonly var cell in moving.AsSpan().Slice(runs[0].Start, runs[0].Count))
            Assert.Equal(outgoingRow, cell.Row);

        foreach (var run in runs)
        {
            Assert.InRange(run.Start, 0, moving.InstanceCount - 1);
            Assert.True(run.End <= moving.InstanceCount);
        }

        Assert.True(adapter.AdvanceSmoothScrollFrame(animationStartMs + 1_000));
        var settled = Render();
        Assert.Empty(settled.AsTransformSpan().ToArray());
        Assert.Null(adapter.SmoothScrollOutgoingSnapshot);
        Assert.False(adapter.AdvanceSmoothScrollFrame(animationStartMs + 1_001));
    }

    [Fact]
    public void SmoothScrollKeepsIndependentTransformsForSplitPanes()
    {
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 4, columns: 8);
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        using var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var theme = BuiltInThemes.DarkPlus;
        var noSearch = new SearchOverlayRenderState(false, string.Empty, -1, 0, null);
        var menu = new ContextMenuModel(
            x: 40f,
            y: 20f,
            items: new List<ContextMenuItem>
            {
                new("copy", "Copy", "Ctrl+C", null),
                ContextMenuItem.Separator(),
                new("close", "Close", "Ctrl+W", null)
            });

        // Establish pane sizes before creating transitions: a real resize deliberately cancels an old snapshot.
        _ = Render();
        first.Session.Parser.Feed("A\r\nB\r\nC\r\nD\x1b[?4h\x1b[4;1H\r\nE"u8);
        second.Session.Parser.Feed("W\r\nX\r\nY\r\nZ\x1b[?4h\x1b[1T"u8);
        var firstAdapter = first.Session.Adapter;
        var secondAdapter = second.Session.Adapter;
        Assert.Equal(-1, firstAdapter.SmoothScrollDirection);
        Assert.Equal(1, secondAdapter.SmoothScrollDirection);

        TerminalSceneFrame Render() => composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), new SgrColorArgb(0x803385DB),
            160, 80, 10, 20, 1, 4, 16,
            new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 }, showTabBar: false,
            cursorVisible: false, scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: noSearch, activeContextMenu: menu);

        _ = Render();
        long firstStart = firstAdapter.SmoothScrollStartedAtMs;
        long secondStart = secondAdapter.SmoothScrollStartedAtMs;
        Assert.True(firstAdapter.AdvanceSmoothScrollFrame(firstStart + 20));
        Assert.True(secondAdapter.AdvanceSmoothScrollFrame(secondStart + 80));
        TerminalSceneFrame moving = Render();
        CellTransformRange[] runs = moving.AsTransformSpan().ToArray();
        Assert.Equal(4, runs.Length);
        Assert.NotEqual(first.Bounds.X, second.Bounds.X);

        CellTransformRange[] RunsFor(LeafPane pane)
        {
            ushort startColumn = (ushort)Math.Round(pane.Bounds.X / 10f);
            ushort endColumn = (ushort)Math.Round((pane.Bounds.X + pane.Bounds.Width) / 10f);
            return runs.Where(run =>
            {
                ushort firstColumn = moving.Instances[run.Start].Col;
                return firstColumn >= startColumn && firstColumn < endColumn;
            }).ToArray();
        }

        var firstRuns = RunsFor(first);
        var secondRuns = RunsFor(second);
        Assert.Equal(2, firstRuns.Length);
        Assert.Equal(2, secondRuns.Length);
        Assert.True(firstRuns[0].PixelOffsetY < 0f);
        Assert.True(firstRuns[1].PixelOffsetY > 0f);
        Assert.True(secondRuns[0].PixelOffsetY > 0f);
        Assert.True(secondRuns[1].PixelOffsetY < 0f);
        Assert.Equal(firstAdapter.SmoothScrollProgress * 20f, Math.Abs(firstRuns[0].PixelOffsetY), 3);
        Assert.Equal(secondAdapter.SmoothScrollProgress * 20f, Math.Abs(secondRuns[0].PixelOffsetY), 3);
        Assert.NotEqual(Math.Abs(firstRuns[0].PixelOffsetY), Math.Abs(secondRuns[0].PixelOffsetY));
        Assert.Equal(0f, firstRuns[0].ClipTop);
        Assert.InRange(firstRuns[0].ClipBottom, 0f, 20f);
        Assert.InRange(firstRuns[1].ClipTop, 0f, 20f);
        Assert.Equal(80f, firstRuns[1].ClipBottom);
        Assert.InRange(secondRuns[0].ClipTop, 60f, 80f);
        Assert.Equal(80f, secondRuns[0].ClipBottom);
        Assert.Equal(0f, secondRuns[1].ClipTop);
        Assert.InRange(secondRuns[1].ClipBottom, 60f, 80f);
        Assert.All(runs, run => Assert.True(run.End <= moving.MenuInstanceStart));

        int previousEnd = 0;
        foreach (var run in runs)
        {
            Assert.True(run.Start >= previousEnd);
            Assert.True(run.Count > 0);
            Assert.True(run.End <= moving.InstanceCount);
            previousEnd = run.End;
            foreach (CellInstance cell in moving.AsSpan().Slice(run.Start, run.Count))
                Assert.InRange(cell.Col, (ushort)0, (ushort)15);
        }

        firstAdapter.OnSetSmoothScroll(false);
        Assert.Null(firstAdapter.SmoothScrollOutgoingSnapshot);
        TerminalSceneFrame afterDisable = Render();
        var retainedRuns = afterDisable.AsTransformSpan();
        Assert.Equal(2, retainedRuns.Length);
        ushort secondColumn = (ushort)Math.Round(second.Bounds.X / 10f);
        for (int i = 0; i < retainedRuns.Length; i++)
        {
            Assert.Equal(secondRuns[i].PixelOffsetY, retainedRuns[i].PixelOffsetY);
            Assert.Equal(secondRuns[i].ClipTop, retainedRuns[i].ClipTop);
            Assert.Equal(secondRuns[i].ClipBottom, retainedRuns[i].ClipBottom);
            foreach (CellInstance cell in afterDisable.AsSpan().Slice(retainedRuns[i].Start, retainedRuns[i].Count))
                Assert.InRange(cell.Col, secondColumn, (ushort)15);
        }

        secondAdapter.OnSetSmoothScroll(false);
        Assert.Empty(Render().AsTransformSpan().ToArray());
    }
}
