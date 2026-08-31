using System;
using System.Collections.Generic;
using System.Numerics;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Input;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalMouseControllerTests
{
    private sealed class FakeMouse : IMouse
    {
        public string Name => "FakeMouse";
        public int Index => 0;
        public bool IsConnected => true;
        public IReadOnlyList<MouseButton> SupportedButtons => new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle };
        public IReadOnlyList<ScrollWheel> ScrollWheels => Array.Empty<ScrollWheel>();
        public Vector2 Position { get; set; }
        public ICursor Cursor { get; } = null!;
        public int DoubleClickTime { get; set; }
        public int DoubleClickRange { get; set; }

        public event Action<IMouse, MouseButton>? MouseDown;
        public event Action<IMouse, MouseButton>? MouseUp;
        public event Action<IMouse, Vector2>? MouseMove;
        public event Action<IMouse, ScrollWheel>? Scroll;
        public event Action<IMouse, MouseButton, Vector2>? Click;
        public event Action<IMouse, MouseButton, Vector2>? DoubleClick;

        public bool IsButtonPressed(MouseButton btn) => false;
    }

    private sealed class FakeTerminalMouseHost : ITerminalMouseHost, IDisposable
    {
        public TerminalTabManager TabManager { get; } = new();
        public TerminalTab? ActiveTab => TabManager.ActiveTab;
        public TextSelectionService SelectionService { get; } = new();
        public ContextMenuModel? ActiveContextMenu { get; set; }

        public TerminalMouseGeometry Geometry { get; set; } = new(
            Scale: 1.0f,
            CellWidth: 10.0f,
            CellHeight: 20.0f,
            PaddingLeft: 0f,
            PaddingTop: 0f,
            TopOffset: 20.0f,
            FramebufferWidth: 800f,
            FramebufferHeight: 600f,
            Columns: 80,
            Rows: 24,
            ShowTabBar: true);

        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }
        public bool Super { get; set; }

        public bool CopyCalled { get; private set; }
        public bool PasteCalled { get; private set; }
        public bool CreateTabCalled { get; private set; }
        public bool ClearTerminalCalled { get; private set; }
        public string? OpenedHyperlink { get; private set; }
        public StandardCursor CurrentCursor { get; private set; } = StandardCursor.Default;

        public void CopySelection() => CopyCalled = true;
        public void PasteClipboard() => PasteCalled = true;
        public void CreateTab(TerminalTab activeTab) => CreateTabCalled = true;
        public void ClearTerminal(TerminalTab activeTab) => ClearTerminalCalled = true;
        public void OpenHyperlink(string url) => OpenedHyperlink = url;
        public void SetPointerCursor(StandardCursor cursor) => CurrentCursor = cursor;

        public void Dispose()
        {
            TabManager.Dispose();
        }
    }

    [Fact]
    public void IsInScrollbarHitArea_CalculatesCorrectHitTarget()
    {
        float cellWidth = 10f;
        float scale = 1f;
        float boundsWidth = 800f;

        // Expanded target is max(10 * 1, 14 * 1) = 14px
        // Hit area is [800 - 14, 800] = [786, 800]
        Assert.True(TerminalMouseController.IsInScrollbarHitArea(790f, boundsWidth, cellWidth, scale));
        Assert.True(TerminalMouseController.IsInScrollbarHitArea(786f, boundsWidth, cellWidth, scale));
        Assert.True(TerminalMouseController.IsInScrollbarHitArea(800f, boundsWidth, cellWidth, scale));
        Assert.False(TerminalMouseController.IsInScrollbarHitArea(785.9f, boundsWidth, cellWidth, scale));
        Assert.False(TerminalMouseController.IsInScrollbarHitArea(801f, boundsWidth, cellWidth, scale));
        Assert.False(TerminalMouseController.IsInScrollbarHitArea(100f, boundsWidth, cellWidth, scale));
    }

    [Fact]
    public void IsInScrollbarHitArea_WithLargeCellWidth_UsesCellWidth()
    {
        float cellWidth = 20f;
        float scale = 1f;
        float boundsWidth = 800f;

        // max(20, 14) = 20px -> [780, 800]
        Assert.True(TerminalMouseController.IsInScrollbarHitArea(781f, boundsWidth, cellWidth, scale));
        Assert.False(TerminalMouseController.IsInScrollbarHitArea(779f, boundsWidth, cellWidth, scale));
    }

    [Fact]
    public void ScrollProgressAndOffsetCalculation_ComputesExpectedRanges()
    {
        float height = 480f;
        int rows = 24;
        int scrollback = 100;

        // Top (relY = 0) -> progress 0.0 -> offset 100
        float topProgress = TerminalMouseController.CalculateScrollProgress(0f, height, rows);
        Assert.Equal(0f, topProgress);
        Assert.Equal(100, TerminalMouseController.CalculateTargetOffset(topProgress, scrollback));

        // Bottom (relY = 480) -> progress 1.0 -> offset 0
        float botProgress = TerminalMouseController.CalculateScrollProgress(480f, height, rows);
        Assert.Equal(1f, botProgress);
        Assert.Equal(0, TerminalMouseController.CalculateTargetOffset(botProgress, scrollback));

        // Middle (relY = 240) -> progress 0.5 -> offset 50
        float midProgress = TerminalMouseController.CalculateScrollProgress(240f, height, rows);
        Assert.Equal(0.5f, midProgress);
        Assert.Equal(50, TerminalMouseController.CalculateTargetOffset(midProgress, scrollback));
    }

    [Fact]
    public void HandleMouseDown_OnScrollbarStrip_EngagesDragAndSetsHandCursor()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        // Populate scrollback in active pane
        for (int i = 0; i < 50; i++)
        {
            tab.ActivePane.Session.Adapter.Buffer.ScrollUpLines(1);
        }
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(795f, 100f) }; // TopOffset is 20, localY is 80

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.True(controller.IsDraggingScrollbar);
        Assert.True(controller.LeftMouseDown);
        Assert.True(controller.IsScrollbarHovered);
        Assert.Equal(StandardCursor.Hand, host.CurrentCursor);
        Assert.False(host.SelectionService.HasSelection);
    }

    [Fact]
    public void HandleMouseMove_HoverOverScrollbar_SetsHoverAndHandCursor()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        // Populate scrollback
        tab.ActivePane.Session.Adapter.Buffer.ScrollUpLines(10);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse();

        // Hover over scrollbar strip
        controller.HandleMouseMove(mouse, new Vector2(795f, 50f));

        Assert.True(controller.IsScrollbarHovered);
        Assert.Equal(StandardCursor.Hand, host.CurrentCursor);

        // Move away from scrollbar strip
        controller.HandleMouseMove(mouse, new Vector2(100f, 50f));

        Assert.False(controller.IsScrollbarHovered);
        Assert.Equal(StandardCursor.IBeam, host.CurrentCursor);
    }

    [Fact]
    public void HandleMouseMove_ContinuousDrag_UpdatesScrollOffset()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        for (int i = 0; i < 100; i++)
        {
            tab.ActivePane.Session.Adapter.Buffer.ScrollUpLines(1);
        }
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(795f, 20f) }; // top (physY=20, TopOffset=20 -> localY=0)

        controller.HandleMouseDown(mouse, MouseButton.Left);
        Assert.True(controller.IsDraggingScrollbar);
        Assert.Equal(100, tab.ScrollOffset);

        // Drag to halfway down (physY = 260 -> localY = 240 / 480 = 0.5)
        controller.HandleMouseMove(mouse, new Vector2(795f, 260f));
        Assert.Equal(50, tab.ScrollOffset);
        Assert.Equal(StandardCursor.Hand, host.CurrentCursor);

        // Release mouse
        controller.HandleMouseUp(mouse, MouseButton.Left);
        Assert.False(controller.IsDraggingScrollbar);
        Assert.False(controller.LeftMouseDown);
    }

    [Fact]
    public void HandleMouseDown_SingleClickStartsSelection_AndMouseMoveExtendsSelection()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) }; // localX=50 (col 5), localY=40 (row 2)

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.False(controller.IsDraggingScrollbar);
        Assert.True(controller.LeftMouseDown);
        Assert.True(host.SelectionService.HasSelection);
        Assert.Equal(SelectionMode.Character, host.SelectionService.Mode);
        Assert.Equal(2, host.SelectionService.AnchorRow);
        Assert.Equal(5, host.SelectionService.AnchorColumn);

        // Drag to col 10, row 3
        controller.HandleMouseMove(mouse, new Vector2(100f, 80f));
        Assert.Equal(3, host.SelectionService.ActiveRow);
        Assert.Equal(10, host.SelectionService.ActiveColumn);
    }

    [Fact]
    public void HandleMouseDown_DoubleClick_SelectsWholeLine()
    {
        long currentTime = 1000;
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host, () => currentTime);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        // First click
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        Assert.Equal(SelectionMode.Character, host.SelectionService.Mode);

        // Second click within double-click window
        currentTime += 100;
        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.True(host.SelectionService.HasSelection);
        Assert.Equal(SelectionMode.Line, host.SelectionService.Mode);
        Assert.Equal(2, host.SelectionService.AnchorRow);
        Assert.Equal(0, host.SelectionService.AnchorColumn);
        Assert.Equal(79, host.SelectionService.ActiveColumn);
    }

    [Fact]
    public void HandleMouseDown_ActiveContextMenu_TakesHighestPrecedence()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        bool itemClicked = false;
        var menu = new ContextMenuModel(10f, 10f, new[]
        {
            new ContextMenuItem("action", "Action", action: () => itemClicked = true)
        });
        host.ActiveContextMenu = menu;

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(20f, 20f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.True(itemClicked);
        Assert.Null(host.ActiveContextMenu);
        Assert.False(host.SelectionService.HasSelection);
    }

    [Fact]
    public void HandleMouseDown_RightClick_OpensContextMenu()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(100f, 100f) };

        controller.HandleMouseDown(mouse, MouseButton.Right);

        Assert.NotNull(host.ActiveContextMenu);
        Assert.True(host.ActiveContextMenu.Items.Count > 0);
    }
}
