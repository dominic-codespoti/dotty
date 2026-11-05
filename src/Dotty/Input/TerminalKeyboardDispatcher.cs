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
    /// <summary>A Meta chord's delivered character is the key's layout character (see <see cref="KeyboardModifierState.MetaUsesCommittedText"/>).</summary>
    bool MetaUsesCommittedText { get; }
    /// <summary>Keypad digits send navigation keys (Num Lock off; see <see cref="KeyboardModifierState.KeypadNavigation"/>).</summary>
    bool KeypadNavigation { get; }
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
    /// <summary>Pastes the primary selection into <paramref name="targetPane"/>.</summary>
    void PastePrimary(LeafPane targetPane);
    /// <summary>Re-reads the configuration files (as the file watcher would).</summary>
    void ReloadConfig();
    /// <summary>Shows or hides the tab bar for this run (the next config reload restores <c>tabBar.show</c>).</summary>
    void ToggleTabBar();
    /// <summary>The clipboard text, or null (search overlay paste).</summary>
    string? ReadClipboardText();
    /// <summary>Initial toggles of the search overlay (<c>search.*</c> config).</summary>
    Dotty.Runtime.Config.SearchUserConfig SearchDefaults { get; }
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
        public readonly SearchQueryEditor Editor = new();
        public bool Regex;
        public bool CaseSensitive;
        public bool WholeWord;
        public bool RegexInvalid;
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
        public TerminalKeyModifiers DeferredModifiers;
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

    public string SearchQuery => ActiveSearchState?.Editor.Text ?? string.Empty;

    public int SearchQueryCursor => ActiveSearchState?.Editor.Cursor ?? 0;

    /// <summary>The overlay's regex toggle (Alt+R; <c>search.regex</c> when it opens).</summary>
    public bool SearchRegex => ActiveSearchState?.Regex ?? false;

    /// <summary>The overlay's case toggle (Alt+C; <c>search.caseSensitive</c> when it opens).</summary>
    public bool SearchCaseSensitive => ActiveSearchState?.CaseSensitive ?? false;

    /// <summary>The overlay's whole-word toggle (Alt+W; <c>search.wholeWord</c> when it opens).</summary>
    public bool SearchWholeWord => ActiveSearchState?.WholeWord ?? false;

    /// <summary>Regex mode is on and the query does not compile (it is then searched literally).</summary>
    public bool SearchRegexInvalid => ActiveSearchState?.RegexInvalid ?? false;

    /// <summary>Toggle states for the overlay renderer.</summary>
    public SearchOverlayFlags SearchOverlayFlags => ActiveSearchState is not { } state ? SearchOverlayFlags.None :
        (state.Regex ? SearchOverlayFlags.Regex : 0) |
        (state.CaseSensitive ? SearchOverlayFlags.CaseSensitive : 0) |
        (state.WholeWord ? SearchOverlayFlags.WholeWord : 0) |
        (state.RegexInvalid ? SearchOverlayFlags.InvalidRegex : 0);

    /// <summary>Caret column in the input box: grapheme clusters before the cursor (one cell each in the overlay).</summary>
    public int SearchQueryCursorColumn
    {
        get
        {
            if (ActiveSearchState is not { } state) return 0;
            string text = state.Editor.Text;
            int column = 0;
            for (int index = 0; index < state.Editor.Cursor; column++)
                index += System.Globalization.StringInfo.GetNextTextElementLength(text.AsSpan(index));
            return column;
        }
    }

    /// <summary>
    /// A left click at device pixels (<paramref name="x"/>, <paramref name="y"/>) on the overlay of a
    /// <paramref name="viewportWidth"/>×<paramref name="viewportHeight"/> frame: ▲/▼ step, × closes, the toggles flip,
    /// a click in the input box moves the caret. True when the click landed on the overlay (it is then consumed).
    /// </summary>
    public bool ClickSearchOverlay(float x, float y, float viewportWidth, float viewportHeight, float cellWidth)
    {
        if (_host.ActiveTab is not { } tab || !_searchStates.TryGetValue(tab, out var state) || !state.IsActive)
            return false;
        var layout = SearchOverlayLayout.Compute(viewportWidth, viewportHeight, state.Editor.Text, state.ActiveMatchIndex,
            state.Matches?.Count ?? 0, flags: SearchOverlayFlags);
        switch (layout.HitTest(x, y))
        {
            case SearchOverlayHit.None:
                return false;
            case SearchOverlayHit.Previous:
                StepSearch(tab, -1);
                break;
            case SearchOverlayHit.Next:
                StepSearch(tab, 1);
                break;
            case SearchOverlayHit.Close:
                CloseSearch(state);
                break;
            case SearchOverlayHit.ToggleRegex:
                state.Regex = !state.Regex;
                RefreshSearch(tab, state, resetActiveMatch: true);
                break;
            case SearchOverlayHit.ToggleCase:
                state.CaseSensitive = !state.CaseSensitive;
                RefreshSearch(tab, state, resetActiveMatch: true);
                break;
            case SearchOverlayHit.ToggleWord:
                state.WholeWord = !state.WholeWord;
                RefreshSearch(tab, state, resetActiveMatch: true);
                break;
            case SearchOverlayHit.Input when cellWidth > 0:
                state.Editor.SetCursorToGrapheme(Math.Max(0, (int)(x / cellWidth) - (int)(layout.InputBoxRect.X / cellWidth)));
                break;
        }
        return true;
    }

    private SearchState? ActiveSearchState =>
        _host.ActiveTab is { } tab && _searchStates.TryGetValue(tab, out var state) ? state : null;
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
        Actions = new TerminalActionExecutor(_host, tab => ToggleSearch(tab, EnsureState(tab)), StepSearch);
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
        if (activeTab == null)
        {
            _lastPhysicalPressConsumed = true;
            return;
        }


        // A Compose-role Alt (AltGr, Option) is not a shortcut modifier: the press runs through bindings with
        // Alt clear and the character the layout composes arrives as text (decision K1).
        var adapter = activeTab.Session.Adapter;
        var searchState = EnsureState(activeTab);

        // While the overlay is open nothing reaches the application: the overlay's own keys come first, then
        // bound chords (so Ctrl+Shift+F still closes it); every other key is swallowed (its text goes to the query).
        if (searchState.IsActive)
        {
            _lastPhysicalPressConsumed = true;
            if (!HandleSearchKey(activeTab, searchState, key, repeat: false))
                TryRunBinding(key, primaryCodepoint);
            return;
        }

        if (adapter.FontShiftModeEnabled && _host.Shift && !_host.Alt && !_host.Super &&
            (key == Key.KeypadAdd || key == Key.KeypadSubtract))
        {
            if (key == Key.KeypadAdd && !_host.Ctrl)
                _host.ZoomIn();
            else
                _host.ZoomOut();
            _lastPhysicalPressConsumed = true;
            return;
        }

        if (TryRunBinding(key, primaryCodepoint))
        {
            _lastPhysicalPressConsumed = true;
            return;
        }

        if (adapter.KeyboardActionModeLocked)
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

        // Keys that produce no input (bare modifiers, lock keys, Menu) leave the scroll position and the
        // selection alone, so Ctrl+click on a scrollback link and Shift+click extension keep working (plan-12 D5).
        // Scrollback navigation is bound to actions (ScrollPageUp, ScrollLineUp, ...), not hard-wired to Shift.
        var activePane = activeTab.ActivePane;
        if (!IsInputlessKey(key))
        {
            if (!_host.Ctrl && !_host.Alt && ClearsSelectionOnTyping && activePane.Selection.HasSelection)
                activePane.Selection.ClearSelection();
            if (activePane.ScrollOffset > 0)
                activePane.ScrollToBottom();
        }

        if (adapter.KittyKeyboardFlags != 0)
            return;
        if (DefersLegacyMetaChord(primaryCodepoint))
        {
            Defer(TerminalKeyEventType.Press, primaryCodepoint, state.PressModifiers);
            return;
        }
        _lastPhysicalPressEncoded = WriteLegacyKey(activeTab, key, primaryCodepoint, state.PressModifiers);
    }

    /// <summary><c>selection.clearOnTyping</c>, read live.</summary>
    private static bool ClearsSelectionOnTyping => Dotty.Runtime.Config.UserConfigService.Current.Selection.ClearOnTyping;

    private static bool IsInputlessKey(Key key) => key is
        Key.ShiftLeft or Key.ShiftRight or Key.ControlLeft or Key.ControlRight or
        Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight or
        Key.CapsLock or Key.NumLock or Key.ScrollLock or Key.Menu;

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
        if (adapter.KeyboardActionModeLocked)
            return;
        int kittyFlags = adapter.KittyKeyboardFlags;
        if ((kittyFlags & (8 | 16)) != (8 | 16))
        {
            HandleText(text);
            return;
        }

        int length = _inputEncoder.EncodeKeyEvent(TerminalKey.Unknown, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, text, _keyEventScratch, kittyFlags,
            backarrowSendsBackspace: adapter.BackarrowSendsBackspace);
        if (length == 0)
            return;
        var activePane = activeTab.ActivePane;
        if (activePane.ScrollOffset > 0)
            activePane.ScrollToBottom();
        if (ClearsSelectionOnTyping && activePane.Selection.HasSelection)
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
            if (searchState.Editor.Insert(text))
                RefreshSearch(activeTab, searchState, resetActiveMatch: true);
            return;
        }
        if (activeTab.Session.Adapter.KeyboardActionModeLocked)
            return;
        var activePane = activeTab.ActivePane;
        if (activePane.ScrollOffset > 0)
            activePane.ScrollToBottom();
        if (ClearsSelectionOnTyping && activePane.Selection.HasSelection)
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
                if (ReferenceEquals(_host.ActiveContextMenu, menu))
                    _host.ActiveContextMenu = null;
                break;
        }
        return true;
    }

    // Lua bindings first, then the binding table; matching per KeybindingManager.TryMatch (physical:, layout, physical).
    private bool TryRunBinding(Key key, int primaryCodepoint)
    {
        string keyName = SilkKeyMapper.GetKeyName(key);
        if (_host.LuaHost.Keybinds.TryExecute(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, keyName, primaryCodepoint))
            return true;
        return _host.Keybindings.TryGetBinding(_host.Ctrl, _host.Shift, _host.Alt, _host.Super, keyName, primaryCodepoint, out var binding)
            && binding.Action != TerminalAction.None
            && Actions.TryExecute(binding);
    }

    private void ToggleSearch(TerminalTab tab, SearchState state)
    {
        if (state.IsActive)
        {
            CloseSearch(state);
            return;
        }
        state.IsActive = true;
        state.Editor.Clear();
        var defaults = _host.SearchDefaults;
        state.Regex = defaults.Regex;
        state.CaseSensitive = defaults.CaseSensitive;
        state.WholeWord = defaults.WholeWord;
        state.RegexInvalid = false;
        state.SourcePane = tab.ActivePane;
        state.ActiveMatchIndex = -1;
        state.Matches = null;
    }

    private static void CloseSearch(SearchState state)
    {
        state.IsActive = false;
        state.Matches = null;
        state.ActiveMatchIndex = -1;
    }

    /// <summary>Closes the active tab's search overlay (the overlay's × button). False when it was not open.</summary>
    public bool CloseSearch()
    {
        if (ActiveSearchState is not { IsActive: true } state)
            return false;
        CloseSearch(state);
        return true;
    }

    /// <summary>Moves the active match of the active tab by <paramref name="delta"/> (the ▲▼ buttons). False when closed or matchless.</summary>
    public bool StepSearch(int delta) => _host.ActiveTab is { } tab && StepSearch(tab, delta);

    private bool StepSearch(TerminalTab tab, int delta)
    {
        if (!_searchStates.TryGetValue(tab, out var state) || !state.IsActive)
            return false;
        if (state.Matches is { Count: > 0 } matches)
        {
            state.ActiveMatchIndex = ((state.ActiveMatchIndex + delta) % matches.Count + matches.Count) % matches.Count;
            RefreshSearch(tab, state, resetActiveMatch: false);
        }
        return true;
    }

    /// <summary>Places the query cursor before the <paramref name="grapheme"/>-th grapheme (a click into the input box).</summary>
    public bool SetSearchCursor(int grapheme) =>
        ActiveSearchState is { IsActive: true } state && state.Editor.SetCursorToGrapheme(Math.Max(0, grapheme));

    /// <summary>
    /// The overlay's own keys. Returns false for keys it does not use (bindings may then run; the key is swallowed
    /// either way). <paramref name="repeat"/> limits a held key to navigation and editing.
    /// </summary>
    private bool HandleSearchKey(TerminalTab tab, SearchState state, Key key, bool repeat)
    {
        bool ctrl = _host.Ctrl, shift = _host.Shift, alt = _host.Alt;
        var editor = state.Editor;
        switch (key)
        {
            case Key.Escape when !repeat:
            case Key.C when ctrl && !shift && !alt && !repeat:
                CloseSearch(state);
                return true;
            case Key.Enter or Key.KeypadEnter:
                StepSearch(tab, shift ? -1 : 1);
                return true;
            case Key.Up:
                StepSearch(tab, -1);
                return true;
            case Key.Down:
                StepSearch(tab, 1);
                return true;
            case Key.F3:
                StepSearch(tab, shift ? -1 : 1);
                return true;
            case Key.PageUp or Key.PageDown:
                ScrollSource(tab, state, up: key == Key.PageUp);
                return true;
            case Key.Left:
                editor.MoveLeft(word: ctrl);
                return true;
            case Key.Right:
                editor.MoveRight(word: ctrl);
                return true;
            case Key.Home:
                editor.MoveHome();
                return true;
            case Key.End:
                editor.MoveEnd();
                return true;
            case Key.Backspace:
                if (editor.Backspace(word: ctrl))
                    RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            case Key.Delete:
                if (editor.Delete(word: ctrl))
                    RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            case Key.V when ctrl && !alt && !repeat:
                if (editor.Paste(_host.ReadClipboardText()))
                    RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            case Key.R when alt && !ctrl && !repeat:
                state.Regex = !state.Regex;
                RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            case Key.C when alt && !ctrl && !repeat:
                state.CaseSensitive = !state.CaseSensitive;
                RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            case Key.W when alt && !ctrl && !repeat:
                state.WholeWord = !state.WholeWord;
                RefreshSearch(tab, state, resetActiveMatch: true);
                return true;
            default:
                return false;
        }
    }

    private static void ScrollSource(TerminalTab tab, SearchState state, bool up)
    {
        var pane = state.SourcePane != null && ContainsPane(tab, state.SourcePane) ? state.SourcePane : tab.ActivePane;
        int step = Math.Max(1, pane.Rows - 1);
        if (up)
            pane.ScrollUp(step, pane.Session.Adapter.Buffer.ScrollbackCount);
        else
            pane.ScrollDown(step);
    }

    private void RefreshSearch(TerminalTab tab, SearchState state, bool resetActiveMatch)
    {
        if (state.SourcePane == null || !ContainsPane(tab, state.SourcePane))
            state.SourcePane = tab.ActivePane;

        if (resetActiveMatch)
            state.ActiveMatchIndex = 0;

        string query = state.Editor.Text;
        state.RegexInvalid = state.Regex && !SearchEngine.IsValidRegex(query);
        if (state.SourcePane == null || string.IsNullOrEmpty(query))
        {
            state.Matches = null;
            state.ActiveMatchIndex = -1;
            return;
        }

        var pane = state.SourcePane;
        // The search capture copies the scrollback cells too (and takes SyncRoot itself); the per-frame
        // visible capture (sbEnd: -1) carries no scrollback, so scrollback matches were never found.
        using var snapshot = pane.Session.Adapter.Buffer.CaptureRenderSnapshotForSearch();
        var matches = SearchEngine.FindMatches(
            snapshot,
            query,
            regex: state.Regex,
            matchCase: state.CaseSensitive,
            activeMatchIndex: state.ActiveMatchIndex,
            wholeWord: state.WholeWord);
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
                query,
                regex: state.Regex,
                matchCase: state.CaseSensitive,
                activeMatchIndex: state.ActiveMatchIndex,
                wholeWord: state.WholeWord);
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
