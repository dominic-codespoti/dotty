using System;
using Dotty.Runtime.Input;
using Dotty.Runtime.Tabs;
using Dotty.Silk;
using Key = Silk.NET.Input.Key;

namespace Dotty.Silk.Input;

public sealed partial class TerminalKeyboardDispatcher
{
    public void HandleKeyEvent(Key key, int scancode, int primaryCodepoint, TerminalKeyEventType eventType, ReadOnlySpan<char> associatedText)
    {
        _currentPhysicalKeyIndex = FindPhysicalKeyState(key, scancode, create: eventType == TerminalKeyEventType.Press);
        var activeTab = _host.ActiveTab;
        if (activeTab == null)
            return;
        var adapter = activeTab.Session.Adapter;
        int kittyFlags = adapter.KittyKeyboardFlags;

        if (eventType == TerminalKeyEventType.Press && associatedText.IsEmpty)
        {
            ProcessKeyPress(key, scancode, primaryCodepoint);
            if (_lastPhysicalPressConsumed || kittyFlags == 0)
                return;

            _lastPhysicalPressDeferred = (kittyFlags & 16) != 0 && !_host.Ctrl && !_host.Alt &&
                (primaryCodepoint > 0 || GetBaseLayoutCodepoint(key) != 0 || key == Key.Unknown);
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
                    HandleText(associatedText);
                else
                    WriteLegacyKey(activeTab, key, primaryCodepoint);
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
            if ((kittyFlags & 16) != 0 && associatedText.IsEmpty && correlated && !_host.Ctrl && !_host.Alt &&
                (primaryCodepoint > 0 || GetBaseLayoutCodepoint(key) != 0 || key == Key.Unknown))
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
            if (_lastPhysicalPressDeferred && correlated)
            {
                PhysicalKeyState deferred = _physicalKeyStates[_currentPhysicalKeyIndex];
                _lastPhysicalPressDeferred = false;
                _lastPhysicalPressEncoded = WriteKeyEvent(activeTab, key, deferred.DeferredCodepoint,
                    deferred.DeferredPhase, ReadOnlySpan<char>.Empty, kittyFlags);
            }
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

    private bool WriteKeyEvent(TerminalTab tab, Key key, int primaryCodepoint, TerminalKeyEventType eventType,
        ReadOnlySpan<char> associatedText, int kittyFlags)
    {
        var (terminalKey, modifiers) = SilkKeyMapper.Map(key, _host.Ctrl, _host.Shift, _host.Alt, _host.Super);
        GetAlternateCodepoints(key, primaryCodepoint, associatedText,
            out int shiftedCodepoint, out int baseCodepoint);
        TerminalKeyModifiers eventModifiers = modifiers | _host.LockModifiers;
        if (eventType == TerminalKeyEventType.Press && _currentPhysicalKeyIndex >= 0)
            eventModifiers = _physicalKeyStates[_currentPhysicalKeyIndex].PressModifiers;

        var adapter = tab.Session.Adapter;
        int length = _inputEncoder.EncodeKeyEvent(terminalKey, eventModifiers, primaryCodepoint, eventType,
            associatedText, _keyEventScratch, kittyFlags, adapter.KeypadApplicationMode,
            adapter.ApplicationCursorKeysEnabled, shiftedCodepoint, baseCodepoint);
        if (length == 0)
            return false;
        _host.WriteInput(tab, _keyEventScratch.AsSpan(0, length));
        return true;
    }

    private void GetAlternateCodepoints(Key key, int primaryCodepoint, ReadOnlySpan<char> associatedText,
        out int shifted, out int baseLayout)
    {
        baseLayout = GetBaseLayoutCodepoint(key);
        if (primaryCodepoint <= 0 || baseLayout == 0 || baseLayout == primaryCodepoint)
            baseLayout = 0;

        shifted = 0;
        if (_host.Shift && !_host.Ctrl && !_host.Alt && !_host.Super)
            shifted = GetSingleCodepoint(associatedText);
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
        Key.Minus => '-', Key.Equal => '=', Key.LeftBracket => '[', Key.RightBracket => ']',
        Key.BackSlash => '\\', Key.Semicolon => ';', Key.Apostrophe => 39, Key.GraveAccent => 96,
        Key.Comma => ',', Key.Period => '.', Key.Slash => '/',
        _ => 0
    };

    private void WriteLegacyKey(TerminalTab tab, Key key, int primaryCodepoint)
    {
        Span<byte> bytes = stackalloc byte[64];
        if (primaryCodepoint > 0)
        {
            if (!_host.Ctrl && !_host.Alt)
                return;
            int textLength = EncodeLegacyTextCodepoint(primaryCodepoint, _host.Ctrl, _host.Alt, bytes);
            if (textLength != 0)
                _host.WriteInput(tab, bytes[..textLength]);
            return;
        }

        int length = SilkKeyMapper.Encode(key, _host.Ctrl, _host.Shift, _host.Alt,
            tab.Session.Adapter.KeypadApplicationMode, bytes, super: _host.Super,
            applicationCursorKeys: tab.Session.Adapter.ApplicationCursorKeysEnabled);
        if (length != 0)
            _host.WriteInput(tab, bytes[..length]);
    }
}
