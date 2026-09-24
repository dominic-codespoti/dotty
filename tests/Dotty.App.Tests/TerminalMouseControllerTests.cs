using Dotty.Abstractions.Config;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Input;
using Dotty.Terminal.Adapter;
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

        public event Action<IMouse, MouseButton>? MouseDown
        {
            add { }
            remove { }
        }
        public event Action<IMouse, MouseButton>? MouseUp
        {
            add { }
            remove { }
        }
        public event Action<IMouse, Vector2>? MouseMove
        {
            add { }
            remove { }
        }
        public event Action<IMouse, ScrollWheel>? Scroll
        {
            add { }
            remove { }
        }
        public event Action<IMouse, MouseButton, Vector2>? Click
        {
            add { }
            remove { }
        }
        public event Action<IMouse, MouseButton, Vector2>? DoubleClick
        {
            add { }
            remove { }
        }

        public bool IsButtonPressed(MouseButton btn) => false;
    }

    internal sealed class FakeTerminalMouseHost : ITerminalMouseHost, IDisposable
    {
        public TerminalTabManager TabManager { get; } = new();
        public TerminalTab? ActiveTab => TabManager.ActiveTab;
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
        public LeafPane? PasteTarget { get; private set; }
        public StandardCursor CurrentCursor { get; private set; } = StandardCursor.Default;

        public void CopySelection() => CopyCalled = true;
        public void PasteClipboard() => PasteCalled = true;
        public void PasteClipboard(LeafPane targetPane)
        {
            PasteCalled = true;
            PasteTarget = targetPane;
        }
        public void CreateTab(TerminalTab activeTab) => CreateTabCalled = true;
        public void ClearTerminal(TerminalTab activeTab) => ClearTerminalCalled = true;
        public void OpenHyperlink(string url) => OpenedHyperlink = url;
        public void SetPointerCursor(StandardCursor cursor) => CurrentCursor = cursor;
        public bool TryExecuteAction(TerminalAction action) => true;

        public void Dispose()
        {
            TabManager.Dispose();
        }
    }

    private static void EnableMouseMode(LeafPane pane, int mode)
    {
        TestShellEnvironment.WaitForStartupOutput(pane.Session);
        pane.Session.Parser.Feed(Encoding.ASCII.GetBytes($"\u001b[?{mode}h"));
    }

    private static void Feed(LeafPane pane, string text)
    {
        TestShellEnvironment.WaitForStartupOutput(pane.Session);
        pane.Session.Parser.Feed(Encoding.UTF8.GetBytes(text));
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
        Assert.False(tab.ActivePane.Selection.HasSelection);
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
        Assert.Equal(100, tab.ActivePane.ScrollOffset);

        // Drag to halfway down (physY = 260 -> localY = 240 / 480 = 0.5)
        controller.HandleMouseMove(mouse, new Vector2(795f, 260f));
        Assert.Equal(50, tab.ActivePane.ScrollOffset);
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
        Assert.True(tab.ActivePane.Selection.HasSelection);
        Assert.Equal(SelectionMode.Character, tab.ActivePane.Selection.Mode);
        Assert.Equal(2, tab.ActivePane.Selection.AnchorRow);
        Assert.Equal(5, tab.ActivePane.Selection.AnchorColumn);

        // Drag to col 10, row 3
        controller.HandleMouseMove(mouse, new Vector2(100f, 80f));
        Assert.Equal(10, tab.ActivePane.Selection.ActiveColumn);
    }

    [Fact]
    public void HandleMouseDown_LeftClickAllocatesNothingAfterWarmup()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        long now = 1000;
        var controller = new TerminalMouseController(host, () => now);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        for (int i = 0; i < 8; i++)
            Click();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            Click();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Click()
        {
            now += 500;
            controller.HandleMouseDown(mouse, MouseButton.Left);
            controller.HandleMouseUp(mouse, MouseButton.Left);
        }
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
        Assert.False(tab.ActivePane.Selection.HasSelection);
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
    [Fact]
    public void HandleMouseDown_SplitPane_UsesPaneLocalCoordinates()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        var rightPane = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(500f, 60f) }, MouseButton.Left);

        Assert.Same(rightPane, tab.ActivePane);
        Assert.Equal(2, rightPane.Selection.AnchorRow);
        Assert.Equal(9, rightPane.Selection.AnchorColumn);
        Assert.False(tab.PaneTree.Leaves[0].Selection.HasSelection);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.Right)]
    public void ReportingMode_ReportsPressAndReleaseWithoutLocalPointerActions(MouseButton button)
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        EnableMouseMode(tab.ActivePane, 1000);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, button);
        controller.HandleMouseUp(mouse, button);

        Assert.Equal(TerminalAdapter.MouseMode.Normal, tab.ActivePane.Session.Adapter.CurrentMouseMode);
        Assert.False(tab.ActivePane.Selection.HasSelection);
        Assert.Null(host.ActiveContextMenu);
        Assert.False(host.PasteCalled);
        Assert.False(controller.LeftMouseDown);
    }

    [Fact]
    public void ReportingMotion_ButtonEventRequiresPressedButton_WhileAnyEventReportsHover()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse();

        EnableMouseMode(tab.ActivePane, 1002);
        controller.HandleMouseMove(mouse, new Vector2(50f, 60f));
        Assert.Equal(TerminalAdapter.MouseMode.ButtonEvent, tab.ActivePane.Session.Adapter.CurrentMouseMode);
        Assert.Equal(StandardCursor.IBeam, host.CurrentCursor);

        controller.ResetState();
        host.SetPointerCursor(StandardCursor.Default);
        EnableMouseMode(tab.ActivePane, 1003);
        controller.HandleMouseMove(mouse, new Vector2(50f, 60f));
        Assert.Equal(TerminalAdapter.MouseMode.AnyEvent, tab.ActivePane.Session.Adapter.CurrentMouseMode);
        Assert.Equal(StandardCursor.Default, host.CurrentCursor);
    }

    [Fact]
    public void Shift_OverridesMouseReportingAndKeepsLocalSelection()
    {
        using var host = new FakeTerminalMouseHost { Shift = true };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        EnableMouseMode(tab.ActivePane, 1003);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(50f, 60f) }, MouseButton.Left);

        Assert.True(tab.ActivePane.Selection.HasSelection);
        Assert.Equal(SelectionMode.Character, tab.ActivePane.Selection.Mode);
        Assert.True(controller.LeftMouseDown);
    }

    [Fact]
    public void MiddleClick_PastesIntoPaneUnderPointer()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        var rightPane = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(500f, 60f) }, MouseButton.Middle);

        Assert.True(host.PasteCalled);
        Assert.Same(rightPane, host.PasteTarget);
        Assert.Same(rightPane, tab.ActivePane);
    }

    [Fact]
    public void DoubleClick_SelectsWord_AndTripleClickSelectsLine()
    {
        long now = 1000;
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        Feed(tab.ActivePane, "hello world");

        var controller = new TerminalMouseController(host, () => now);
        var mouse = new FakeMouse { Position = new Vector2(15f, 30f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        now += 100;
        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.Equal(SelectionMode.Word, tab.ActivePane.Selection.Mode);
        Assert.Equal(0, tab.ActivePane.Selection.AnchorRow);
        Assert.Equal(0, tab.ActivePane.Selection.AnchorColumn);
        Assert.Equal(4, tab.ActivePane.Selection.ActiveColumn);

        now += 100;
        controller.HandleMouseDown(mouse, MouseButton.Left);
        Assert.Equal(SelectionMode.Line, tab.ActivePane.Selection.Mode);
        Assert.Equal(0, tab.ActivePane.Selection.AnchorColumn);
        Assert.Equal(79, tab.ActivePane.Selection.ActiveColumn);
    }

    [Fact]
    public void Selection_UsesScrolledLogicalRows()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        for (int i = 0; i < 40; i++)
            Feed(tab.ActivePane, $"line-{i}\n");
        tab.ActivePane.ScrollUp(3, tab.ActivePane.Session.Adapter.Buffer.ScrollbackCount);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(50f, 60f) }, MouseButton.Left);

        Assert.Equal(-1, tab.ActivePane.Selection.AnchorRow);
        Assert.Equal(5, tab.ActivePane.Selection.AnchorColumn);
    }

    [Fact]
    public void StationarySelectionAutoscrollsUpAndDown_AndStopsAtBounds()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        for (int i = 0; i < 100; i++)
            Feed(tab.ActivePane, $"line-{i}\n");

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(50f, 0f));
        Assert.True(controller.TickSelectionAutoscroll());
        Assert.True(tab.ActivePane.ScrollOffset > 0);

        tab.ActivePane.ScrollTo(tab.ActivePane.Session.Adapter.Buffer.ScrollbackCount, tab.ActivePane.Session.Adapter.Buffer.ScrollbackCount);
        int maxOffset = tab.ActivePane.ScrollOffset;
        controller.HandleMouseMove(mouse, new Vector2(50f, 0f));
        controller.TickSelectionAutoscroll();
        Assert.Equal(maxOffset, tab.ActivePane.ScrollOffset);
        Assert.False(controller.TickSelectionAutoscroll());

        tab.ActivePane.ScrollTo(1, tab.ActivePane.Session.Adapter.Buffer.ScrollbackCount);
        controller.HandleMouseMove(mouse, new Vector2(50f, 600f));
        Assert.True(controller.TickSelectionAutoscroll());
        Assert.Equal(0, tab.ActivePane.ScrollOffset);
        Assert.False(controller.TickSelectionAutoscroll());
    }

    [Fact]
    public void ResetState_ClearsCaptureHoverAndClickHistory()
    {
        long now = 1000;
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        for (int i = 0; i < 30; i++)
            Feed(tab.ActivePane, $"line-{i}\n");

        var controller = new TerminalMouseController(host, () => now);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        mouse.Position = new Vector2(795f, 50f);
        controller.HandleMouseMove(mouse, mouse.Position);
        controller.HandleMouseDown(mouse, MouseButton.Left);
        Assert.True(controller.IsDraggingScrollbar);
        Assert.True(controller.LeftMouseDown);

        controller.ResetState();
        Assert.False(controller.LeftMouseDown);
        Assert.False(controller.IsDraggingScrollbar);
        Assert.False(controller.IsScrollbarHovered);
        Assert.Equal(-1, controller.HoveredTabIndex);
        Assert.Equal(TabBarHitType.None, controller.HoveredTabHitType);

        mouse.Position = new Vector2(50f, 60f);
        controller.HandleMouseDown(mouse, MouseButton.Left);
        Assert.Equal(SelectionMode.Character, tab.ActivePane.Selection.Mode);
    }

    [Fact]
    public void WheelAndScrollbarOperateOnPaneUnderPointer()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        var rightPane = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);
        tab.PaneTree.Layout(800, 480, 10, 20);
        foreach (var pane in tab.PaneTree.Leaves)
        {
            for (int i = 0; i < 50; i++)
                Feed(pane, $"line-{i}\n");
        }

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(500f, 60f) };
        controller.HandleMouseScroll(mouse, new ScrollWheel(0f, 1f));
        Assert.True(rightPane.ScrollOffset > 0);
        Assert.Equal(0, tab.PaneTree.Leaves[0].ScrollOffset);

        mouse.Position = new Vector2(795f, 100f);
        controller.HandleMouseDown(mouse, MouseButton.Left);
        Assert.Same(rightPane, tab.ActivePane);
        Assert.True(controller.IsDraggingScrollbar);
        Assert.Equal(0, tab.PaneTree.Leaves[0].ScrollOffset);
    }

}
