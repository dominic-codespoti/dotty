using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Search;
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
    LuaScriptHost LuaHost { get; }
    KeybindingManager Keybindings { get; }
    ContextMenuModel? ActiveContextMenu { get; set; }
    int Rows { get; }
    bool Ctrl { get; }
    bool Shift { get; }
    bool Alt { get; }
    bool AltGr { get; }
    bool Super { get; }
    TerminalKeyModifiers LockModifiers { get; }
    void CopySelection();
    void PasteClipboard();
    void ToggleFullscreen();
    void ZoomIn();
    void ZoomOut();
    void ResetZoom();
    void DuplicateTab(TerminalTab activeTab);
    void CloseOtherTabs(TerminalTab activeTab);
    void Quit();
    void CreateTab(TerminalTab activeTab);
    void ClearTerminal(TerminalTab activeTab);
    void WriteInput(TerminalTab activeTab, ReadOnlySpan<byte> bytes);
}

/// <summary>
/// Routes keyboard events to terminal actions, search, Lua bindings, and PTY input.
/// Native key actions drive phases; the controller tracks held keys and shortcut modifiers.
/// </summary>
public sealed partial class TerminalKeyboardDispatcher : IDisposable
{
    private sealed class SearchState
    {
        public bool IsActive;
        public string Query = string.Empty;
        public int Cursor;
        public IReadOnlyList<SearchMatch>? Matches;
        public int ActiveMatchIndex = -1;
        public LeafPane? SourcePane;
    }

    private readonly ITerminalKeyboardHost _host;
    private readonly Dictionary<TerminalTab, SearchState> _searchStates = new();
    private readonly Dictionary<TerminalTab, Action<LeafPane, LeafPane>> _paneHandlers = new();
    private readonly TerminalInputEncoder _inputEncoder = new();
    private readonly byte[] _keyEventScratch = new byte[256];
    private byte[] _textInputScratch = new byte[512];
    private struct PhysicalKeyState
    {
        public Key Key;
        public int Scancode;
        public bool Active;
        public bool Encoded;
        public bool Consumed;
        public bool Deferred;
        public TerminalKeyModifiers PressModifiers;
        public TerminalKeyEventType DeferredPhase;
        public int DeferredCodepoint;
    }

    private PhysicalKeyState[] _physicalKeyStates = new PhysicalKeyState[16];
    private int _currentPhysicalKeyIndex = -1;
    private bool _lastPhysicalPressEncoded
    {
        get => _currentPhysicalKeyIndex >= 0 && _physicalKeyStates[_currentPhysicalKeyIndex].Encoded;
        set { if (_currentPhysicalKeyIndex >= 0) _physicalKeyStates[_currentPhysicalKeyIndex].Encoded = value; }
    }
    private bool _lastPhysicalPressConsumed
    {
        get => _currentPhysicalKeyIndex >= 0 && _physicalKeyStates[_currentPhysicalKeyIndex].Consumed;
        set { if (_currentPhysicalKeyIndex >= 0) _physicalKeyStates[_currentPhysicalKeyIndex].Consumed = value; }
    }
    private bool _lastPhysicalPressDeferred
    {
        get => _currentPhysicalKeyIndex >= 0 && _physicalKeyStates[_currentPhysicalKeyIndex].Deferred;
        set { if (_currentPhysicalKeyIndex >= 0) _physicalKeyStates[_currentPhysicalKeyIndex].Deferred = value; }
    }
    private bool _isDisposed;

    public bool SearchActive =>
        _host.ActiveTab is { } tab &&
        _searchStates.TryGetValue(tab, out var state) &&
        state.IsActive;

    public string SearchQuery =>
        _host.ActiveTab is { } tab &&
        _searchStates.TryGetValue(tab, out var state)
            ? state.Query
            : string.Empty;

    public int SearchQueryCursor =>
        _host.ActiveTab is { } tab &&
        _searchStates.TryGetValue(tab, out var state)
            ? state.Cursor
            : 0;
    public int SearchCursor => SearchQueryCursor;

