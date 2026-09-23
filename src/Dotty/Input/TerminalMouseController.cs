using Dotty.Abstractions.Config;
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
    bool ShowTabBar,
    float StatusReservedWidth = 0f);

public interface ITerminalMouseHost
{
    TerminalTabManager TabManager { get; }
    TerminalTab? ActiveTab { get; }
    ContextMenuModel? ActiveContextMenu { get; set; }
    TerminalMouseGeometry Geometry { get; }
    bool Ctrl { get; }
    bool Shift { get; }
    bool Alt { get; }
    bool Super { get; }
    void CopySelection();
    void PasteClipboard();
    void PasteClipboard(LeafPane targetPane);
    void CreateTab(TerminalTab activeTab);
    void ClearTerminal(TerminalTab activeTab);
    void OpenHyperlink(string url);
    bool TryExecuteAction(TerminalAction action);
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
    private LeafPane? _lastClickPane;
    private int _pressedButtons;
    private int _reportingButtons;
    private LeafPane? _reportingPane;
    private LeafPane? _selectionPane;
    private LeafPane? _scrollbarPane;
    private Vector2 _lastContentPointer;

    private readonly record struct PaneHit(
        LeafPane? Pane,
        float RelativeX,
        float RelativeY,
        int ViewRow,
        int ViewColumn,
        int LogicalRow,
        bool Inside)
    {
        public bool IsValid => Pane != null;
    }

    public bool LeftMouseDown => (_pressedButtons & ButtonMask(MouseButton.Left)) != 0;
    public bool IsDraggingScrollbar { get; private set; }
    public bool IsScrollbarHovered { get; private set; }
    public int HoveredTabIndex { get; private set; } = -1;
    public TabBarHitType HoveredTabHitType { get; private set; } = TabBarHitType.None;

    public TerminalMouseController(ITerminalMouseHost host, Func<long>? clockMilliseconds = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _clockMilliseconds = clockMilliseconds ?? GetDefaultClockMilliseconds;
    }

    private static long GetDefaultClockMilliseconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private static int ButtonMask(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Middle => 2,
        MouseButton.Right => 4,
        _ => 0
    };

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
        if (leafBoundsHeight <= 0f) return 0f;
        return Math.Clamp(leafRelY / leafBoundsHeight, 0f, 1f);
    }

    public static int CalculateTargetOffset(float progress, int scrollbackCount)
    {
        if (scrollbackCount <= 0) return 0;
        return (int)Math.Round((1.0f - Math.Clamp(progress, 0f, 1f)) * scrollbackCount);
    }

    private (float X, float Y) ContentPosition(Vector2 position)
    {
        var geom = _host.Geometry;
        return (
            position.X * geom.Scale - geom.PaddingLeft,
            position.Y * geom.Scale - geom.TopOffset - geom.PaddingTop);
    }

    private PaneHit HitTest(TerminalTab tab, Vector2 position, LeafPane? forcedPane = null)
    {
        var content = ContentPosition(position);
        return HitTestContent(tab, content.X, content.Y, forcedPane);
    }

    private PaneHit HitTestContent(TerminalTab? tab, float x, float y, LeafPane? forcedPane = null)
    {
        var pane = forcedPane ?? tab?.PaneTree.FindPaneAt(x, y);
        if (pane == null)
            return new PaneHit(null, x, y, 0, 0, 0, false);

        float relX = x - pane.Bounds.X;
        float relY = y - pane.Bounds.Y;
        var geom = _host.Geometry;
        float cellW = geom.CellWidth * geom.Scale;
        float cellH = geom.CellHeight * geom.Scale;
        int col = cellW > 0f
            ? Math.Clamp((int)MathF.Floor(relX / cellW), 0, Math.Max(0, pane.Columns - 1))
            : 0;
        int row = cellH > 0f
            ? Math.Clamp((int)MathF.Floor(relY / cellH), 0, Math.Max(0, pane.Rows - 1))
            : 0;
        bool inside = pane.Bounds.Contains(x, y);
        return new PaneHit(pane, relX, relY, row, col, row - pane.ScrollOffset, inside);
    }

    private PaneHit HitTestAtContent(TerminalTab? tab, float x, float y, LeafPane? forcedPane = null) =>
        HitTestContent(tab, x, y, forcedPane);


    private void SendMouse(LeafPane pane, int button, int row, int col, bool press, bool move)
    {
        var adapter = pane.Session.Adapter;
        Span<byte> bytes = stackalloc byte[32];
        int length = _inputEncoder.EncodeMouseEvent(
            adapter.CurrentMouseMode,
            adapter.CurrentMouseEncoding,
            button,
            Math.Clamp(row, 0, Math.Max(0, pane.Rows - 1)),
            Math.Clamp(col, 0, Math.Max(0, pane.Columns - 1)),
            press,
            move,
            GetCurrentKeyModifiers(),
            bytes);
        if (length != 0)
            pane.Session.WriteInput(bytes[..length]);
    }

    private static int ReportingButton(int pressedButtons)
    {
        if ((pressedButtons & 1) != 0) return 0;
        if ((pressedButtons & 2) != 0) return 1;
        if ((pressedButtons & 4) != 0) return 2;
        return 3;
    }

    private bool Reports(LeafPane pane) =>
        !_host.Shift && pane.Session.Adapter.MouseReportingEnabled;

    public void HandleMouseDown(IMouse mouse, MouseButton button)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;
        var geom = _host.Geometry;

        var pos = mouse.Position;
        float physX = pos.X * geom.Scale;
        float physY = pos.Y * geom.Scale;
        _lastContentPointer = ContentVector(pos);
        int mask = ButtonMask(button);
        if (_reportingPane != null && _reportingButtons != 0 && mask != 0)
        {
            _pressedButtons |= mask;
            _reportingButtons |= mask;
            var reportHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
            SendMouse(_reportingPane, ButtonNumber(button), reportHit.ViewRow, reportHit.ViewColumn,
                press: true, move: false);
            return;
        }
        var activeContextMenu = _host.ActiveContextMenu;
        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            var menuLayout = ContextMenuLayout.Calculate(activeContextMenu, geom.FramebufferWidth, geom.FramebufferHeight,
                geom.CellWidth * geom.Scale, geom.CellHeight * geom.Scale);
            int hitItemIndex = ContextMenuHitTester.HitTest(menuLayout, physX, physY);
            activeContextMenu.HoveredIndex = hitItemIndex;
            if (hitItemIndex >= 0) activeContextMenu.ExecuteHovered();
            else activeContextMenu.Close();
            _host.ActiveContextMenu = null;
            return;
        }

        if (geom.ShowTabBar && physY < geom.TopOffset)
        {
            var tabHit = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth,
                _host.TabManager.Count, _host.TabManager.ActiveIndex, geom.TopOffset, geom.StatusReservedWidth);
            if (button == MouseButton.Right && tabHit is TabBarHitResult.SelectTab select)
            {
                OpenTabContextMenu(select.Index, pos, geom);
            }
            else if (tabHit is TabBarHitResult.SelectTab selectTab) _host.TabManager.SelectTab(selectTab.Index);
            else if (tabHit is TabBarHitResult.CloseTab close && close.Index >= 0 && close.Index < _host.TabManager.Tabs.Count)
                _host.TabManager.CloseTab(_host.TabManager.Tabs[close.Index]);
            else if (tabHit is TabBarHitResult.NewTab) _host.TryExecuteAction(TerminalAction.NewTab);
            return;
        }

        var hit = HitTest(activeTab, pos);
        var pane = hit.Pane;
        bool insidePane = pane != null && hit.Inside;

        if (insidePane)
        {
            activeTab.PaneTree.ActivePane = pane!;
            if (Reports(pane!) && mask != 0)
            {
                _reportingPane = pane!;
                _pressedButtons |= mask;
                _reportingButtons |= mask;
                SendMouse(pane!, ButtonNumber(button), hit.ViewRow, hit.ViewColumn,
                    press: true, move: false);
                return;
            }
        }

        // The scrollbar is the owner of a click in its strip, including Ctrl+clicks.
        if (button == MouseButton.Left && insidePane &&
            pane!.Session.Adapter.Buffer.ScrollbackCount > 0 &&
            IsInScrollbarHitArea(hit.RelativeX, pane.Bounds.Width, geom.CellWidth * geom.Scale, geom.Scale))
        {
            _pressedButtons |= mask;
            ResetClickSequence();
            _selectionPane = pane;
            _scrollbarPane = pane;
            IsDraggingScrollbar = true;
            IsScrollbarHovered = true;
            pane.Selection.ClearSelection();
            int scrollback = pane.Session.Adapter.Buffer.ScrollbackCount;
            pane.ScrollTo(CalculateTargetOffset(
                CalculateScrollProgress(hit.RelativeY, pane.Bounds.Height, pane.Rows), scrollback), scrollback);
            _host.SetPointerCursor(StandardCursor.Hand);
            return;
        }

        if (_host.Ctrl && button == MouseButton.Left && insidePane)
        {
            // Never use the hover cache for activation: the buffer and scroll offset may have changed.
            var clickedLink = FindLink(pane!, hit.ViewRow, hit.ViewColumn);
            if (clickedLink.HasValue)
            {
                ResetClickSequence();
                _host.OpenHyperlink(clickedLink.Value.Url);
                return;
            }
        }

        _pressedButtons |= mask;
        if (button == MouseButton.Middle && insidePane)
        {
            _host.PasteClipboard(pane!);
            return;
        }

        if (button == MouseButton.Right)
        {
            OpenTerminalContextMenu(activeTab, pane, pos, geom);
            return;
        }

        if (button != MouseButton.Left || !insidePane)
        {
            if (button == MouseButton.Left)
                ResetClickSequence();
            return;
        }
        var localPane = pane!;
        _selectionPane = localPane;
        _scrollbarPane = null;
        IsDraggingScrollbar = false;
        IsScrollbarHovered = false;

        long now = _clockMilliseconds();
        float dist = Vector2.Distance(pos, _lastClickPosition);
        _clickCount = ReferenceEquals(_lastClickPane, localPane) &&
            now - _lastClickTimestampMs < 450 && dist < 12f
            ? Math.Min(3, _clickCount + 1)
            : 1;
        _lastClickTimestampMs = now;
        _lastClickPosition = pos;
        _lastClickPane = localPane;

        if (_clickCount >= 3)
            localPane.Selection.SelectLine(hit.LogicalRow, localPane.Columns);
        else if (_clickCount == 2)
            localPane.Selection.SelectWord(localPane.Session.Adapter.Buffer, hit.LogicalRow, hit.ViewColumn);
        else
            localPane.Selection.StartSelection(hit.LogicalRow, hit.ViewColumn, SelectionMode.Character);
        _host.SetPointerCursor(StandardCursor.IBeam);
    }
    private void OpenTabContextMenu(int tabIndex, Vector2 position, TerminalMouseGeometry geometry)
    {
        _host.ActiveContextMenu = new ContextMenuModel(position.X * geometry.Scale, position.Y * geometry.Scale,
            DefaultContextMenus.BuildTabMenu(
                onSplitRight: () => _host.TryExecuteAction(TerminalAction.SplitVertical),
                onSplitDown: () => _host.TryExecuteAction(TerminalAction.SplitHorizontal),
                onNewTab: () => _host.TryExecuteAction(TerminalAction.NewTab),
                onClose: () => _host.TabManager.CloseTab(_host.TabManager.Tabs[tabIndex])));
    }

    private void OpenTerminalContextMenu(
        TerminalTab activeTab,
        LeafPane? pane,
        Vector2 position,
        TerminalMouseGeometry geometry)
    {
        LeafPane contextPane = pane ?? activeTab.ActivePane;
        _host.ActiveContextMenu = new ContextMenuModel(position.X * geometry.Scale, position.Y * geometry.Scale,
            DefaultContextMenus.BuildTerminalMenu(
                hasSelection: contextPane.Selection.HasSelection,
                onCopy: () => _host.TryExecuteAction(TerminalAction.Copy),
                onPaste: () => _host.TryExecuteAction(TerminalAction.Paste),
                onSelectAll: () =>
                {
                    if (contextPane.Rows > 0 && contextPane.Columns > 0)
                    {
                        contextPane.Selection.StartSelection(-contextPane.ScrollOffset, 0, SelectionMode.Character);
                        contextPane.Selection.UpdateSelection(contextPane.Rows - 1 - contextPane.ScrollOffset, contextPane.Columns - 1);
                    }
                },
                onSplitRight: () =>
                {
                    if (ReferenceEquals(contextPane, activeTab.ActivePane))
                        _host.TryExecuteAction(TerminalAction.SplitVertical);
                    else
                        activeTab.PaneTree.Split(contextPane, SplitDirection.Vertical);
                },
                onSplitDown: () =>
                {
                    if (ReferenceEquals(contextPane, activeTab.ActivePane))
                        _host.TryExecuteAction(TerminalAction.SplitHorizontal);
                    else
                        activeTab.PaneTree.Split(contextPane, SplitDirection.Horizontal);
                },
                onClear: () => _host.TryExecuteAction(TerminalAction.Clear)));
    }

    private Vector2 ContentVector(Vector2 position)
    {
        var (x, y) = ContentPosition(position);
        return new Vector2(x, y);
    }

    private static int ButtonNumber(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Middle => 1,
        MouseButton.Right => 2,
        _ => 3
    };
    private void ResetClickSequence()
    {
        _clickCount = 0;
        _lastClickTimestampMs = 0;
        _lastClickPosition = default;
        _lastClickPane = null;
    }
    private HyperlinkSpan? FindLink(LeafPane pane, int viewRow, int viewColumn)
    {
        using var snapshot = pane.Session.Adapter.Buffer.CaptureRenderSnapshotVisible(
            scrollOffset: pane.ScrollOffset, sbStart: 0, sbEnd: -1);
        return HyperlinkScanner.FindLinkAt(snapshot, viewRow, viewColumn);
    }

    public void HandleMouseMove(IMouse mouse, Vector2 position)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        _lastContentPointer = ContentVector(position);
        HoveredTabIndex = -1;
        HoveredTabHitType = TabBarHitType.None;
        var geom = _host.Geometry;
        float physX = position.X * geom.Scale;
        float physY = position.Y * geom.Scale;
        if (_reportingPane != null && _reportingButtons != 0)
        {
            if (!_host.Shift)
            {
                var reportHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
                SendMouse(_reportingPane, ReportingButton(_reportingButtons), reportHit.ViewRow, reportHit.ViewColumn,
                    press: true, move: true);
            }
            return;
        }

        var activeContextMenu = _host.ActiveContextMenu;
        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            var menuLayout = ContextMenuLayout.Calculate(activeContextMenu, geom.FramebufferWidth, geom.FramebufferHeight,
                geom.CellWidth * geom.Scale, geom.CellHeight * geom.Scale);
            bool actionable = ContextMenuHitTester.TryHitInteractiveItem(menuLayout, physX, physY, out int item) &&
                item >= 0 && item < activeContextMenu.Items.Count && activeContextMenu.Items[item].Action != null;
            activeContextMenu.HoveredIndex = ContextMenuHitTester.HitTest(menuLayout, physX, physY);
            _host.SetPointerCursor(actionable ? StandardCursor.Hand : StandardCursor.Default);
            return;
        }

        if (geom.ShowTabBar && physY < geom.TopOffset)
        {
            var tabHitType = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth,
                _host.TabManager.Count, _host.TabManager.ActiveIndex, out int tabIndex, geom.TopOffset, geom.StatusReservedWidth);
            HoveredTabIndex = tabIndex;
            HoveredTabHitType = tabHitType;
            _host.SetPointerCursor(tabHitType is TabBarHitType.SelectTab or TabBarHitType.CloseTab or TabBarHitType.NewTab
                ? StandardCursor.Hand : StandardCursor.Default);
            return;
        }

        var hit = HitTest(activeTab, position);
        IsScrollbarHovered = hit.Pane != null && hit.Inside &&
            hit.Pane.Session.Adapter.Buffer.ScrollbackCount > 0 &&
            IsInScrollbarHitArea(hit.RelativeX, hit.Pane.Bounds.Width, geom.CellWidth * geom.Scale, geom.Scale);

        if (IsDraggingScrollbar && _scrollbarPane != null && LeftMouseDown)
        {
            var scrollHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _scrollbarPane);
            int scrollback = _scrollbarPane.Session.Adapter.Buffer.ScrollbackCount;
            _scrollbarPane.ScrollTo(CalculateTargetOffset(
                CalculateScrollProgress(scrollHit.RelativeY, _scrollbarPane.Bounds.Height, _scrollbarPane.Rows), scrollback), scrollback);
            IsScrollbarHovered = true;
            _host.SetPointerCursor(StandardCursor.Hand);
            return;
        }

        bool localSelectionCapture = _selectionPane != null && LeftMouseDown;
        if (localSelectionCapture && !IsDraggingScrollbar)
        {
            var selectionPane = _selectionPane!;
            var selectionHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, selectionPane);
            var selection = selectionPane.Selection;
            if (selection.Mode == SelectionMode.Line)
                selection.UpdateLineSelection(selectionHit.LogicalRow, selectionPane.Columns);
            else
                selection.UpdateSelection(selectionHit.LogicalRow, selectionHit.ViewColumn);

            // Keep a local drag local even when the pointer crosses a split boundary.
            _host.SetPointerCursor(StandardCursor.IBeam);
            return;
        }

        if (!_host.Shift && hit.Pane != null && hit.Inside &&
            hit.Pane.Session.Adapter.CurrentMouseMode == TerminalAdapter.MouseMode.AnyEvent)
        {
            SendMouse(hit.Pane, 3, hit.ViewRow, hit.ViewColumn, press: true, move: true);
            return;
        }

        if (_host.Ctrl && hit.Pane != null && hit.Inside)
        {
            bool hoveredLink = HyperlinkScanner.ContainsLinkAt(
                hit.Pane.Session.Adapter.Buffer, hit.ViewRow, hit.ViewColumn);
            _host.SetPointerCursor(hoveredLink ? StandardCursor.Hand : StandardCursor.IBeam);
        }
        else
        {
            _host.SetPointerCursor(IsScrollbarHovered ? StandardCursor.Hand : StandardCursor.IBeam);
        }
    }

    public void HandleMouseUp(IMouse mouse, MouseButton button)
    {
        int mask = ButtonMask(button);
        if (mask == 0) return;

        _lastContentPointer = ContentVector(mouse.Position);
        if (_reportingPane != null && (_reportingButtons & mask) != 0)
        {
            var hit = HitTestAtContent(_host.ActiveTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
            SendMouse(_reportingPane, ButtonNumber(button), hit.ViewRow, hit.ViewColumn, press: false, move: false);
            _reportingButtons &= ~mask;
            if (_reportingButtons == 0)
                _reportingPane = null;
        }

        _pressedButtons &= ~mask;
        if (button == MouseButton.Left)
        {
            IsDraggingScrollbar = false;
            _scrollbarPane = null;
            _selectionPane = null;
        }
    }

    public void HandleMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (wheel.Y == 0f) return;

        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;
        if (_reportingPane != null && _reportingButtons != 0)
        {
            if (!_host.Shift)
            {
                var reportHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
                SendMouse(_reportingPane, wheel.Y > 0f ? 64 : 65, reportHit.ViewRow, reportHit.ViewColumn,
                    press: true, move: false);
            }
            return;
        }
        if (_selectionPane != null && LeftMouseDown)
            return;
        var hit = HitTest(activeTab, mouse.Position);
        if (hit.Pane == null || !hit.Inside) return;
        var pane = hit.Pane;
        activeTab.PaneTree.ActivePane = pane;

        if (Reports(pane))
        {
            SendMouse(pane, wheel.Y > 0f ? 64 : 65, hit.ViewRow, hit.ViewColumn, press: true, move: false);
            return;
        }

        int lines = Math.Max(1, (int)Math.Ceiling(Math.Abs(wheel.Y) * 3));
        if (wheel.Y > 0f)
            pane.ScrollUp(lines, pane.Session.Adapter.Buffer.ScrollbackCount);
        else
            pane.ScrollDown(lines);
    }

    public bool TickSelectionAutoscroll()
    {
        var pane = _selectionPane;
        var activeTab = _host.ActiveTab;
        if (pane == null || activeTab == null || !LeftMouseDown || IsDraggingScrollbar)
            return false;

        float y = _lastContentPointer.Y - pane.Bounds.Y;
        float overshoot;
        bool scrollUp;
        if (y < 0f)
        {
            overshoot = -y;
            scrollUp = true;
        }
        else if (y > pane.Bounds.Height)
        {
            overshoot = y - pane.Bounds.Height;
            scrollUp = false;
        }
        else
        {
            return false;
        }

        float cellHeight = _host.Geometry.CellHeight * _host.Geometry.Scale;
        int lines = cellHeight > 0f
            ? Math.Clamp((int)MathF.Min(8f, MathF.Ceiling(overshoot / cellHeight)), 1, 8)
            : 1;
        int oldOffset = pane.ScrollOffset;
        if (scrollUp)
            pane.ScrollUp(lines, pane.Session.Adapter.Buffer.ScrollbackCount);
        else
            pane.ScrollDown(lines);

        var hit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, pane);
        bool changed = oldOffset != pane.ScrollOffset;
        var selection = pane.Selection;
        int oldRow = selection.ActiveRow;
        int oldColumn = selection.ActiveColumn;
        if (selection.Mode == SelectionMode.Line)
            selection.UpdateLineSelection(hit.LogicalRow, pane.Columns);
        else
            selection.UpdateSelection(hit.LogicalRow, hit.ViewColumn);
        return changed || oldRow != selection.ActiveRow || oldColumn != selection.ActiveColumn;
    }

    public void ResetState()
    {
        _pressedButtons = 0;
        _reportingButtons = 0;
        _reportingPane = null;
        _selectionPane = null;
        _scrollbarPane = null;
        IsDraggingScrollbar = false;
        IsScrollbarHovered = false;

        ResetClickSequence();
        _lastContentPointer = default;
        HoveredTabIndex = -1;
        HoveredTabHitType = TabBarHitType.None;
    }

}
