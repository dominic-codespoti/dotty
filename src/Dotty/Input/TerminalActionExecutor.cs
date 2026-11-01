using System;
using System.Buffers;
using System.Text;
using Dotty.Abstractions.Config;

using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Terminal.Adapter;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk.Input;

public sealed class TerminalActionExecutor
{
    private readonly ITerminalKeyboardHost _host;
    private readonly Action<TerminalTab> _toggleSearch;
    private readonly Func<TerminalTab, int, bool> _searchStep;

    /// <param name="host">Window services.</param>
    /// <param name="toggleSearch">Opens or closes the tab's search overlay.</param>
    /// <param name="searchStep">Moves the active match by the delta; false when the overlay is closed or has no matches.</param>
    public TerminalActionExecutor(ITerminalKeyboardHost host, Action<TerminalTab> toggleSearch, Func<TerminalTab, int, bool> searchStep)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _toggleSearch = toggleSearch ?? throw new ArgumentNullException(nameof(toggleSearch));
        _searchStep = searchStep ?? throw new ArgumentNullException(nameof(searchStep));
    }

    /// <summary>
    /// Runs <paramref name="bound"/> against the active tab. False means "not performed" and the key goes on to
    /// the search overlay or the application (scroll on the alternate screen, copy_or_send without a selection, ...).
    /// </summary>
    public bool TryExecute(BoundAction bound)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return false;

        var action = bound.Action;
        switch (action)
        {
            case TerminalAction.NewTab:
                _host.CreateTab(activeTab);
                return true;
            case TerminalAction.CloseTab:
                _host.TabManager.CloseTab(activeTab);
                return true;
            case TerminalAction.ClosePane:
                if (activeTab.PaneTree.Leaves.Count > 1)
                    activeTab.PaneTree.Close(activeTab.ActivePane);
                else
                    _host.TabManager.CloseTab(activeTab);
                return true;
            case TerminalAction.SplitVertical:
                activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Vertical);
                return true;
            case TerminalAction.SplitHorizontal:
                activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Horizontal);
                return true;
            case TerminalAction.FocusPaneLeft:
                SelectPane(activeTab, PaneDirection.Left);
                return true;
            case TerminalAction.FocusPaneRight:
                SelectPane(activeTab, PaneDirection.Right);
                return true;
            case TerminalAction.FocusPaneUp:
                SelectPane(activeTab, PaneDirection.Up);
                return true;
            case TerminalAction.FocusPaneDown:
                SelectPane(activeTab, PaneDirection.Down);
                return true;
            case TerminalAction.NextTab:
                _host.TabManager.SelectNextTab();
                return true;
            case TerminalAction.PreviousTab:
                _host.TabManager.SelectPreviousTab();
                return true;
            case TerminalAction.SwitchTab1:
                _host.TabManager.SelectTab(0);
                return true;
            case TerminalAction.SwitchTab2:
                _host.TabManager.SelectTab(1);
                return true;
            case TerminalAction.SwitchTab3:
                _host.TabManager.SelectTab(2);
                return true;
            case TerminalAction.SwitchTab4:
                _host.TabManager.SelectTab(3);
                return true;
            case TerminalAction.SwitchTab5:
                _host.TabManager.SelectTab(4);
                return true;
            case TerminalAction.SwitchTab6:
                _host.TabManager.SelectTab(5);
                return true;
            case TerminalAction.SwitchTab7:
                _host.TabManager.SelectTab(6);
                return true;
            case TerminalAction.SwitchTab8:
                _host.TabManager.SelectTab(7);
                return true;
            case TerminalAction.SwitchTab9:
                _host.TabManager.SelectTab(8);
                return true;
            case TerminalAction.Copy:
                _host.CopySelection();
                return true;
            case TerminalAction.Paste:
                _host.PasteClipboard();
                return true;
            case TerminalAction.Clear:
                _host.ClearTerminal(activeTab);
                return true;
            case TerminalAction.ToggleFullscreen:
                _host.ToggleFullscreen();
                return true;
            case TerminalAction.ZoomIn:
                _host.ZoomIn();
                return true;
            case TerminalAction.ZoomOut:
                _host.ZoomOut();
                return true;
            case TerminalAction.ResetZoom:
                _host.ResetZoom();
                return true;
            case TerminalAction.DuplicateTab:
                _host.DuplicateTab(activeTab);
                return true;
            case TerminalAction.CloseOtherTabs:
                _host.CloseOtherTabs(activeTab);
                return true;
            case TerminalAction.Quit:
                _host.Quit();
                return true;
            case TerminalAction.Search:
                _toggleSearch(activeTab);
                return true;
            case TerminalAction.PreviousPrompt:
                return NavigatePrompt(activeTab, previous: true);
            case TerminalAction.NextPrompt:
                return NavigatePrompt(activeTab, previous: false);
            case TerminalAction.CopyCommandOutput:
                var pane = activeTab.ActivePane;
                var buffer = pane.Session.Adapter.Buffer;
                if (!buffer.TryGetLatestCommandOutputRange(out int startRow, out int startColumn, out int endRow, out int endColumn)
                    || !pane.Selection.StartRangeExclusive(startRow, startColumn, endRow, endColumn))
                    return false;
                _host.CopySelection();
                return true;
            case TerminalAction.ScrollPageUp:
            case TerminalAction.ScrollPageDown:
            case TerminalAction.ScrollLineUp:
            case TerminalAction.ScrollLineDown:
            case TerminalAction.ScrollToTop:
            case TerminalAction.ScrollToBottom:
                return ScrollScrollback(activeTab, action);
            case TerminalAction.SelectAll:
                activeTab.ActivePane.Selection.SelectAll();
                return true;
            case TerminalAction.ClearScrollback:
                ClearScrollback(activeTab.ActivePane);
                return true;
            case TerminalAction.ReloadConfig:
                _host.ReloadConfig();
                return true;
            case TerminalAction.PasteSelection:
                _host.PastePrimary(activeTab.ActivePane);
                return true;
            case TerminalAction.SearchNext:
                return _searchStep(activeTab, 1);
            case TerminalAction.SearchPrevious:
                return _searchStep(activeTab, -1);
            case TerminalAction.CopyOrSend:
                if (!activeTab.ActivePane.Selection.HasSelection)
                    return false;
                _host.CopySelection();
                return true;
            case TerminalAction.MoveTabLeft:
                return _host.TabManager.MoveTab(activeTab, -1);
            case TerminalAction.MoveTabRight:
                return _host.TabManager.MoveTab(activeTab, 1);
            case TerminalAction.ToggleTabBar:
                _host.ToggleTabBar();
                return true;
            case TerminalAction.SendText:
                return SendText(activeTab, bound.Argument);
            default:
                return false;
        }
    }

    private static void ClearScrollback(LeafPane pane)
    {
        var buffer = pane.Session.Adapter.Buffer;
        lock (buffer.SyncRoot)
            buffer.ClearScrollback();
        pane.Selection.ClearSelection();
        pane.ScrollToBottom();
    }

    private bool SendText(TerminalTab tab, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = max > 256 ? ArrayPool<byte>.Shared.Rent(max) : null;
        Span<byte> buffer = rented ?? stackalloc byte[256];
        try
        {
            int length = Encoding.UTF8.GetBytes(text, buffer);
            var pane = tab.ActivePane;
            if (pane.ScrollOffset > 0)
                pane.ScrollToBottom();
            _host.WriteInput(tab, buffer[..length]);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
        return true;
    }

    // Not performable (so the key reaches the application) on the alternate screen or without scrollback.
    private static bool ScrollScrollback(TerminalTab tab, TerminalAction action)
    {
        var pane = tab.ActivePane;
        var buffer = pane.Session.Adapter.Buffer;
        int scrollback = buffer.ScrollbackCount;
        if (buffer.IsAlternateScreenActive || scrollback == 0)
            return false;
        int pageStep = Math.Max(1, pane.Rows / 2);
        switch (action)
        {
            case TerminalAction.ScrollPageUp: pane.ScrollUp(pageStep, scrollback); break;
            case TerminalAction.ScrollPageDown: pane.ScrollDown(pageStep); break;
            case TerminalAction.ScrollLineUp: pane.ScrollUp(1, scrollback); break;
            case TerminalAction.ScrollLineDown: pane.ScrollDown(1); break;
            case TerminalAction.ScrollToTop: pane.ScrollUp(scrollback, scrollback); break;
            default: pane.ScrollToBottom(); break;
        }
        return true;
    }

    private static bool NavigatePrompt(TerminalTab tab, bool previous)
    {
        var pane = tab.ActivePane;
        var buffer = pane.Session.Adapter.Buffer;
        int topRow = -pane.ScrollOffset;
        var mark = buffer.FindNearestPrompt(PromptKind.Prompt, topRow, searchForward: !previous);
        if (mark is not { } prompt) return false;
        pane.ScrollTo(Math.Max(0, buffer.ScrollbackCount - prompt.AbsoluteRow), buffer.ScrollbackCount);
        return true;
    }
    private static void SelectPane(TerminalTab activeTab, PaneDirection direction)
    {
        var pane = activeTab.PaneTree.NavigateFocus(activeTab.ActivePane, direction);
        if (pane != null)
            activeTab.PaneTree.ActivePane = pane;
    }
}
