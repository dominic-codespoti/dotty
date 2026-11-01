using System;
using System.Text;
using Dotty.Runtime.Input;
using Dotty.Runtime.Tabs;
using Dotty.Silk;
using Key = Silk.NET.Input.Key;

namespace Dotty.Silk.Input;

public sealed partial class TerminalKeyboardDispatcher
{
    // The search overlay owns the keyboard: presses run its keys and bindings, text edits the query, and no
    // press, repeat or release (kitty release events included) is encoded for the application.
    private void HandleSearchModeKeyEvent(Key key, int scancode, int primaryCodepoint, TerminalKeyEventType eventType,
        ReadOnlySpan<char> associatedText)
    {
        switch (eventType)
        {
            case TerminalKeyEventType.Press when associatedText.IsEmpty:
                ProcessKeyPress(key, scancode, primaryCodepoint);
                return;
            case TerminalKeyEventType.Press:
                HandleText(associatedText);
                return;
            case TerminalKeyEventType.Repeat when !associatedText.IsEmpty:
                HandleText(associatedText);
                return;
            case TerminalKeyEventType.Repeat:
                if (_host.ActiveTab is { } tab)
                    HandleSearchKey(tab, EnsureState(tab), key, repeat: true);
                return;
            default:
                if (_currentPhysicalKeyIndex >= 0)
                {
                    _lastPhysicalPressConsumed = false;
                    _lastPhysicalPressDeferred = false;
                    _lastPhysicalPressEncoded = false;
                    _physicalKeyStates[_currentPhysicalKeyIndex].Active = false;
                }
                return;
        }
    }