    public IReadOnlyList<SearchMatch>? SearchMatches =>
        _host.ActiveTab is { } tab &&
        _searchStates.TryGetValue(tab, out var state)
            ? state.Matches
            : null;

    public int ActiveMatchIndex =>
        _host.ActiveTab is { } tab &&
        _searchStates.TryGetValue(tab, out var state)
            ? state.ActiveMatchIndex
            : -1;

    public TerminalActionExecutor Actions { get; }

    public TerminalKeyboardDispatcher(ITerminalKeyboardHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Actions = new TerminalActionExecutor(_host, tab => ToggleSearch(tab, EnsureState(tab)));
        _host.TabManager.TabAdded += OnTabAdded;
        _host.TabManager.TabClosed += OnTabClosed;
        _host.TabManager.ActiveTabChanged += OnActiveTabChanged;


        foreach (var tab in _host.TabManager.Tabs)
            SubscribeTab(tab);

        if (_host.ActiveTab != null)
            SubscribeTab(_host.ActiveTab);
    }

    private int FindPhysicalKeyState(Key key, int scancode, bool create)
    {
        for (int i = 0; i < _physicalKeyStates.Length; i++)
        {
            ref PhysicalKeyState state = ref _physicalKeyStates[i];
            if (state.Active && state.Key == key && state.Scancode == scancode)
                return i;
        }
        if (!create)
            return -1;
        for (int i = 0; i < _physicalKeyStates.Length; i++)
        {
            if (_physicalKeyStates[i].Active)
                continue;
            _physicalKeyStates[i] = new PhysicalKeyState { Key = key, Scancode = scancode, Active = true };
            return i;
        }
        int oldLength = _physicalKeyStates.Length;
        Array.Resize(ref _physicalKeyStates, oldLength * 2);
        _physicalKeyStates[oldLength] = new PhysicalKeyState { Key = key, Scancode = scancode, Active = true };
        return oldLength;
    }

    private void ProcessKeyPress(Key key, int scancode, int primaryCodepoint)
    {
        ref PhysicalKeyState state = ref _physicalKeyStates[_currentPhysicalKeyIndex];
        state.PressModifiers = SilkKeyMapper.Map(key, _host.Ctrl, _host.Shift, _host.Alt, _host.Super).Modifiers | _host.LockModifiers;
        state.Key = key;
        state.Scancode = scancode;
        state.Active = true;
        state.Consumed = false;
        state.Deferred = false;
        state.Encoded = false;
        _lastPhysicalPressConsumed = false;
        _lastPhysicalPressDeferred = false;
        _lastPhysicalPressEncoded = false;

        if (HandleContextMenuKey(key))
        {
            _lastPhysicalPressConsumed = true;
            return;
        }

        var activeTab = _host.ActiveTab;
        if (_host.AltGr || activeTab == null)
        {
            _lastPhysicalPressConsumed = true;
            return;
        }
        var searchState = EnsureState(activeTab);

        if (_host.LuaHost.Keybinds.TryExecute(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, SilkKeyMapper.GetKeyName(key)) ||
            (TryGetAction(key, out var action) && action != TerminalAction.None && Actions.TryExecute(action)) ||
            (searchState.IsActive && HandleSearchKey(activeTab, searchState, key)))
        {
            _lastPhysicalPressConsumed = true;
            return;
        }

        if (key == Key.Escape && _host.ActiveContextMenu != null)
        {
            _host.ActiveContextMenu = null;
            _lastPhysicalPressConsumed = true;
            return;
        }

        var activePane = activeTab.ActivePane;
        if (_host.Shift && !activeTab.Session.Adapter.MouseReportingEnabled)
        {
            var buffer = activePane.Session.Adapter.Buffer;
            int pageStep = Math.Max(1, activePane.Rows / 2);
            switch (key)
            {
                case Key.PageUp:
                    activePane.ScrollUp(pageStep, buffer.ScrollbackCount);
                    _lastPhysicalPressConsumed = true;
                    return;
                case Key.PageDown:
                    activePane.ScrollDown(pageStep);
                    _lastPhysicalPressConsumed = true;
                    return;
                case Key.Up:
                    activePane.ScrollUp(1, buffer.ScrollbackCount);
                    _lastPhysicalPressConsumed = true;
                    return;
                case Key.Down:
                    activePane.ScrollDown(1);
                    _lastPhysicalPressConsumed = true;
                    return;
                case Key.Home:
                    activePane.ScrollUp(buffer.ScrollbackCount, buffer.ScrollbackCount);
                    _lastPhysicalPressConsumed = true;
                    return;
                case Key.End:
                    activePane.ScrollToBottom();
                    _lastPhysicalPressConsumed = true;
                    return;
            }
        }

        if (activePane.Selection.HasSelection && !_host.Ctrl && !_host.Alt)
            activePane.Selection.ClearSelection();
        if (activePane.ScrollOffset > 0 && !_host.Shift)
            activePane.ScrollToBottom();

        var adapter = activeTab.Session.Adapter;
        if (adapter.KittyKeyboardFlags != 0)
            return;
        WriteLegacyKey(activeTab, key, primaryCodepoint);
    }

