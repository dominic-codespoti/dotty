using System;
using System.Collections.Generic;
using Dotty.Runtime.ContextMenu;
using Xunit;

namespace Dotty.App.Tests;

public class ContextMenuTests
{
    [Fact]
    public void ContextMenuLayout_ComputesCorrectMenuDimensions()
    {
        bool itemClicked = false;
        var items = new List<ContextMenuItem>
        {
            new("split-v", "Split Right", "Ctrl+Shift+D", () => itemClicked = true),
            new("split-h", "Split Down", "Ctrl+Shift+S", null),
            ContextMenuItem.Separator(),
            new("close", "Close", "Ctrl+Shift+W", null)
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        Assert.NotNull(layout);
        Assert.Equal(4, layout.Items.Length);
        Assert.True(layout.Bounds.Width > 120f);
        Assert.True(layout.Bounds.Height > 80f);
        Assert.Equal(100f, layout.Bounds.X);
        Assert.Equal(50f, layout.Bounds.Y);
    }

    [Fact]
    public void ContextMenuLayout_AlignsShortcutsAndReservesSharedGap()
    {
        var items = new List<ContextMenuItem>
        {
            new("long", "A longer label", "Ctrl+Shift+E", null, icon: "◫"),
            new("short", "Short", "X", null, icon: "◫")
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        var longestShortcut = layout.Items[0];
        var shorterShortcut = layout.Items[1];

        Assert.Equal(longestShortcut.ShortcutBounds.Right, shorterShortcut.ShortcutBounds.Right);
        Assert.Equal(
            longestShortcut.ShortcutBounds.Left - ContextMenuLayout.DefaultShortcutGap,
            longestShortcut.LabelBounds.Right);
        Assert.True(shorterShortcut.LabelBounds.Right < shorterShortcut.ShortcutBounds.Left);
    }

    [Fact]
    public void ContextMenuHitTester_ClickingItem_ReturnsItemIndex()
    {
        bool itemClicked = false;
        var items = new List<ContextMenuItem>
        {
            new("copy", "Copy", "Ctrl+Shift+C", () => itemClicked = true),
            new("paste", "Paste", "Ctrl+Shift+V", null)
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        int hitIndex = ContextMenuHitTester.HitTest(layout, x: 120f, y: 65f);
        Assert.Equal(0, hitIndex);

        int outsideIndex = ContextMenuHitTester.HitTest(layout, x: 500f, y: 500f);
        Assert.Equal(-1, outsideIndex);
    }

    [Fact]
    public void DefaultContextMenus_BuildTabMenu_HasExpectedActions()
    {
        bool splitRight = false, close = false;
        var menu = DefaultContextMenus.BuildTabMenu(
            tabIndex: 0,
            onSplitRight: () => splitRight = true,
            onSplitDown: () => { },
            onRename: () => { },
            onClose: () => close = true);

        Assert.NotNull(menu);
        Assert.True(menu.Count >= 4);

        // Trigger Split Right action
        menu[0].Action?.Invoke();
        Assert.True(splitRight);
    }
}