    public void HandleKeyEvent(Key key, int scancode, int primaryCodepoint, TerminalKeyEventType eventType, ReadOnlySpan<char> associatedText)
    {
        _currentPhysicalKeyIndex = FindPhysicalKeyState(key, scancode, create: eventType == TerminalKeyEventType.Press);
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;
        var adapter = activeTab.Session.Adapter;
        int kittyFlags = adapter.KittyKeyboardFlags;

        if (SearchActive && _host.ActiveContextMenu is not { IsVisible: true })
        {
            HandleSearchModeKeyEvent(key, scancode, primaryCodepoint, eventType, associatedText);
            return;
        }

        if (eventType == TerminalKeyEventType.Press && associatedText.IsEmpty)
        {
            ProcessKeyPress(key, scancode, primaryCodepoint);
            if (_lastPhysicalPressConsumed || kittyFlags == 0)
                return;

            _lastPhysicalPressDeferred = DefersKittyKeyEvent(key, primaryCodepoint, kittyFlags);
            if (_lastPhysicalPressDeferred)
            {
                _physicalKeyStates[_currentPhysicalKeyIndex].DeferredPhase = TerminalKeyEventType.Press;
                _physicalKeyStates[_currentPhysicalKeyIndex].DeferredCodepoint = primaryCodepoint;
            }
            if (!_lastPhysicalPressDeferred)
                _lastPhysicalPressEncoded = WriteKeyEvent(activeTab, key, primaryCodepoint, eventType, associatedText, kittyFlags);
            return;
        }

        bool correlated = _currentPhysicalKeyIndex >= 0;
        if (correlated && _lastPhysicalPressConsumed)
        {
            if (eventType == TerminalKeyEventType.Release)
                _physicalKeyStates[_currentPhysicalKeyIndex].Active = false;
            return;
        }

        if (eventType == TerminalKeyEventType.Press)
        {
            if (kittyFlags == 0)
            {
                if (correlated && _lastPhysicalPressDeferred)
                {
                    _lastPhysicalPressDeferred = false;
                    WriteMetaText(activeTab, associatedText);
                    return;
                }
                // A press already encoded from the key (keypad application/navigation keys, Ctrl and name-based
                // Meta chords) must not type its character a second time.
                if (!(correlated && _lastPhysicalPressEncoded))
                    HandleText(associatedText);
                return;
            }

            if (correlated && _lastPhysicalPressEncoded && !_lastPhysicalPressDeferred)
            {
                if (!associatedText.IsEmpty && (kittyFlags & 16) != 0)
                    HandleCommittedText(associatedText);
                return;
            }
            _lastPhysicalPressDeferred = false;
            bool encoded = WriteKeyEvent(activeTab, key, primaryCodepoint, eventType, associatedText, kittyFlags);
            if (correlated)
                _lastPhysicalPressEncoded = encoded;
            if (!encoded)
                HandleText(associatedText);
            return;
        }

        if (eventType == TerminalKeyEventType.Repeat)
        {
            if (kittyFlags == 0)
            {
                if (!associatedText.IsEmpty)
                {
                    if (correlated && _lastPhysicalPressDeferred)
                    {
                        _lastPhysicalPressDeferred = false;
                        WriteMetaText(activeTab, associatedText);
                    }
                    else if (!(correlated && _lastPhysicalPressEncoded))
                    {
                        HandleText(associatedText);
                    }
                    return;
                }
                if (correlated)
                    FlushDeferred(activeTab, _currentPhysicalKeyIndex, kittyFlags);
                TerminalKeyModifiers repeatModifiers = CurrentModifiers(key);
                if (correlated && DefersLegacyMetaChord(primaryCodepoint))
                {
                    Defer(TerminalKeyEventType.Repeat, primaryCodepoint, repeatModifiers);
                    return;
                }
                bool repeatEncoded = WriteLegacyKey(activeTab, key, primaryCodepoint, repeatModifiers);
                if (correlated)
                    _lastPhysicalPressEncoded = repeatEncoded;
                return;
            }

            if (correlated && _lastPhysicalPressDeferred)
            {
                if (associatedText.IsEmpty)
                {
                    PhysicalKeyState prior = _physicalKeyStates[_currentPhysicalKeyIndex];
                    WriteKeyEvent(activeTab, key, prior.DeferredCodepoint, prior.DeferredPhase,
                        ReadOnlySpan<char>.Empty, kittyFlags);
                    _physicalKeyStates[_currentPhysicalKeyIndex].DeferredPhase = TerminalKeyEventType.Repeat;
                    _physicalKeyStates[_currentPhysicalKeyIndex].DeferredCodepoint = primaryCodepoint;
                    return;
                }
                PhysicalKeyState deferred = _physicalKeyStates[_currentPhysicalKeyIndex];
                _lastPhysicalPressDeferred = false;
                bool deferredEncoded = WriteKeyEvent(activeTab, key, deferred.DeferredCodepoint,
                    deferred.DeferredPhase, associatedText, kittyFlags);
                _lastPhysicalPressEncoded = deferredEncoded;
                if (!deferredEncoded)
                    HandleText(associatedText);
                return;
            }
            if (correlated && !associatedText.IsEmpty && _lastPhysicalPressEncoded && (kittyFlags & 16) != 0)
            {
                HandleCommittedText(associatedText);
                return;
            }

            if (correlated && !associatedText.IsEmpty && _lastPhysicalPressEncoded && (kittyFlags & 16) == 0)
                return;
            if (associatedText.IsEmpty && correlated && DefersKittyKeyEvent(key, primaryCodepoint, kittyFlags))
            {
                _lastPhysicalPressDeferred = true;
                _physicalKeyStates[_currentPhysicalKeyIndex].DeferredPhase = TerminalKeyEventType.Repeat;
                _physicalKeyStates[_currentPhysicalKeyIndex].DeferredCodepoint = primaryCodepoint;
                return;
            }
            bool encoded = WriteKeyEvent(activeTab, key, primaryCodepoint, eventType, associatedText, kittyFlags);
            if (correlated)
                _lastPhysicalPressEncoded = encoded;
            if (!encoded && !associatedText.IsEmpty)
                HandleText(associatedText);
            return;
        }

        if (eventType == TerminalKeyEventType.Release)
        {
            if (correlated)
                FlushDeferred(activeTab, _currentPhysicalKeyIndex, kittyFlags);
            if ((kittyFlags & 2) != 0 && (kittyFlags & (1 | 8)) != 0)
                WriteKeyEvent(activeTab, key, primaryCodepoint, eventType, ReadOnlySpan<char>.Empty, kittyFlags);
            if (correlated)
            {
                _lastPhysicalPressConsumed = false;
                _lastPhysicalPressDeferred = false;
                _lastPhysicalPressEncoded = false;
                _physicalKeyStates[_currentPhysicalKeyIndex].Active = false;
            }
        }
    }

    /// <summary>
    /// Sends every key event still waiting for its character. GLFW delivers a key event and the character it
    /// produces in the same event batch, so the host calls this once the batch has been drained; a deferred key
    /// that got no character (for example Alt+key on a layout position without one) is then encoded from the key.
    /// </summary>
    public void FlushDeferredKeys()
    {
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;
        int kittyFlags = activeTab.Session.Adapter.KittyKeyboardFlags;
        for (int i = 0; i < _physicalKeyStates.Length; i++)
        {
            if (_physicalKeyStates[i].Active && _physicalKeyStates[i].Deferred)
                FlushDeferred(activeTab, i, kittyFlags);
        }
    }

