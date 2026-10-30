using Dotty.Abstractions.Config;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Input;
using Dotty.Terminal.Adapter;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class TerminalMouseControllerTests
{
    internal sealed class FakeMouse : IMouse
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
        public FakeTerminalMouseHost()
            : this(SilentPty.Create)
        {
        }

        public FakeTerminalMouseHost(Func<Dotty.Abstractions.Pty.IPty> ptyFactory) =>
            TabManager = new TerminalTabManager(ptyFactory);

        public TerminalTabManager TabManager { get; }
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
        public HyperlinkSource? OpenedHyperlinkSource { get; private set; }
        public string? OpenedHyperlinkText { get; private set; }
        public HyperlinkScanOptions HyperlinkScan { get; set; } = HyperlinkScanOptions.Default;
        public LeafPane? PasteTarget { get; private set; }
        public StandardCursor CurrentCursor { get; private set; } = StandardCursor.Default;

        public void CopySelection() => CopyCalled = true;
        public void PasteClipboard() => PasteCalled = true;
        public void PasteClipboard(LeafPane targetPane)
        {
            PasteCalled = true;
            PasteTarget = targetPane;
        }
        public MiddleClickAction MiddleClick { get; set; } = MiddleClickAction.ClipboardPaste;
        public MouseInputSettings MouseInput { get; set; } = MouseInputSettings.Default;
        public int PastePrimaryCalls { get; private set; }
        public LeafPane? PrimaryTarget { get; private set; }
        public int SelectionFinishedCalls { get; private set; }
        public LeafPane? SelectionFinishedPane { get; private set; }
        public void PastePrimary(LeafPane targetPane)
        {
            PastePrimaryCalls++;
            PrimaryTarget = targetPane;
        }
        public void OnLocalSelectionFinished(LeafPane pane)
        {
            SelectionFinishedCalls++;
            SelectionFinishedPane = pane;
        }
        public void CreateTab(TerminalTab activeTab) => CreateTabCalled = true;
        public void ClearTerminal(TerminalTab activeTab) => ClearTerminalCalled = true;
        public void OpenHyperlink(string url, HyperlinkSource source, string? displayText)
        {
            OpenedHyperlink = url;
            OpenedHyperlinkSource = source;
            OpenedHyperlinkText = displayText;
        }
        public void SetPointerCursor(StandardCursor cursor) => CurrentCursor = cursor;
        public bool TryExecuteAction(TerminalAction action) => true;

        public void Dispose()
        {
            TabManager.Dispose();
        }
    }

    private static void EnableMouseMode(LeafPane pane, int mode)
    {
        pane.Session.Parser.Feed(Encoding.ASCII.GetBytes($"\u001b[?{mode}h"));
    }

    private static void Feed(LeafPane pane, string text)
    {
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
    public void HandleMouseDown_CtrlClickOnOscEightLink_ActivatesWithSourceAndVisibleText()
    {
        using var host = new FakeTerminalMouseHost { Ctrl = true };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        Feed(tab.ActivePane, "\u001b]8;;https://evil.test/\u001b\\https://paypal.com\u001b]8;;\u001b\\ tail");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(55f, 30f) }; // col 5, row 0 (TopOffset 20)

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.Equal("https://evil.test/", host.OpenedHyperlink);
        Assert.Equal(HyperlinkSource.Explicit, host.OpenedHyperlinkSource);
        Assert.Equal("https://paypal.com", host.OpenedHyperlinkText);
        Assert.False(tab.ActivePane.Selection.HasSelection);
    }

    [Fact]
    public void HandleMouseDown_CtrlClickOnDetectedUrl_ActivatesWithoutDisplayText()
    {
        using var host = new FakeTerminalMouseHost { Ctrl = true };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        Feed(tab.ActivePane, "go https://example.com/x now");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(75f, 30f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.Equal("https://example.com/x", host.OpenedHyperlink);
        Assert.Equal(HyperlinkSource.Detected, host.OpenedHyperlinkSource);
        Assert.Null(host.OpenedHyperlinkText);
    }

    [Fact]
    public void HandleMouseDown_CtrlClickOnOscEightLink_IgnoredWhenExplicitLinksDisabled()
    {
        using var host = new FakeTerminalMouseHost
        {
            Ctrl = true,
            HyperlinkScan = new HyperlinkScanOptions(IncludeExplicit: false),
        };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        Feed(tab.ActivePane, "\u001b]8;;https://evil.test/\u001b\\Click me\u001b]8;;\u001b\\");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(25f, 30f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(25f, 30f));

        Assert.Null(host.OpenedHyperlink);
        Assert.Equal(StandardCursor.IBeam, host.CurrentCursor);
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

        AllocationAssert.NoAllocations(Click, measuredIterationsPerWindow: 4);

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
    public void HandleMouseDown_ActionThatOpensNewMenu_KeepsNewMenuActive()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        var prompt = new ContextMenuModel(10f, 10f, new[]
        {
            new ContextMenuItem("deny", "Deny", action: () => { })
        });
        var menu = new ContextMenuModel(10f, 10f, new[]
        {
            new ContextMenuItem("open", "Open prompt", action: () => host.ActiveContextMenu = prompt)
        });
        host.ActiveContextMenu = menu;

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(20f, 20f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.Same(prompt, host.ActiveContextMenu);
        Assert.True(prompt.IsVisible);
    }

    [Fact]
    public void HandleMouseDown_ClickOutside_FiresDismissedAndClearsMenu()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);

        int dismissed = 0;
        var menu = new ContextMenuModel(10f, 10f, new[]
        {
            new ContextMenuItem("action", "Action", action: () => { })
        })
        {
            Dismissed = () => dismissed++
        };
        host.ActiveContextMenu = menu;

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(700f, 500f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);

        Assert.Equal(1, dismissed);
        Assert.Null(host.ActiveContextMenu);
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

    [Theory]
    [InlineData(MiddleClickAction.PrimaryPaste, 1, false)]
    [InlineData(MiddleClickAction.ClipboardPaste, 0, true)]
    [InlineData(MiddleClickAction.Ignore, 0, false)]
    public void MiddleClick_FollowsConfiguredAction(MiddleClickAction action, int primaryCalls, bool clipboardPaste)
    {
        using var host = new FakeTerminalMouseHost { MiddleClick = action };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(50f, 60f) }, MouseButton.Middle);

        Assert.Equal(primaryCalls, host.PastePrimaryCalls);
        Assert.Equal(clipboardPaste, host.PasteCalled);
        if (primaryCalls > 0)
            Assert.Same(tab.ActivePane, host.PrimaryTarget);
    }

    [Fact]
    public void MiddleClick_InReportingPane_NeverPastes()
    {
        using var host = new FakeTerminalMouseHost { MiddleClick = MiddleClickAction.PrimaryPaste };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        EnableMouseMode(tab.ActivePane, 1000);

        var controller = new TerminalMouseController(host);
        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(50f, 60f) }, MouseButton.Middle);

        Assert.Equal(0, host.PastePrimaryCalls);
        Assert.False(host.PasteCalled);
    }

    [Fact]
    public void DragSelection_ReleaseReportsFinishedSelectionOnce()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(100f, 80f));
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal(1, host.SelectionFinishedCalls);
        Assert.Same(tab.ActivePane, host.SelectionFinishedPane);
        Assert.True(tab.ActivePane.Selection.HasSelection);
    }

    [Fact]
    public void PlainClick_DoesNotReportFinishedSelection()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal(0, host.SelectionFinishedCalls);
    }

    [Fact]
    public void DoubleClick_ReportsFinishedSelectionOnSecondRelease()
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
        Assert.Equal(0, host.SelectionFinishedCalls);

        now += 100;
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        Assert.Equal(1, host.SelectionFinishedCalls);
    }

    [Fact]
    public void ScrollbarDrag_ReleaseDoesNotReportFinishedSelection()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        for (int i = 0; i < 50; i++)
            tab.ActivePane.Session.Adapter.Buffer.ScrollUpLines(1);
        tab.PaneTree.Layout(800, 480, 10, 20);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(795f, 100f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(795f, 200f));
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal(0, host.SelectionFinishedCalls);
    }

    [Fact]
    public void ReportingPane_LeftDragNeverReportsFinishedSelection()
    {
        using var host = new FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        EnableMouseMode(tab.ActivePane, 1002);

        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(100f, 80f));
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal(0, host.SelectionFinishedCalls);
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

    // Sessions are started on a recorded PTY so the reported bytes can be read back.
    private static FakeTerminalMouseHost NewStartedHost(out TerminalTab tab, out SilentPty pty)
    {
        SilentPty? created = null;
        var host = new FakeTerminalMouseHost(() => created = new SilentPty());
        tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        tab.ActivePane.Session.Start();
        pty = Assert.IsType<SilentPty>(created);
        return host;
    }

    // PTY writes are queued in order, so a trailing marker proves everything before it has landed.
    private static string WrittenText(LeafPane pane, SilentPty pty)
    {
        const string marker = "<END>";
        pane.Session.WriteInput(Encoding.ASCII.GetBytes(marker));
        string text = string.Empty;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            text = Encoding.Latin1.GetString(pty.WrittenBytes());
            return text.EndsWith(marker, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5)));
        return text[..^marker.Length];
    }

    [Fact]
    public void ButtonEventDrag_ReportsMotionOnlyWhenTheCellChanges()
    {
        using var host = NewStartedHost(out var tab, out var pty);
        Feed(tab.ActivePane, "\u001b[?1002h\u001b[?1006h");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseMove(mouse, new Vector2(52f, 61f));
        controller.HandleMouseMove(mouse, new Vector2(58f, 79f));
        controller.HandleMouseMove(mouse, new Vector2(75f, 60f));
        controller.HandleMouseMove(mouse, new Vector2(76f, 62f));
        mouse.Position = new Vector2(76f, 62f);
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal("\u001b[<0;6;3M\u001b[<32;8;3M\u001b[<0;8;3m", WrittenText(tab.ActivePane, pty));
    }

    [Fact]
    public void AnyEventHover_SgrPixels_ReportsPixelChangesOnly()
    {
        using var host = NewStartedHost(out var tab, out var pty);
        Feed(tab.ActivePane, "\u001b[?1003h\u001b[?1016h");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse();

        // Content origin is below the 20 px tab bar: (50, 60) is pane pixel (50, 40), reported 1-based.
        controller.HandleMouseMove(mouse, new Vector2(50f, 60f));
        controller.HandleMouseMove(mouse, new Vector2(50f, 60f));
        controller.HandleMouseMove(mouse, new Vector2(51f, 60f));

        Assert.Equal("\u001b[<35;51;41M\u001b[<35;52;41M", WrittenText(tab.ActivePane, pty));
    }

    [Fact]
    public void X10Mode_ReportsPressWithoutModifiersAndNoRelease()
    {
        using var host = NewStartedHost(out var tab, out var pty);
        host.Ctrl = true;
        Feed(tab.ActivePane, "\u001b[?9h");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        controller.HandleMouseScroll(mouse, new ScrollWheel(0f, 1f));

        Assert.Equal("\u001b[M &#", WrittenText(tab.ActivePane, pty));
        Assert.False(tab.ActivePane.Selection.HasSelection);
    }

    [Theory]
    [InlineData(MouseReportBypassModifier.Shift, true, false, true, "")]
    [InlineData(MouseReportBypassModifier.Ctrl, false, true, true, "")]
    [InlineData(MouseReportBypassModifier.Ctrl, true, false, false, "\u001b[<4;6;3M")]
    [InlineData(MouseReportBypassModifier.None, true, false, false, "\u001b[<4;6;3M")]
    public void ReportBypassModifier_KeepsTheClickLocal(MouseReportBypassModifier bypass, bool shift, bool ctrl,
        bool expectLocalSelection, string expected)
    {
        using var host = NewStartedHost(out var tab, out var pty);
        host.MouseInput = MouseInputSettings.Default with { ReportBypassModifier = bypass };
        host.Shift = shift;
        host.Ctrl = ctrl;
        Feed(tab.ActivePane, "\u001b[?1000h\u001b[?1006h");
        var controller = new TerminalMouseController(host);

        controller.HandleMouseDown(new FakeMouse { Position = new Vector2(50f, 60f) }, MouseButton.Left);

        Assert.Equal(expectLocalSelection, tab.ActivePane.Selection.HasSelection);
        Assert.Equal(expected, WrittenText(tab.ActivePane, pty));
    }

    // One report per notch on both axes (66/67 horizontal, positive X = left); fractions carry.
    [Theory]
    [InlineData(0f, 1f, 1, "\u001b[<64;6;3M")]
    [InlineData(0f, -2f, 1, "\u001b[<65;6;3M\u001b[<65;6;3M")]
    [InlineData(1f, 0f, 1, "\u001b[<66;6;3M")]
    [InlineData(-1f, 0f, 1, "\u001b[<67;6;3M")]
    [InlineData(1f, 1f, 1, "\u001b[<64;6;3M\u001b[<66;6;3M")]
    [InlineData(0f, 0.5f, 2, "\u001b[<64;6;3M")]
    [InlineData(0f, 0.25f, 3, "")]
    public void ReportingWheel_ReportsPerNotchWithHorizontalButtons(float x, float y, int repeat, string expected)
    {
        using var host = NewStartedHost(out var tab, out var pty);
        Feed(tab.ActivePane, "\u001b[?1000h\u001b[?1006h");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        for (int i = 0; i < repeat; i++)
            controller.HandleMouseScroll(mouse, new ScrollWheel(x, y));

        Assert.Equal(expected, WrittenText(tab.ActivePane, pty));
        Assert.Equal(0, tab.ActivePane.ScrollOffset);
    }

    [Theory]
    [InlineData("\u001b[?1049h", true, 1f, "\u001b[A\u001b[A\u001b[A")]
    [InlineData("\u001b[?1049h", true, -1f, "\u001b[B\u001b[B\u001b[B")]
    [InlineData("\u001b[?1049h\u001b[?1h", true, 1f, "\u001bOA\u001bOA\u001bOA")]
    [InlineData("\u001b[?1047h", true, 0.5f, "\u001b[A")]
    [InlineData("\u001b[?1049h\u001b[?1007l", true, 1f, "")]
    [InlineData("\u001b[?1049h", false, 1f, "")]
    [InlineData("\u001b[?1049h\u001b[?1000h\u001b[?1006h", true, 1f, "\u001b[<64;6;3M")]
    [InlineData("", true, 1f, "")]
    public void AlternateScroll_WheelBecomesCursorKeysOnlyOnTheAlternateScreenWithoutReporting(
        string setup, bool alternateScrollSetting, float deltaY, string expected)
    {
        using var host = NewStartedHost(out var tab, out var pty);
        host.MouseInput = MouseInputSettings.Default with { AlternateScroll = alternateScrollSetting };
        Feed(tab.ActivePane, setup);
        var controller = new TerminalMouseController(host);

        controller.HandleMouseScroll(new FakeMouse { Position = new Vector2(50f, 60f) }, new ScrollWheel(0f, deltaY));

        Assert.Equal(expected, WrittenText(tab.ActivePane, pty));
    }

    [Theory]
    [InlineData(3.0, 1f, 1, 3)]
    [InlineData(3.0, 1f, 3, 9)]
    [InlineData(3.0, 0.1f, 30, 9)]
    [InlineData(3.0, 0.1f, 1, 0)]
    [InlineData(1.5, 1f, 2, 3)]
    [InlineData(1.0, 0.4f, 5, 2)]
    public void LocalWheel_AccumulatesFractionalDeltasWithMultiplier(double multiplier, float deltaY, int events, int expectedOffset)
    {
        using var host = new FakeTerminalMouseHost
        {
            MouseInput = MouseInputSettings.Default with { ScrollMultiplier = multiplier }
        };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        for (int i = 0; i < 100; i++)
            Feed(tab.ActivePane, $"line-{i}\r\n");
        var controller = new TerminalMouseController(host);
        var mouse = new FakeMouse { Position = new Vector2(50f, 60f) };

        for (int i = 0; i < events; i++)
            controller.HandleMouseScroll(mouse, new ScrollWheel(0f, deltaY));

        Assert.Equal(expectedOffset, tab.ActivePane.ScrollOffset);
    }

    [Fact]
    public void LocalWheel_RemainderDoesNotCarryAcrossPanes()
    {
        using var host = new FakeTerminalMouseHost
        {
            MouseInput = MouseInputSettings.Default with { ScrollMultiplier = 1.0 }
        };
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        var rightPane = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);
        tab.PaneTree.Layout(800, 480, 10, 20);
        foreach (var pane in tab.PaneTree.Leaves)
        {
            for (int i = 0; i < 50; i++)
                Feed(pane, $"line-{i}\r\n");
        }
        var leftPane = tab.PaneTree.Leaves[0];
        var controller = new TerminalMouseController(host);
        var left = new FakeMouse { Position = new Vector2(50f, 60f) };
        var right = new FakeMouse { Position = new Vector2(500f, 60f) };

        controller.HandleMouseScroll(left, new ScrollWheel(0f, 0.5f));
        controller.HandleMouseScroll(right, new ScrollWheel(0f, 0.5f));
        controller.HandleMouseScroll(left, new ScrollWheel(0f, 0.5f));
        Assert.Equal(0, leftPane.ScrollOffset);
        Assert.Equal(0, rightPane.ScrollOffset);

        controller.HandleMouseScroll(left, new ScrollWheel(0f, 0.5f));
        Assert.Equal(1, leftPane.ScrollOffset);
    }

}
