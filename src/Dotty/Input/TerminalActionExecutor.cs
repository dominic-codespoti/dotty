using System;
using Dotty.Abstractions.Config;

using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk.Input;

public sealed class TerminalActionExecutor
{
    private readonly ITerminalKeyboardHost _host;
    private readonly Action<TerminalTab> _toggleSearch;

    public TerminalActionExecutor(ITerminalKeyboardHost host, Action<TerminalTab> toggleSearch)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _toggleSearch = toggleSearch ?? throw new ArgumentNullException(nameof(toggleSearch));
    }

    public bool TryExecute(TerminalAction action)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return false;

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
            default:
                return false;
        }
    }

    private static void SelectPane(TerminalTab activeTab, PaneDirection direction)
    {
        var pane = activeTab.PaneTree.NavigateFocus(activeTab.ActivePane, direction);
        if (pane != null)
            activeTab.PaneTree.ActivePane = pane;
    }
}
