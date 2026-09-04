using System;
using System.Numerics;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Silk.NET.Input;

namespace Dotty.Silk.Input;

public readonly record struct TerminalMouseGeometry(
    float Scale,
    float CellWidth,
    float CellHeight,
    float PaddingLeft,
    float PaddingTop,
    float TopOffset,
    float FramebufferWidth,
    float FramebufferHeight,
    int Columns,
    int Rows,
    bool ShowTabBar);

public interface ITerminalMouseHost
{
    TerminalTabManager TabManager { get; }
    TerminalTab? ActiveTab { get; }
    TextSelectionService SelectionService { get; }
    ContextMenuModel? ActiveContextMenu { get; set; }
    TerminalMouseGeometry Geometry { get; }
    bool Ctrl { get; }
    bool Shift { get; }
    bool Alt { get; }
    bool Super { get; }
    void CopySelection();
    void PasteClipboard();
    void CreateTab(TerminalTab activeTab);
    void ClearTerminal(TerminalTab activeTab);
    void OpenHyperlink(string url);
    void SetPointerCursor(StandardCursor cursor);
}

public sealed class TerminalMouseController
{
    private readonly ITerminalMouseHost _host;
    private readonly Func<long> _clockMilliseconds;
    private readonly TerminalInputEncoder _inputEncoder = new();

    private long _lastClickTimestampMs;
    private Vector2 _lastClickPosition;
    private int _clickCount;
    private HyperlinkSpan? _hoveredLink;

    public bool LeftMouseDown { get; private set; }
    public bool IsDraggingScrollbar { get; private set; }
    public bool IsScrollbarHovered { get; private set; }
    public int HoveredTabIndex { get; private set; } = -1;
    public TabBarHitType HoveredTabHitType { get; private set; } = TabBarHitType.None;

    public TerminalMouseController(ITerminalMouseHost host, Func<long>? clockMilliseconds = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _clockMilliseconds = clockMilliseconds ?? GetDefaultClockMilliseconds;
    }

    private static long GetDefaultClockMilliseconds()
    {
        return System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }

    private TerminalKeyModifiers GetCurrentKeyModifiers()
    {
        var modifiers = TerminalKeyModifiers.None;
        if (_host.Shift) modifiers |= TerminalKeyModifiers.Shift;
        if (_host.Alt) modifiers |= TerminalKeyModifiers.Alt;
        if (_host.Ctrl) modifiers |= TerminalKeyModifiers.Control;
        if (_host.Super) modifiers |= TerminalKeyModifiers.Meta;
        return modifiers;
    }

    public static bool IsInScrollbarHitArea(float leafRelX, float leafBoundsWidth, float cellWidthScaled, float scale)
    {
        float stripWidth = Math.Max(cellWidthScaled, 14f * scale);
        float stripLeft = leafBoundsWidth - stripWidth;
        return leafRelX >= stripLeft && leafRelX <= leafBoundsWidth;
    }

    public static float CalculateScrollProgress(float leafRelY, float leafBoundsHeight, int leafRows)
    {
        if (leafBoundsHeight <= 0f)
        {
            return 0f;
        }

        return Math.Clamp(leafRelY / leafBoundsHeight, 0f, 1f);
    }

    public static int CalculateTargetOffset(float progress, int scrollbackCount)
    {
        if (scrollbackCount <= 0)
        {
            return 0;
        }

        return (int)Math.Round((1.0f - Math.Clamp(progress, 0f, 1f)) * scrollbackCount);
    }

    public void HandleMouseDown(IMouse mouse, MouseButton button)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        var geom = _host.Geometry;
        var pos = mouse.Position;
        float physX = pos.X * geom.Scale;
        float physY = pos.Y * geom.Scale;

        long now = _clockMilliseconds();
        float dist = Vector2.Distance(pos, _lastClickPosition);
        if (now - _lastClickTimestampMs < 450 && dist < 12f)
        {
            _clickCount++;
        }
        else
        {
            _clickCount = 1;
        }
        _lastClickTimestampMs = now;
        _lastClickPosition = pos;

        // 1. Check Context Menu click handling
        var activeContextMenu = _host.ActiveContextMenu;
        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            var menuLayout = ContextMenuLayout.Calculate(
                activeContextMenu,
                geom.FramebufferWidth,
                geom.FramebufferHeight,
                geom.CellWidth * geom.Scale,
                geom.CellHeight * geom.Scale);

