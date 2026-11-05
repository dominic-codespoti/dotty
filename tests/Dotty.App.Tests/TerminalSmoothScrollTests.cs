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

        CellInstance[] Render() => composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), new SgrColorArgb(0x803385DB),
            80, 80, 10, 20, 1, 4, 8,
            new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 }, showTabBar: false,
            cursorVisible: false, scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: noSearch, activeContextMenu: null).AsSpan().ToArray();

        _ = Render();
        long animationStartMs = adapter.SmoothScrollStartedAtMs;
        Assert.True(adapter.AdvanceSmoothScrollFrame(animationStartMs + 10));
        var moving = Render();
        Assert.Contains(moving, cell => cell.PixelOffsetY != 0f);
        if (scrollUp)
        {
            Assert.Contains(moving, cell => cell.PixelOffsetY < 0f
                && cell.ClipTop == 0f && cell.ClipBottom > 0f && cell.ClipBottom < 20f);
            Assert.Contains(moving, cell => cell.PixelOffsetY > 0f
                && cell.ClipTop > 0f && cell.ClipBottom == 80f);
        }
        else
        {
            Assert.Contains(moving, cell => cell.PixelOffsetY > 0f
                && cell.ClipTop > 0f && cell.ClipBottom == 80f);
            Assert.Contains(moving, cell => cell.PixelOffsetY < 0f
                && cell.ClipTop == 0f && cell.ClipBottom < 80f);
        }

        Assert.True(adapter.AdvanceSmoothScrollFrame(animationStartMs + 1_000));
        var settled = Render();
        Assert.All(settled, cell =>
        {
            Assert.Equal(0f, cell.PixelOffsetY);
            Assert.Equal(0f, cell.ClipTop);
            Assert.Equal(0f, cell.ClipBottom);
        });
        Assert.Null(adapter.SmoothScrollOutgoingSnapshot);
        Assert.False(adapter.AdvanceSmoothScrollFrame(animationStartMs + 1_001));
    }
}