    private void FlushDeferred(TerminalTab tab, int index, int kittyFlags)
    {
        ref PhysicalKeyState state = ref _physicalKeyStates[index];
        if (!state.Deferred)
            return;
        state.Deferred = false;
        int current = _currentPhysicalKeyIndex;
        _currentPhysicalKeyIndex = index;
        if (kittyFlags == 0)
        {
            _physicalKeyStates[index].Encoded = WriteLegacyKey(tab, state.Key, state.DeferredCodepoint, state.DeferredModifiers);
        }
        else
        {
            _physicalKeyStates[index].Encoded = WriteKeyEvent(tab, state.Key, state.DeferredCodepoint,
                state.DeferredPhase, ReadOnlySpan<char>.Empty, kittyFlags);
        }
        _currentPhysicalKeyIndex = current;
    }

    private void Defer(TerminalKeyEventType phase, int primaryCodepoint, TerminalKeyModifiers modifiers)
    {
        ref PhysicalKeyState state = ref _physicalKeyStates[_currentPhysicalKeyIndex];
        state.Deferred = true;
        state.DeferredPhase = phase;
        state.DeferredCodepoint = primaryCodepoint;
        state.DeferredModifiers = modifiers;
    }

    // A legacy Meta chord on a text key waits for the character the layout produces (Shift included), so
    // Alt+Shift+, sends ESC < rather than ESC , (plan-12 D3). Ctrl chords are encoded from the key at once.
    private bool DefersLegacyMetaChord(int primaryCodepoint) =>
        primaryCodepoint > 0 && _host.Alt && !_host.Ctrl && !_host.Super && _host.MetaUsesCommittedText;

    private TerminalKeyModifiers CurrentModifiers(Key key) =>
        SilkKeyMapper.Map(key, _host.Ctrl, _host.Shift, _host.Alt, _host.Super).Modifiers | _host.LockModifiers;

    private void WriteMetaText(TerminalTab tab, ReadOnlySpan<char> text)
    {
        Span<byte> bytes = stackalloc byte[64];
        if (text.IsEmpty || Encoding.UTF8.GetMaxByteCount(text.Length) > bytes.Length - 1)
            return;
        bytes[0] = 0x1b;
        int length = 1 + Encoding.UTF8.GetBytes(text, bytes[1..]);
        _host.WriteInput(tab, bytes[..length]);
    }

    private bool WriteKeyEvent(TerminalTab tab, Key key, int primaryCodepoint, TerminalKeyEventType eventType,
        ReadOnlySpan<char> associatedText, int kittyFlags)
    {
        var (terminalKey, modifiers) = SilkKeyMapper.Map(key, _host.Ctrl, _host.Shift, _host.Alt, _host.Super,
            _host.KeypadNavigation && SilkKeyMapper.IsNumLockKeypadKey(key));
        TerminalKeyModifiers eventModifiers = modifiers | _host.LockModifiers;
        if (eventType == TerminalKeyEventType.Press && _currentPhysicalKeyIndex >= 0)
            eventModifiers = _physicalKeyStates[_currentPhysicalKeyIndex].PressModifiers;
        GetAlternateCodepoints(key, primaryCodepoint, associatedText, eventModifiers,
            out int shiftedCodepoint, out int baseCodepoint);
        // The character of a Ctrl, Meta or Super chord is not text the key produced; it only supplies the
        // shifted key, so it is not reported as associated text.
        ReadOnlySpan<char> reportedText =
            (eventModifiers & (TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Meta)) != 0
                ? ReadOnlySpan<char>.Empty
                : associatedText;

        var adapter = tab.Session.Adapter;
        int length = _inputEncoder.EncodeKeyEvent(terminalKey, eventModifiers, primaryCodepoint, eventType,
            reportedText, _keyEventScratch, kittyFlags, adapter.KeypadApplicationMode,
            adapter.ApplicationCursorKeysEnabled, shiftedCodepoint, baseCodepoint,
            adapter.BackarrowSendsBackspace);
        if (length == 0)
            return false;
        _host.WriteInput(tab, _keyEventScratch.AsSpan(0, length));
        return true;
    }

    // A Kitty key event waits for its character when the character is reported (flag 16, plain or shifted keys)
    // or supplies the shifted alternate key (flag 4 with Shift, K6(d)) that the key name cannot give: letters
    // shift by case, other keys need the layout's character (Alt+Shift+, = <).
    private bool DefersKittyKeyEvent(Key key, int primaryCodepoint, int kittyFlags) =>
        ((kittyFlags & 16) != 0 && !_host.Ctrl && !_host.Alt &&
            (primaryCodepoint > 0 || GetBaseLayoutCodepoint(key) != 0 || key == Key.Unknown)) ||
        ((kittyFlags & 4) != 0 && _host.Shift && !_host.Ctrl && primaryCodepoint > 0 &&
            ShiftedLetter(primaryCodepoint) == 0);