    private static int EncodeLegacyTextCodepoint(int codepoint, bool ctrl, bool alt, Span<byte> destination)
    {
        if (codepoint <= 0 || codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF)
            return 0;

        int offset = 0;
        if (alt)
            destination[offset++] = 0x1b;
        if (ctrl)
        {
            int controlCodepoint = GetControlCodepoint(codepoint);
            if (controlCodepoint < 0)
                return 0;
            destination[offset++] = (byte)controlCodepoint;
            return offset;
        }

        Span<char> scalar = stackalloc char[2];
        int charCount;
        if (codepoint <= 0xFFFF)
        {
            scalar[0] = (char)codepoint;
            charCount = 1;
        }
        else
        {
            int adjusted = codepoint - 0x10000;
            scalar[0] = (char)(0xD800 + (adjusted >> 10));
            scalar[1] = (char)(0xDC00 + (adjusted & 0x3FF));
            charCount = 2;
        }
        return offset + Encoding.UTF8.GetBytes(scalar[..charCount], destination[offset..]);
    }

    private static int GetControlCodepoint(int codepoint)
    {
        if (codepoint is >= 'a' and <= 'z')
            return codepoint - 'a' + 1;
        if (codepoint is >= 'A' and <= 'Z')
            return codepoint - 'A' + 1;
        return codepoint switch
        {
            ' ' or '@' or '2' => 0,
            '3' or '[' => 0x1B,
            '4' or '\\' => 0x1C,
            '5' or ']' => 0x1D,
            '6' or '^' => 0x1E,
            '7' or '_' or '-' or '/' => 0x1F,
            '8' or '?' => 0x7F,
            _ => -1
        };
    }

    public void HandleCommittedText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return;
        if (_host.ActiveContextMenu is { IsVisible: true })
        {
            HandleText(text);
            return;
        }
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;
        if (_host.AltGr || (!_host.Ctrl && !_host.Alt))
        {
            var searchState = EnsureState(activeTab);
            if (searchState.IsActive)
            {
                HandleText(text);
                return;
            }
        }
        else
        {
            return;
        }

        var adapter = activeTab.Session.Adapter;
        int kittyFlags = adapter.KittyKeyboardFlags;
        if ((kittyFlags & (8 | 16)) != (8 | 16))
        {
            HandleText(text);
            return;
        }

