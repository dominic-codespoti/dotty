using Dotty.Abstractions.Config;
using System;
using System.Numerics;
using Dotty.Runtime.Config;
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
    float StatusReservedWidth = 0f,
    float CaptionButtonsWidth = 0f);

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

    /// <summary>What a middle click does outside mouse reporting (live configuration).</summary>
    MiddleClickAction MiddleClick { get; }

    /// <summary>Wheel multiplier, alternate scroll and reporting bypass modifier (live configuration).</summary>
    MouseInputSettings MouseInput { get; }

    /// <summary>Pastes the PRIMARY selection into <paramref name="targetPane"/> through the shared paste router.</summary>
    void PastePrimary(LeafPane targetPane);

    /// <summary>A user-made local selection was completed by releasing the left button (copy-on-select hook).</summary>
    void OnLocalSelectionFinished(LeafPane pane);
    void CreateTab(TerminalTab activeTab);
    void ClearTerminal(TerminalTab activeTab);
    /// <summary>Activates a clicked link. <paramref name="displayText"/> is the visible text of an explicit (OSC 8) link, null for detected links.</summary>
    void OpenHyperlink(string url, HyperlinkSource source, string? displayText);

    /// <summary>Scan options for hover and click (e.g. OSC 8 links disabled by configuration).</summary>
    HyperlinkScanOptions HyperlinkScan { get; }
    bool TryExecuteAction(TerminalAction action);
    void SetPointerCursor(StandardCursor cursor);

    /// <summary>
    /// A left click at device pixels on the search overlay (▲ ▼ × buttons, toggles, input box). True when the click
    /// landed on the overlay and was handled.
    /// </summary>
    bool TryClickSearchOverlay(float deviceX, float deviceY) => false;

    /// <summary>Menu label of the chord bound to <paramref name="action"/> in the live binding table; null when unbound.</summary>
    string? GetShortcutLabel(TerminalAction action) => null;
}

public sealed class TerminalMouseController
{
    private readonly ITerminalMouseHost _host;
    private readonly Func<long> _clockMilliseconds;
    private MouseMotionFilter _motionFilter;
    private LeafPane? _motionFilterPane;
    private ScrollAccumulator _scrollAccumulator;
    private LeafPane? _scrollAccumulatorPane;
    private bool _scrollAccumulatorReports;

    private long _lastClickTimestampMs;
    private Vector2 _lastClickPosition;
    private int _clickCount;
    private LeafPane? _lastClickPane;
    private int _pressedButtons;
    private int _reportingButtons;
    private LeafPane? _reportingPane;
    private LeafPane? _selectionPane;
    private bool _selectionMoved;
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


    public void SetCaptionButtonHover(TabBarHitType hitType)
    {
        HoveredTabIndex = -1;
        HoveredTabHitType = hitType is
            TabBarHitType.Minimize or TabBarHitType.Maximize or TabBarHitType.Close
            ? hitType
            : TabBarHitType.None;
    }
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


    private void SendMouse(LeafPane pane, int button, in PaneHit hit, MouseEventKind kind)
    {
        var adapter = pane.Session.Adapter;
        var report = new MouseReport(
            adapter.CurrentMouseMode,
            adapter.CurrentMouseEncoding,
            button,
            kind,
            Math.Clamp(hit.ViewColumn, 0, Math.Max(0, pane.Columns - 1)),
            Math.Clamp(hit.ViewRow, 0, Math.Max(0, pane.Rows - 1)),
            PixelOffset(hit.RelativeX, pane.Bounds.Width),
            PixelOffset(hit.RelativeY, pane.Bounds.Height),
            GetCurrentKeyModifiers());
        Span<byte> bytes = stackalloc byte[MouseEncoder.MaxSequenceLength];
        int length = MouseEncoder.Encode(report, bytes);
        if (length == 0)
            return;

        if (!ReferenceEquals(_motionFilterPane, pane))
        {
            _motionFilter.Reset();
            _motionFilterPane = pane;
        }
        if (_motionFilter.Admit(report))
            pane.Session.WriteInput(bytes[..length]);
    }

    // SGR-pixels position: framebuffer pixels from the pane origin, clamped into the pane (drags may leave it).
    private static int PixelOffset(float relative, float extent)
    {
        float last = MathF.Max(0f, MathF.Ceiling(extent) - 1f);
        return (int)Math.Clamp(MathF.Floor(relative), 0f, last);
    }

    private static int ReportingButton(int pressedButtons)
    {
        if ((pressedButtons & 1) != 0) return 0;
        if ((pressedButtons & 2) != 0) return 1;
        if ((pressedButtons & 4) != 0) return 2;
        return 3;
    }

    private bool ReportingBypassed() => _host.MouseInput.ReportBypassModifier switch
    {
        MouseReportBypassModifier.Shift => _host.Shift,
        MouseReportBypassModifier.Ctrl => _host.Ctrl,
        MouseReportBypassModifier.Alt => _host.Alt,
        MouseReportBypassModifier.Super => _host.Super,
        _ => false,
    };

