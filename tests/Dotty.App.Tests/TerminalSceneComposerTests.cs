using System;
using System.Collections.Generic;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
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

    private static TerminalSceneComposer CreateComposer(TextSelectionService selection, out GlyphAtlas atlas)
    {
        atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        return new TerminalSceneComposer(atlas, SKTypeface.Default, 14f, selection);
    }

    [Fact]
    public void Compose_WritesGlyphInstancesForActivePane()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        tab.Session.Adapter.Buffer.SetCursor(0, 0);
        tab.Session.Adapter.Buffer.WriteText("hello".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager();
        var selection = new TextSelectionService();
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f, selection);
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
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0),
            activeContextMenu: null);

        Assert.True(frame.InstanceCount > 0);
        Assert.Contains(frame.Instances.AsSpan(0, frame.InstanceCount).ToArray(), instance => instance.Row == 0);
    }

    [Fact]
    public void Compose_SelectionOverlayUsesProvidedTranslucentColor()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        tab.Session.Adapter.Buffer.SetCursor(0, 0);
        tab.Session.Adapter.Buffer.WriteText("selected".AsSpan(), CellAttributes.Default);
        using var manager = new TerminalTabManager();
        var selection = new TextSelectionService();
        selection.StartSelection(0, 0);
        selection.UpdateSelection(0, 7);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f, selection);
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
            new SearchOverlayRenderState(false, string.Empty, -1, 0),
            null);

        Assert.Contains(frame.Instances.AsSpan(0, frame.InstanceCount).ToArray(), instance =>
            instance.BgR == 0x12 && instance.BgG == 0x34 && instance.BgB == 0x56 && instance.BgA == alpha);
    }

    [Fact]
    public void Compose_HoveredScrollbarAddsTrackGroove()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        for (int row = 0; row < 4; row++)
        {
            tab.Session.Adapter.Buffer.SetCursor(row, 0);
            tab.Session.Adapter.Buffer.WriteText($"line {row}".AsSpan(), CellAttributes.Default);
        }
        tab.Session.Adapter.Buffer.ScrollUpLines(2);
        Assert.Equal(2, tab.Session.Adapter.Buffer.ScrollbackCount);
        using var manager = new TerminalTabManager();
        var selection = new TextSelectionService();
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f, selection);
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
            new SearchOverlayRenderState(false, string.Empty, -1, 0),
            null);
        Assert.Contains(frame.Instances.AsSpan(0, frame.InstanceCount).ToArray(), instance => instance.BgA == 50);
    }

    [Fact]
    public void Compose_ContextMenuRecordsOverlayRangesAfterBaseScene()
    {
        using var tab = new TerminalTab(rows: 4, columns: 20);
        using var manager = new TerminalTabManager();
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var selection = new TextSelectionService();
        var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f, selection);
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
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0),
            activeContextMenu: menu);

        Assert.InRange(frame.MenuInstanceStart, 0, frame.InstanceCount - 1);
        Assert.InRange(frame.MenuChromeStart, 0, frame.ChromeQuadCount - 1);
        Assert.True(frame.MenuInstanceStart < frame.InstanceCount);
        Assert.True(frame.MenuChromeStart < frame.ChromeQuadCount);
    }
}