            int hitItemIndex = ContextMenuHitTester.HitTest(menuLayout, physX, physY);
            if (hitItemIndex >= 0 && hitItemIndex < activeContextMenu.Items.Count)
            {
                var item = activeContextMenu.Items[hitItemIndex];
                if (!item.IsDisabled && !item.IsSeparator)
                {
                    item.Action?.Invoke();
                }
            }
            _host.ActiveContextMenu = null;
            return;
        }

        // 2. Right Click: Open Context Menu
        if (button == MouseButton.Right)
        {
            if (geom.ShowTabBar && _host.TabManager != null && physY < geom.TopOffset)
            {
                var hit = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth, _host.TabManager.Count, _host.TabManager.ActiveIndex, geom.TopOffset);
                if (hit is TabBarHitResult.SelectTab select)
                {
                    _host.ActiveContextMenu = new ContextMenuModel(pos.X * geom.Scale, pos.Y * geom.Scale, DefaultContextMenus.BuildTabMenu(
                        select.Index,
                        onSplitRight: () => activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Vertical),
                        onSplitDown: () => activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Horizontal),
                        onRename: () => { },
                        onClose: () => _host.TabManager.CloseTab(_host.TabManager.Tabs[select.Index])
                    ));
                    return;
                }
            }
            else
            {
                _host.ActiveContextMenu = new ContextMenuModel(pos.X * geom.Scale, pos.Y * geom.Scale, DefaultContextMenus.BuildTerminalMenu(
                    hasSelection: _host.SelectionService.HasSelection,
                    onCopy: _host.CopySelection,
                    onPaste: _host.PasteClipboard,
                    onSelectAll: () => { },
                    onSplitRight: () => activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Vertical),
                    onSplitDown: () => activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Horizontal),
                    onClear: () => _host.ClearTerminal(activeTab)
                ));
                return;
            }
        }

        // 3. Tab Bar hit testing
        if (geom.ShowTabBar && _host.TabManager != null && physY < geom.TopOffset)
        {
            var hit = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth, _host.TabManager.Count, _host.TabManager.ActiveIndex, geom.TopOffset);
            if (hit is TabBarHitResult.SelectTab select)
            {
                _host.TabManager.SelectTab(select.Index);
            }
            else if (hit is TabBarHitResult.CloseTab close)
            {
                if (close.Index >= 0 && close.Index < _host.TabManager.Tabs.Count)
                {
                    _host.TabManager.CloseTab(_host.TabManager.Tabs[close.Index]);
                }
            }
            else if (hit is TabBarHitResult.NewTab)
            {
                _host.CreateTab(activeTab);
            }
            return;
        }

        // 4. Check Ctrl+Click for Hyperlink launching
        if (_host.Ctrl && button == MouseButton.Left && _hoveredLink.HasValue)
        {
            string linkUrl = _hoveredLink.Value.Url;
            _host.OpenHyperlink(linkUrl);
            return;
        }

        // 5. Terminal Pane / Selection / Scrollbar handling
        float localX = physX - geom.PaddingLeft;
        float localY = physY - geom.TopOffset - geom.PaddingTop;
        var clickedLeaf = activeTab.PaneTree.FindPaneAt(localX, localY) ?? activeTab.ActivePane;
        if (clickedLeaf != null)
        {
            activeTab.PaneTree.ActivePane = clickedLeaf;
            float leafRelX = Math.Max(0f, localX - clickedLeaf.Bounds.X);
            float leafRelY = Math.Max(0f, localY - clickedLeaf.Bounds.Y);
            float cellW = geom.CellWidth * geom.Scale;
            float cellH = geom.CellHeight * geom.Scale;
            int leafCol = Math.Clamp((int)(leafRelX / cellW), 0, Math.Max(0, clickedLeaf.Columns - 1));
            int leafRow = Math.Clamp((int)(leafRelY / cellH), 0, Math.Max(0, clickedLeaf.Rows - 1));

            if (button == MouseButton.Left)
            {
                bool isScrollbarHit = clickedLeaf.Session.Adapter.Buffer.ScrollbackCount > 0 &&
                    IsInScrollbarHitArea(leafRelX, clickedLeaf.Bounds.Width, cellW, geom.Scale);

                if (isScrollbarHit)
                {
                    IsDraggingScrollbar = true;
                    LeftMouseDown = true;
                    IsScrollbarHovered = true;
                    _host.SelectionService.ClearSelection();
                    int scrollback = clickedLeaf.Session.Adapter.Buffer.ScrollbackCount;
                    float progress = CalculateScrollProgress(leafRelY, clickedLeaf.Bounds.Height, clickedLeaf.Rows);
                    int targetOffset = CalculateTargetOffset(progress, scrollback);
                    activeTab.ScrollTo(targetOffset, scrollback);
                    _host.SetPointerCursor(StandardCursor.Hand);
                    return;
                }

                IsDraggingScrollbar = false;
                LeftMouseDown = true;
                if (_clickCount >= 2)
                {
                    _host.SelectionService.SelectLine(leafRow, clickedLeaf.Columns);
                }
                else
                {
                    _host.SelectionService.StartSelection(leafRow, leafCol, SelectionMode.Character);
                }
            }
        }
    }

    public void HandleMouseMove(IMouse mouse, Vector2 position)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        var geom = _host.Geometry;
        float physX = position.X * geom.Scale;
        float physY = position.Y * geom.Scale;
        HoveredTabIndex = -1;
        HoveredTabHitType = TabBarHitType.None;
        // 1. Check Context Menu hover
        var activeContextMenu = _host.ActiveContextMenu;
        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            var menuLayout = ContextMenuLayout.Calculate(
                activeContextMenu,
                geom.FramebufferWidth,
                geom.FramebufferHeight,
                geom.CellWidth * geom.Scale,
                geom.CellHeight * geom.Scale);

            int hitItemIndex = ContextMenuHitTester.HitTest(menuLayout, physX, physY);
            activeContextMenu.HoveredIndex = hitItemIndex;
            _host.SetPointerCursor(hitItemIndex >= 0 ? StandardCursor.Hand : StandardCursor.Default);
            return;
        }

        // 2. Check Tab Bar hover
        if (geom.ShowTabBar && _host.TabManager != null && physY < geom.TopOffset)
        {
            var hit = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth, _host.TabManager.Count, _host.TabManager.ActiveIndex, out int hitTabIndex, geom.TopOffset);
            HoveredTabIndex = hitTabIndex;
            HoveredTabHitType = hit;
            if (hit is TabBarHitType.SelectTab or TabBarHitType.CloseTab or TabBarHitType.NewTab)
            {
                _host.SetPointerCursor(StandardCursor.Hand);
            }
            else
            {
                _host.SetPointerCursor(StandardCursor.Default);
            }
            return;
        }

        float localX = physX - geom.PaddingLeft;
        float localY = physY - geom.TopOffset - geom.PaddingTop;
        float cellW = geom.CellWidth * geom.Scale;
        float cellH = geom.CellHeight * geom.Scale;
        int col = Math.Clamp((int)(localX / cellW), 0, Math.Max(0, geom.Columns - 1));
        int row = Math.Clamp((int)(localY / cellH), 0, Math.Max(0, geom.Rows - 1));

        if (activeTab.Session.Adapter.MouseReportingEnabled)
        {
            if (activeTab.Session.Adapter.CurrentMouseMode is TerminalAdapter.MouseMode.ButtonEvent or TerminalAdapter.MouseMode.AnyEvent)
            {
                var modifiers = GetCurrentKeyModifiers();
                int btn = LeftMouseDown ? 0 : 3;
                var bytes = _inputEncoder.EncodeMouseEvent(
                    activeTab.Session.Adapter.CurrentMouseMode,
                    activeTab.Session.Adapter.CurrentMouseEncoding,
                    btn, row, col, isPress: LeftMouseDown, isMove: true, modifiers);
                if (bytes != null) activeTab.Session.WriteInput(bytes);
            }
            return;
        }

        // Scrollbar continuous dragging
        if (IsDraggingScrollbar && LeftMouseDown && activeTab.ActivePane != null)
        {
            var curLeaf = activeTab.ActivePane;
            int scrollback = curLeaf.Session.Adapter.Buffer.ScrollbackCount;
            float leafRelY = Math.Max(0f, localY - curLeaf.Bounds.Y);
            float progress = CalculateScrollProgress(leafRelY, curLeaf.Bounds.Height, curLeaf.Rows);
            int targetOffset = CalculateTargetOffset(progress, scrollback);
            activeTab.ScrollTo(targetOffset, scrollback);
            IsScrollbarHovered = true;
            _host.SetPointerCursor(StandardCursor.Hand);
            return;
        }

        // Scrollbar hover detection
        if (activeTab.ActivePane != null)
        {
            var curLeaf = activeTab.ActivePane;
            float leafRelX = Math.Max(0f, localX - curLeaf.Bounds.X);
            IsScrollbarHovered = curLeaf.Session.Adapter.Buffer.ScrollbackCount > 0 &&
                IsInScrollbarHitArea(leafRelX, curLeaf.Bounds.Width, cellW, geom.Scale);
        }
        else
        {
            IsScrollbarHovered = false;
        }

        if (LeftMouseDown && activeTab.ActivePane != null)
        {
            var curLeaf = activeTab.ActivePane;
            float leafRelX = Math.Max(0f, localX - curLeaf.Bounds.X);
            float leafRelY = Math.Max(0f, localY - curLeaf.Bounds.Y);
            int leafCol = Math.Clamp((int)(leafRelX / cellW), 0, Math.Max(0, curLeaf.Columns - 1));
            int leafRow = Math.Clamp((int)(leafRelY / cellH), 0, Math.Max(0, curLeaf.Rows - 1));

            if (_host.SelectionService.Mode == SelectionMode.Line)
            {
                _host.SelectionService.UpdateLineSelection(leafRow, curLeaf.Columns);
            }
            else
            {
                _host.SelectionService.UpdateSelection(leafRow, leafCol);
            }
        }

        // Update Hyperlink hover state and cursor if Ctrl is held
        if (_host.Ctrl)
        {
            using var snap = activeTab.Session.Adapter.Buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: -1);
            _hoveredLink = HyperlinkScanner.FindLinkAt(snap, row, col);
            _host.SetPointerCursor(_hoveredLink.HasValue ? StandardCursor.Hand : StandardCursor.IBeam);
        }
        else if (IsScrollbarHovered || IsDraggingScrollbar)
        {
            _hoveredLink = null;
            _host.SetPointerCursor(StandardCursor.Hand);
        }
        else
        {
            _hoveredLink = null;
            _host.SetPointerCursor(StandardCursor.IBeam);
        }
    }

    public void HandleMouseUp(IMouse mouse, MouseButton button)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        var geom = _host.Geometry;
        var pos = mouse.Position;
        float physX = pos.X * geom.Scale;
        float physY = pos.Y * geom.Scale;

        float cellW = geom.CellWidth * geom.Scale;
        float cellH = geom.CellHeight * geom.Scale;
        int col = Math.Clamp((int)((physX - geom.PaddingLeft) / cellW), 0, Math.Max(0, geom.Columns - 1));
        int row = Math.Clamp((int)((physY - geom.TopOffset - geom.PaddingTop) / cellH), 0, Math.Max(0, geom.Rows - 1));

        if (activeTab.Session.Adapter.MouseReportingEnabled)
        {
            int btn = button switch
            {
                MouseButton.Left => 0,
                MouseButton.Middle => 1,
                MouseButton.Right => 2,
                _ => 3
            };
            var modifiers = GetCurrentKeyModifiers();
            var bytes = _inputEncoder.EncodeMouseEvent(
                activeTab.Session.Adapter.CurrentMouseMode,
                activeTab.Session.Adapter.CurrentMouseEncoding,
                btn, row, col, isPress: false, isMove: false, modifiers);
            if (bytes != null) activeTab.Session.WriteInput(bytes);
            LeftMouseDown = false;
            return;
        }

        IsDraggingScrollbar = false;
        if (button == MouseButton.Left)
        {
            LeftMouseDown = false;
        }
    }

    public void HandleMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        var geom = _host.Geometry;
        var pos = mouse.Position;
        float cellW = geom.CellWidth * geom.Scale;
        float cellH = geom.CellHeight * geom.Scale;
        int col = Math.Clamp((int)(pos.X / cellW), 0, Math.Max(0, geom.Columns - 1));
        int row = Math.Clamp((int)(pos.Y / cellH), 0, Math.Max(0, geom.Rows - 1));

        if (activeTab.Session.Adapter.MouseReportingEnabled)
        {
            int btn = wheel.Y > 0 ? 64 : 65;
            var modifiers = GetCurrentKeyModifiers();
            var bytes = _inputEncoder.EncodeMouseEvent(
                activeTab.Session.Adapter.CurrentMouseMode,
                activeTab.Session.Adapter.CurrentMouseEncoding,
                btn, row, col, isPress: true, isMove: false, modifiers);
            if (bytes != null) activeTab.Session.WriteInput(bytes);
            return;
        }

        var buffer = activeTab.Session.Adapter.Buffer;
        if (wheel.Y > 0)
        {
            activeTab.ScrollUp((int)Math.Ceiling(wheel.Y * 3), buffer.ScrollbackCount);
        }
        else if (wheel.Y < 0)
        {
            activeTab.ScrollDown((int)Math.Ceiling(-wheel.Y * 3));
        }
    }
}