    private bool Reports(LeafPane pane) =>
        !ReportingBypassed() && pane.Session.Adapter.MouseReportingEnabled;

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
            SendMouse(_reportingPane, ButtonNumber(button), reportHit, MouseEventKind.Press);
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
            if (ReferenceEquals(_host.ActiveContextMenu, activeContextMenu))
                _host.ActiveContextMenu = null;
            return;
        }

        if (button == MouseButton.Left && _host.TryClickSearchOverlay(physX, physY))
            return;

        if (geom.ShowTabBar && physY < geom.TopOffset)
        {
            var tabHit = TabBarHitTester.HitTest(physX, physY, geom.FramebufferWidth,
                _host.TabManager.Count, _host.TabManager.ActiveIndex, geom.TopOffset,
                geom.StatusReservedWidth, geom.CaptionButtonsWidth);
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
                SendMouse(pane!, ButtonNumber(button), hit, MouseEventKind.Press);
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
            if (TryFindLink(pane!, hit.ViewRow, hit.ViewColumn, out HyperlinkSpan clickedLink, out string? displayText))
            {
                ResetClickSequence();
                _host.OpenHyperlink(clickedLink.Url, clickedLink.Source, displayText);
                return;
            }
        }

        _pressedButtons |= mask;
        if (button == MouseButton.Middle && insidePane)
        {
            switch (_host.MiddleClick)
            {
                case MiddleClickAction.PrimaryPaste:
                    _host.PastePrimary(pane!);
                    break;
                case MiddleClickAction.ClipboardPaste:
                    _host.PasteClipboard(pane!);
                    break;
            }
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
        _selectionMoved = false;
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
                onClose: () => _host.TabManager.CloseTab(_host.TabManager.Tabs[tabIndex]),
                shortcutFor: _host.GetShortcutLabel));
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
                onSelectAll: () => contextPane.Selection.SelectAll(contextPane.Session.Adapter.Buffer),
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
                onClear: () => _host.TryExecuteAction(TerminalAction.Clear),
                hasCommandOutput: contextPane.Session.Adapter.Buffer.TryGetLatestCommandOutputRange(out _, out _, out _, out _),
                onCopyCommandOutput: () => _host.TryExecuteAction(TerminalAction.CopyCommandOutput),
                hasShellPrompts: contextPane.Session.Adapter.Buffer.GetPromptMarks().Count > 0,
                onPreviousPrompt: () => _host.TryExecuteAction(TerminalAction.PreviousPrompt),
                onNextPrompt: () => _host.TryExecuteAction(TerminalAction.NextPrompt),
                shortcutFor: _host.GetShortcutLabel));
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
    private bool TryFindLink(LeafPane pane, int viewRow, int viewColumn, out HyperlinkSpan link, out string? displayText)
    {
        using var snapshot = pane.Session.Adapter.Buffer.CaptureRenderSnapshotVisible(
            scrollOffset: pane.ScrollOffset, sbStart: 0, sbEnd: -1);
        HyperlinkSpan? found = HyperlinkScanner.FindLinkAt(snapshot, viewRow, viewColumn, _host.HyperlinkScan);
        if (!found.HasValue)
        {
            link = default;
            displayText = null;
            return false;
        }

        link = found.Value;
        // Read from the same snapshot that produced the span so the policy sees what the user clicked.
        displayText = link.Source == HyperlinkSource.Explicit
            ? HyperlinkScanner.GetSpanText(snapshot, link)
            : null;
        return true;
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
            if (!ReportingBypassed())
            {
                var reportHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
                SendMouse(_reportingPane, ReportingButton(_reportingButtons), reportHit, MouseEventKind.Motion);
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
                _host.TabManager.Count, _host.TabManager.ActiveIndex, out int tabIndex,
                geom.TopOffset, geom.StatusReservedWidth, geom.CaptionButtonsWidth);
            HoveredTabIndex = tabIndex;
            HoveredTabHitType = tabHitType;
            _host.SetPointerCursor(tabHitType is
                TabBarHitType.SelectTab or TabBarHitType.CloseTab or TabBarHitType.NewTab or
                TabBarHitType.Minimize or TabBarHitType.Maximize or TabBarHitType.Close
                ? StandardCursor.Hand
                : StandardCursor.Default);
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
            _selectionMoved = true;

            // Keep a local drag local even when the pointer crosses a split boundary.
            _host.SetPointerCursor(StandardCursor.IBeam);
            return;
        }

        if (!ReportingBypassed() && hit.Pane != null && hit.Inside &&
            hit.Pane.Session.Adapter.CurrentMouseMode == TerminalAdapter.MouseMode.AnyEvent)
        {
            SendMouse(hit.Pane, MouseEncoder.ButtonNone, hit, MouseEventKind.Motion);
            return;
        }

        if (_host.Ctrl && hit.Pane != null && hit.Inside)
        {
            bool hoveredLink = HyperlinkScanner.ContainsLinkAt(
                hit.Pane.Session.Adapter.Buffer, hit.ViewRow, hit.ViewColumn, _host.HyperlinkScan);
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
            SendMouse(_reportingPane, ButtonNumber(button), hit, MouseEventKind.Release);
            _reportingButtons &= ~mask;
            if (_reportingButtons == 0)
                _reportingPane = null;
        }

        _pressedButtons &= ~mask;
        if (button == MouseButton.Left)
        {
            if (_selectionPane is { } finished && !IsDraggingScrollbar && finished.Selection.HasSelection &&
                (_selectionMoved || _clickCount >= 2))
                _host.OnLocalSelectionFinished(finished);
            IsDraggingScrollbar = false;
            _scrollbarPane = null;
            _selectionPane = null;
            _selectionMoved = false;
        }
    }

    public void HandleMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (wheel.X == 0f && wheel.Y == 0f) return;

        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;
        if (_reportingPane != null && _reportingButtons != 0)
        {
            if (!ReportingBypassed())
            {
                var reportHit = HitTestAtContent(activeTab, _lastContentPointer.X, _lastContentPointer.Y, _reportingPane);
                ReportWheel(_reportingPane, reportHit, wheel);
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
            ReportWheel(pane, hit, wheel);
            return;
        }

        // Local scrolling and alternate scroll are vertical only.
        var settings = _host.MouseInput;
        int lines = AccumulateScroll(pane, reports: false, 0f, wheel.Y, settings.ScrollMultiplier).Vertical;
        if (lines == 0) return;

        var adapter = pane.Session.Adapter;
        if (settings.AlternateScroll && adapter.AlternateScrollMode &&
            adapter.Buffer.IsAlternateScreenActive && !adapter.MouseReportingEnabled)
        {
            SendAlternateScroll(pane, lines, adapter.ApplicationCursorKeysEnabled);
            return;
        }

        if (lines > 0)
            pane.ScrollUp(lines, adapter.Buffer.ScrollbackCount);
        else
            pane.ScrollDown(-lines);
    }

    // One wheel report per notch (the multiplier is for local lines; applications apply their own), both axes.
    private void ReportWheel(LeafPane pane, in PaneHit hit, ScrollWheel wheel)
    {
        var steps = AccumulateScroll(pane, reports: true, wheel.X, wheel.Y, 1.0);
        SendWheelButtons(pane, hit, steps.Vertical, MouseEncoder.WheelUp, MouseEncoder.WheelDown);
        SendWheelButtons(pane, hit, steps.Horizontal, MouseEncoder.WheelLeft, MouseEncoder.WheelRight);
    }

    private void SendWheelButtons(LeafPane pane, in PaneHit hit, int steps, int positiveButton, int negativeButton)
    {
        int button = steps > 0 ? positiveButton : negativeButton;
        for (int i = Math.Abs(steps); i > 0; i--)
            SendMouse(pane, button, hit, MouseEventKind.Press);
    }

    // The carry belongs to one pane and one target (reports vs local lines); switching either starts afresh.
    private ScrollSteps AccumulateScroll(LeafPane pane, bool reports, float deltaX, float deltaY, double multiplier)
    {
        if (!ReferenceEquals(_scrollAccumulatorPane, pane) || _scrollAccumulatorReports != reports)
        {
            _scrollAccumulator.Reset();
            _scrollAccumulatorPane = pane;
            _scrollAccumulatorReports = reports;
        }
        return _scrollAccumulator.Add(deltaX, deltaY, multiplier);
    }

    private static void SendAlternateScroll(LeafPane pane, int lines, bool applicationCursorKeys)
    {
        Span<byte> bytes = stackalloc byte[48];
        bool up = lines > 0;
        int remaining = Math.Abs(lines);
        while (remaining > 0)
        {
            int length = MouseEncoder.EncodeAlternateScroll(up, applicationCursorKeys, remaining, bytes);
            pane.Session.WriteInput(bytes[..length]);
            remaining -= length / 3;
        }
    }

    /// <summary>
    /// True while a selection drag is held outside its pane: autoscroll must keep
    /// ticking even though the pointer is no longer producing events.
    /// </summary>
    public bool SelectionAutoscrollActive
    {
        get
        {
            var pane = _selectionPane;
            if (pane == null || _host.ActiveTab == null || !LeftMouseDown || IsDraggingScrollbar)
                return false;

            float y = _lastContentPointer.Y - pane.Bounds.Y;
            return y < 0f || y > pane.Bounds.Height;
        }
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
        _motionFilter.Reset();
        _motionFilterPane = null;
        _scrollAccumulator.Reset();
        _scrollAccumulatorPane = null;
        _selectionPane = null;
        _selectionMoved = false;
        _scrollbarPane = null;
        IsDraggingScrollbar = false;
        IsScrollbarHovered = false;

        ResetClickSequence();
        _lastContentPointer = default;
        HoveredTabIndex = -1;
        HoveredTabHitType = TabBarHitType.None;
    }

}