        int length = _inputEncoder.EncodeKeyEvent(TerminalKey.Unknown, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, text, _keyEventScratch, kittyFlags);
        if (length == 0)
            return;
        var activePane = activeTab.ActivePane;
        if (activePane.ScrollOffset > 0)
            activePane.ScrollToBottom();
        if (activePane.Selection.HasSelection)
            activePane.Selection.ClearSelection();
        _host.WriteInput(activeTab, _keyEventScratch.AsSpan(0, length));
    }

    public void HandleText(string text) => HandleText(text.AsSpan());
    public void HandleText(ReadOnlySpan<char> text)
    {
        if (_host.ActiveContextMenu is { IsVisible: true })
            return;
        if (text.IsEmpty)
            return;
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;

        var searchState = EnsureState(activeTab);
        bool textComposition = _host.AltGr || (!_host.Ctrl && !_host.Alt);
        if (!textComposition)
            return;

        if (searchState.IsActive)
        {
            var builder = new StringBuilder(searchState.Query.Length + text.Length);
            builder.Append(searchState.Query, 0, searchState.Cursor);
            int inserted = 0;
            foreach (char character in text)
            {
                if (!char.IsControl(character))
                {
                    builder.Append(character);
                    inserted++;
                }
            }
            builder.Append(searchState.Query, searchState.Cursor, searchState.Query.Length - searchState.Cursor);
            if (inserted > 0)
            {
                searchState.Query = builder.ToString();
                searchState.Cursor += inserted;
                RefreshSearch(activeTab, searchState, resetActiveMatch: true);
            }
            return;
        }

        var activePane = activeTab.ActivePane;
        if (activePane.ScrollOffset > 0)
            activePane.ScrollToBottom();
        if (activePane.Selection.HasSelection)
            activePane.Selection.ClearSelection();
        if (text.Length <= 128)
        {
            Span<byte> bytes = stackalloc byte[512];
            int length = Encoding.UTF8.GetBytes(text, bytes);
            _host.WriteInput(activeTab, bytes[..length]);
        }
        else
        {
            int required = Encoding.UTF8.GetMaxByteCount(text.Length);
            if (_textInputScratch.Length < required)
                Array.Resize(ref _textInputScratch, required);
            int length = Encoding.UTF8.GetBytes(text, _textInputScratch);
            _host.WriteInput(activeTab, _textInputScratch.AsSpan(0, length));
        }
    }

    private bool HandleContextMenuKey(Key key)
    {
        var menu = _host.ActiveContextMenu;
        if (menu is not { IsVisible: true })
            return false;

        switch (key)
        {
            case Key.Up:
                menu.MoveFocus(-1);
                break;
            case Key.Down:
                menu.MoveFocus(1);
                break;
            case Key.Home:
                menu.FocusFirst();
                break;
            case Key.End:
                menu.FocusLast();
                break;
            case Key.Enter:
            case Key.Space:
                menu.ExecuteFocused();
                if (!menu.IsVisible && ReferenceEquals(_host.ActiveContextMenu, menu))
                    _host.ActiveContextMenu = null;
                break;
            case Key.Escape:
                menu.Close();
                _host.ActiveContextMenu = null;
                break;
        }
        return true;
    }

    private bool TryGetAction(Key key, out TerminalAction action)
    {
        if (_host.Keybindings.TryGetAction(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, SilkKeyMapper.GetKeyName(key), out action))
            return true;

        // Silk.NET commonly reports the shifted '+' key as Equal; allow a bind
        // written as ctrl+plus without making Shift part of the chord.
        string keyName = SilkKeyMapper.GetKeyName(key);
        if (_host.Shift &&
            (key == Key.Equal || string.Equals(keyName, "Plus", StringComparison.OrdinalIgnoreCase)) &&
            _host.Keybindings.TryGetAction(_host.Ctrl, false, _host.Alt, _host.Super, "plus", out action))
            return true;
        string? numberName = key switch
        {
            Key.Number0 or Key.D0 => "0",
            Key.Number1 => "1",
            Key.Number2 => "2",
            Key.Number3 => "3",
            Key.Number4 => "4",
            Key.Number5 => "5",
            Key.Number6 => "6",
            Key.Number7 => "7",
            Key.Number8 => "8",
            Key.Number9 => "9",
            _ => null
        };
        if (numberName != null &&
            _host.Keybindings.TryGetAction(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, numberName, out action))
            return true;

        action = TerminalAction.None;
        return false;
    }

    private void ToggleSearch(TerminalTab tab, SearchState state)
    {
        state.IsActive = !state.IsActive;
        if (state.IsActive)
        {
            state.Query = string.Empty;
            state.Cursor = 0;
            state.SourcePane = tab.ActivePane;
            state.ActiveMatchIndex = -1;
            state.Matches = null;
        }
        else
        {
            state.Matches = null;
            state.ActiveMatchIndex = -1;
        }
    }

    private bool HandleSearchKey(TerminalTab tab, SearchState state, Key key)
    {
        switch (key)
        {
            case Key.Escape:
                state.IsActive = false;
                state.Matches = null;
                state.ActiveMatchIndex = -1;
                return true;
            case Key.Left:
                state.Cursor = Math.Max(0, state.Cursor - 1);
                return true;
            case Key.Right:
                state.Cursor = Math.Min(state.Query.Length, state.Cursor + 1);
                return true;
            case Key.Home:
                state.Cursor = 0;
                return true;
            case Key.End:
                state.Cursor = state.Query.Length;
                return true;
            case Key.Backspace:
                DeleteBeforeCursor(tab, state, _host.Ctrl);
                return true;
            case Key.Delete:
                DeleteAtCursor(tab, state, _host.Ctrl);
                return true;
            case Key.Enter:
                if (state.Matches is { Count: > 0 })
                {
                    int delta = _host.Shift ? -1 : 1;
                    state.ActiveMatchIndex =
                        (state.ActiveMatchIndex + delta + state.Matches.Count) % state.Matches.Count;
                    RefreshSearch(tab, state, resetActiveMatch: false);
                }
                return true;
            default:
                return false;
        }
    }

    private void DeleteBeforeCursor(TerminalTab tab, SearchState state, bool word)
    {
        if (state.Cursor <= 0)
            return;

        int start = word ? FindWordStart(state.Query, state.Cursor) : state.Cursor - 1;
        state.Query = state.Query.Remove(start, state.Cursor - start);
        state.Cursor = start;
        RefreshSearch(tab, state, resetActiveMatch: true);
    }

    private void DeleteAtCursor(TerminalTab tab, SearchState state, bool word)
    {
        if (state.Cursor >= state.Query.Length)
            return;

        int end = word ? FindWordEnd(state.Query, state.Cursor) : state.Cursor + 1;
        state.Query = state.Query.Remove(state.Cursor, end - state.Cursor);
        RefreshSearch(tab, state, resetActiveMatch: true);
    }

    private static int FindWordStart(string value, int cursor)
    {
        int start = cursor;
        while (start > 0 && char.IsWhiteSpace(value[start - 1]))
            start--;
        while (start > 0 && !char.IsWhiteSpace(value[start - 1]))
            start--;
        return start;
    }

    private static int FindWordEnd(string value, int cursor)
    {
        int end = cursor;
        while (end < value.Length && char.IsWhiteSpace(value[end]))
            end++;
        while (end < value.Length && !char.IsWhiteSpace(value[end]))
            end++;
        return end;
    }

    private void RefreshSearch(TerminalTab tab, SearchState state, bool resetActiveMatch)
    {
        if (state.SourcePane == null || !ContainsPane(tab, state.SourcePane))
            state.SourcePane = tab.ActivePane;

        if (resetActiveMatch)
            state.ActiveMatchIndex = 0;

        if (state.SourcePane == null || string.IsNullOrEmpty(state.Query))
        {
            state.Matches = null;
            state.ActiveMatchIndex = -1;
            return;
        }

        var pane = state.SourcePane;
        using var snapshot = pane.Session.Adapter.Buffer.CaptureRenderSnapshotVisible(
            scrollOffset: 0,
            sbStart: 0,
            sbEnd: -1);
        var matches = SearchEngine.FindMatches(
            snapshot,
            state.Query,
            regex: false,
            matchCase: false,
            activeMatchIndex: state.ActiveMatchIndex);
        state.Matches = matches;
        if (matches.Count == 0)
        {
            state.ActiveMatchIndex = -1;
            return;
        }

        state.ActiveMatchIndex = Math.Clamp(state.ActiveMatchIndex, 0, matches.Count - 1);
        // Re-run with the clamped index when the prior query/index was invalid,
        // so the render state always has exactly one active marker.
        if (!matches[state.ActiveMatchIndex].IsActive)
        {
            state.Matches = SearchEngine.FindMatches(
                snapshot,
                state.Query,
                regex: false,
                matchCase: false,
                activeMatchIndex: state.ActiveMatchIndex);
        }

        var activeMatch = state.Matches[state.ActiveMatchIndex];
        var buffer = pane.Session.Adapter.Buffer;
        if (activeMatch.Row < 0)
            pane.ScrollTo(-activeMatch.Row, buffer.ScrollbackCount);
        else
            pane.ScrollToBottom();
    }

    private static bool ContainsPane(TerminalTab tab, LeafPane pane)
    {
        foreach (var candidate in tab.PaneTree.Leaves)
        {
            if (ReferenceEquals(candidate, pane))
                return true;
        }
        return false;
    }

    private void OnTabAdded(TerminalTab tab) => SubscribeTab(tab);

    private void OnActiveTabChanged(TerminalTab? tab)
    {
        if (tab != null)
            SubscribeTab(tab);
    }

    private void OnTabClosed(TerminalTab tab)
    {
        if (_paneHandlers.Remove(tab, out var handler))
            tab.PaneTree.ActivePaneChanged -= handler;
        _searchStates.Remove(tab);
    }

    private void SubscribeTab(TerminalTab tab)
    {
        if (_paneHandlers.ContainsKey(tab))
            return;

        Action<LeafPane, LeafPane> handler = CreatePaneChangedHandler(tab);
        _paneHandlers.Add(tab, handler);
        tab.PaneTree.ActivePaneChanged += handler;
        EnsureState(tab).SourcePane ??= tab.ActivePane;
    }

    private SearchState EnsureState(TerminalTab tab)
    {
        SubscribePaneOnly(tab);
        if (!_searchStates.TryGetValue(tab, out var state))
        {
            state = new SearchState { SourcePane = tab.ActivePane };
            _searchStates.Add(tab, state);
        }
        return state;
    }

    private void SubscribePaneOnly(TerminalTab tab)
    {
        if (_paneHandlers.ContainsKey(tab))
            return;

        Action<LeafPane, LeafPane> handler = CreatePaneChangedHandler(tab);
        _paneHandlers.Add(tab, handler);
        tab.PaneTree.ActivePaneChanged += handler;
    }
    private Action<LeafPane, LeafPane> CreatePaneChangedHandler(TerminalTab tab) =>
        (_, newPane) => OnActivePaneChanged(tab, newPane);

    private void OnActivePaneChanged(TerminalTab tab, LeafPane newPane)
    {
        if (!_searchStates.TryGetValue(tab, out var state))
            return;

        state.SourcePane = newPane;
        if (state.IsActive)
        {
            state.ActiveMatchIndex = 0;
            RefreshSearch(tab, state, resetActiveMatch: false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        _host.TabManager.TabAdded -= OnTabAdded;
        _host.TabManager.TabClosed -= OnTabClosed;
        _host.TabManager.ActiveTabChanged -= OnActiveTabChanged;
        foreach (var (tab, handler) in _paneHandlers)
            tab.PaneTree.ActivePaneChanged -= handler;
        _paneHandlers.Clear();
        _searchStates.Clear();
    }

}