    // K6(d) (kitty spec "Key codes"): with Shift in the modifiers the shifted key is the delivered character, or
    // for a cased letter its upper case when no character arrived (Ctrl chords deliver none).
    private static void GetAlternateCodepoints(Key key, int primaryCodepoint, ReadOnlySpan<char> associatedText,
        TerminalKeyModifiers modifiers, out int shifted, out int baseLayout)
    {
        baseLayout = GetBaseLayoutCodepoint(key);
        if (primaryCodepoint <= 0 || baseLayout == 0 || baseLayout == primaryCodepoint)
            baseLayout = 0;

        shifted = 0;
        if ((modifiers & TerminalKeyModifiers.Shift) == 0)
            return;
        shifted = GetSingleCodepoint(associatedText);
        if (shifted == 0 && primaryCodepoint > 0)
            shifted = ShiftedLetter(primaryCodepoint);
    }

    private static int ShiftedLetter(int codepoint)
    {
        int upper = ToUpperScalar(codepoint);
        return upper != codepoint ? upper : 0;
    }

    private static int GetSingleCodepoint(ReadOnlySpan<char> text)
    {
        if (text.Length == 1)
            return char.IsSurrogate(text[0]) ? 0 : text[0];
        if (text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]))
            return char.ConvertToUtf32(text[0], text[1]);
        return 0;
    }

    private static int GetBaseLayoutCodepoint(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (key - Key.A),
        >= Key.Number0 and <= Key.Number9 => '0' + (key - Key.Number0),
        Key.Space => ' ',
        Key.Minus => '-',
        Key.Equal => '=',
        Key.LeftBracket => '[',
        Key.RightBracket => ']',
        Key.BackSlash => '\\',
        Key.Semicolon => ';',
        Key.Apostrophe => 39,
        Key.GraveAccent => 96,
        Key.Comma => ',',
        Key.Period => '.',
        Key.Slash => '/',
        _ => 0
    };

    private bool WriteLegacyKey(TerminalTab tab, Key key, int primaryCodepoint, TerminalKeyModifiers modifiers)
    {
        bool ctrl = (modifiers & TerminalKeyModifiers.Control) != 0;
        bool shift = (modifiers & TerminalKeyModifiers.Shift) != 0;
        bool alt = (modifiers & TerminalKeyModifiers.Alt) != 0;
        bool super = (modifiers & TerminalKeyModifiers.Meta) != 0;
        var adapter = tab.Session.Adapter;
        bool keypadNavigation = _host.KeypadNavigation && SilkKeyMapper.IsNumLockKeypadKey(key);
        // Keypad keys in application keypad mode or at their navigation level (Num Lock off) are encoded from the
        // key even though GLFW names them; otherwise the keypad types the layout's character (plan-12 D8).
        bool keypadEncoded = key is >= Key.Keypad0 and <= Key.KeypadEqual &&
            (keypadNavigation || adapter.KeypadApplicationMode);
        Span<byte> bytes = stackalloc byte[64];
        if (primaryCodepoint > 0 && !keypadEncoded)
        {
            if (!ctrl && !alt)
                return false;
            int codepoint = primaryCodepoint;
            if (ctrl)
            {
                // Non-Latin layouts: Ctrl+С on a Russian layout is Ctrl+C of the physical key (plan-12 D4).
                int physical = GetBaseLayoutCodepoint(key);
                if (GetControlCodepoint(codepoint) < 0 && physical != 0)
                    codepoint = physical;
            }
            else if (shift)
            {
                // A Meta chord built from the key name (macOS Meta, or no character arrived) still honours Shift.
                codepoint = ToUpperScalar(codepoint);
            }
            int textLength = EncodeLegacyTextCodepoint(codepoint, ctrl, alt, bytes);
            if (textLength == 0)
                return false;
            _host.WriteInput(tab, bytes[..textLength]);
            return true;
        }

        int length = SilkKeyMapper.Encode(key, ctrl, shift, alt,
            adapter.KeypadApplicationMode, bytes, super: super,
            applicationCursorKeys: adapter.ApplicationCursorKeysEnabled,
            modifyOtherKeysLevel: adapter.ModifyOtherKeysLevel,
            backarrowSendsBackspace: adapter.BackarrowSendsBackspace,
            keypadNavigation: keypadNavigation);
        if (length == 0)
            return false;
        _host.WriteInput(tab, bytes[..length]);
        return true;
    }

    private static int ToUpperScalar(int codepoint) =>
        codepoint <= 0xFFFF && !char.IsSurrogate((char)codepoint)
            ? char.ToUpperInvariant((char)codepoint)
            : codepoint;
}
