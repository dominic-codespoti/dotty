using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Search;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using SearchMatch = Dotty.Runtime.Search.SearchMatch;
using Key = Silk.NET.Input.Key;

namespace Dotty.Silk.Input;

/// <summary>
/// Host services required by the terminal shortcut dispatcher.
/// </summary>
public interface ITerminalKeyboardHost
{
    TerminalTabManager TabManager { get; }
    TerminalTab? ActiveTab { get; }
    TextSelectionService SelectionService { get; }
    LuaScriptHost LuaHost { get; }
    KeybindingManager Keybindings { get; }
    ContextMenuModel? ActiveContextMenu { get; set; }
    int Rows { get; }
    bool Ctrl { get; }
    bool Shift { get; }
    bool Alt { get; }
    bool Super { get; }
    void CopySelection();
    void PasteClipboard();
    void CreateTab(TerminalTab activeTab);
    void ClearTerminal(TerminalTab activeTab);
    void WriteInput(TerminalTab activeTab, byte[] bytes);
}

/// <summary>
/// Routes keyboard events to terminal actions, search, Lua bindings, and PTY input.
/// Repeat timing and modifier state are owned by <see cref="TerminalKeyboardController"/>.
/// </summary>
public sealed class TerminalKeyboardDispatcher
{
    private readonly ITerminalKeyboardHost _host;

    public bool SearchActive { get; private set; }
    public string SearchQuery { get; private set; } = string.Empty;
    public IReadOnlyList<SearchMatch>? SearchMatches { get; private set; }
    public int ActiveMatchIndex { get; private set; } = -1;

    public TerminalKeyboardDispatcher(ITerminalKeyboardHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public void HandleKeyDown(Key key, int scancode)
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null) return;

        if (_host.LuaHost.Keybinds.TryExecute(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, key.ToString()))
        {
            return;
        }

        if (_host.Keybindings.TryGetAction(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, key.ToString(), out var action)
            && action != TerminalAction.None)
        {
            switch (action)
            {
                case TerminalAction.NewTab:
                    _host.CreateTab(activeTab);
                    return;
                case TerminalAction.CloseTab:
                    _host.TabManager.CloseTab(activeTab);
                    return;
                case TerminalAction.ClosePane:
                    if (activeTab.PaneTree.Leaves.Count > 1)
                        activeTab.PaneTree.Close(activeTab.ActivePane);
                    else
                        _host.TabManager.CloseTab(activeTab);
                    return;
                case TerminalAction.SplitVertical:
                    activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Vertical);
                    return;
                case TerminalAction.SplitHorizontal:
                    activeTab.PaneTree.Split(activeTab.ActivePane, SplitDirection.Horizontal);
                    return;
                case TerminalAction.FocusPaneLeft:
                    SelectPane(activeTab, PaneDirection.Left);
                    return;
                case TerminalAction.FocusPaneRight:
                    SelectPane(activeTab, PaneDirection.Right);
                    return;
                case TerminalAction.FocusPaneUp:
                    SelectPane(activeTab, PaneDirection.Up);
                    return;
                case TerminalAction.FocusPaneDown:
                    SelectPane(activeTab, PaneDirection.Down);
                    return;
                case TerminalAction.NextTab:
                    _host.TabManager.SelectNextTab();
                    return;
                case TerminalAction.PreviousTab:
                    _host.TabManager.SelectPreviousTab();
                    return;
                case TerminalAction.SwitchTab1:
                    _host.TabManager.SelectTab(0);
                    return;
                case TerminalAction.SwitchTab2:
                    _host.TabManager.SelectTab(1);
                    return;
                case TerminalAction.SwitchTab3:
                    _host.TabManager.SelectTab(2);
                    return;
                case TerminalAction.SwitchTab4:
                    _host.TabManager.SelectTab(3);
                    return;
                case TerminalAction.SwitchTab5:
                    _host.TabManager.SelectTab(4);
                    return;
                case TerminalAction.SwitchTab6:
                    _host.TabManager.SelectTab(5);
                    return;
                case TerminalAction.SwitchTab7:
                    _host.TabManager.SelectTab(6);
                    return;
                case TerminalAction.SwitchTab8:
                    _host.TabManager.SelectTab(7);
                    return;
                case TerminalAction.SwitchTab9:
                    _host.TabManager.SelectTab(8);
                    return;
                case TerminalAction.Copy:
                    _host.CopySelection();
                    return;
                case TerminalAction.Paste:
                    _host.PasteClipboard();
                    return;
                case TerminalAction.Search:
                    ToggleSearch();
                    return;
                case TerminalAction.Clear:
                    _host.ClearTerminal(activeTab);
                    return;
            }
        }

        if (key == Key.Escape && _host.ActiveContextMenu != null)
        {
            _host.ActiveContextMenu = null;
            return;
        }

        if (_host.Shift && !activeTab.Session.Adapter.MouseReportingEnabled)
        {
            var buffer = activeTab.Session.Adapter.Buffer;
            switch (key)
            {
                case Key.PageUp:
                    activeTab.ScrollUp(_host.Rows / 2, buffer.ScrollbackCount);
                    return;
                case Key.PageDown:
                    activeTab.ScrollDown(_host.Rows / 2);
                    return;
                case Key.Up:
                    activeTab.ScrollUp(1, buffer.ScrollbackCount);
                    return;
                case Key.Down:
                    activeTab.ScrollDown(1);
                    return;
                case Key.Home:
                    activeTab.ScrollUp(buffer.ScrollbackCount, buffer.ScrollbackCount);
                    return;
                case Key.End:
                    activeTab.ScrollToBottom();
                    return;
            }
        }

        if (SearchActive)
        {
            if (key == Key.Escape)
            {
                SearchActive = false;
                SearchMatches = null;
                ActiveMatchIndex = -1;
                return;
            }
            if (key == Key.Enter)
            {
                if (SearchMatches is { Count: > 0 })
                {
                    ActiveMatchIndex = _host.Shift
                        ? (ActiveMatchIndex <= 0 ? SearchMatches.Count - 1 : ActiveMatchIndex - 1)
                        : (ActiveMatchIndex + 1) % SearchMatches.Count;
                }
                return;
            }
            if (key == Key.Backspace)
            {
                if (SearchQuery.Length > 0)
                {
                    SearchQuery = SearchQuery[..^1];
                    UpdateSearchMatches(activeTab);
                }
                return;
            }
        }

        if (_host.SelectionService.HasSelection && !_host.Ctrl && !_host.Alt)
        {
            _host.SelectionService.ClearSelection();
        }
        if (activeTab.ScrollOffset > 0 && !_host.Shift)
        {
            activeTab.ScrollToBottom();
        }

        var bytes = SilkKeyMapper.Encode(
            key,
            _host.Ctrl,
            _host.Shift,
            _host.Alt,
            activeTab.Session.Adapter.KeypadApplicationMode,
            kittyMode: activeTab.Session.Adapter.KittyKeyboardMode,
            applicationCursorKeys: activeTab.Session.Adapter.ApplicationCursorKeysEnabled,
            super: _host.Super);
        if (bytes != null)
        {
            _host.WriteInput(activeTab, bytes);
        }
    }

    public void HandleKeyChar(char character) => HandleText(character.ToString());

    public void HandleText(string text)
    {
        if (string.IsNullOrEmpty(text) || _host.Ctrl || _host.Alt)
            return;

        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;

        if (SearchActive)
        {
            bool changed = false;
            foreach (char character in text)
            {
                if (!char.IsControl(character))
                {
                    SearchQuery += character;
                    changed = true;
                }
            }
            if (changed)
                UpdateSearchMatches(activeTab);
            return;
        }

        if (activeTab.ScrollOffset > 0)
            activeTab.ScrollToBottom();
        if (_host.SelectionService.HasSelection)
            _host.SelectionService.ClearSelection();

        activeTab.Session.WriteInput(Encoding.UTF8.GetBytes(text));
    }

    private void ToggleSearch()
    {
        SearchActive = !SearchActive;
        if (SearchActive)
        {
            SearchQuery = string.Empty;
            SearchMatches = null;
            ActiveMatchIndex = -1;
        }
    }

    private void UpdateSearchMatches(TerminalTab tab)
    {
        if (string.IsNullOrEmpty(SearchQuery))
        {
            SearchMatches = null;
            ActiveMatchIndex = -1;
            return;
        }

        using var snapshot = tab.Session.Adapter.Buffer.CaptureRenderSnapshotVisible(
            scrollOffset: 0,
            sbStart: 0,
            sbEnd: -1);
        SearchMatches = SearchEngine.FindMatches(snapshot, SearchQuery, matchCase: false, regex: false);
        ActiveMatchIndex = SearchMatches.Count > 0 ? 0 : -1;
    }

    private static void SelectPane(TerminalTab activeTab, PaneDirection direction)
    {
        var pane = activeTab.PaneTree.NavigateFocus(activeTab.ActivePane, direction);
        if (pane != null)
        {
            activeTab.PaneTree.ActivePane = pane;
        }
    }
}
