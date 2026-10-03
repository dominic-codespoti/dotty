using System;
using Silk.NET.Input;
using Dotty.Runtime.Input;

namespace Dotty.Silk.Input;

public delegate void TerminalKeyboardEventReceived(Key key, int scancode, int primaryCodepoint, TerminalKeyEventType eventType, ReadOnlySpan<char> associatedText);
public delegate void TerminalTextReceived(ReadOnlySpan<char> text);

public sealed class TerminalKeyboardController
{
    private readonly TerminalTextReceived? _textReceived;
    private readonly TerminalKeyboardEventReceived? _keyEventReceived;
    private readonly Action? _activity;
    private readonly Func<Key, int, int>? _primaryCodepointProvider;

    private struct HeldKeyState
    {
        public Key Key;
        public int Scancode;
        public int PrimaryCodepoint;
        public char TextFirst;
        public char TextSecond;
        public int TextLength;
        public long PressOrder;
        public TerminalKeyEventType PendingTextPhase;
        public bool Active;
    }

    private HeldKeyState[] _heldKeys = new HeldKeyState[16];
    private int _activeTextIndex = -1;
    private long _pressSequence;

    private bool _leftCtrl;
    private bool _rightCtrl;
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _leftSuper;
    private bool _rightSuper;

    public bool LeftCtrl => _leftCtrl;
    public bool RightCtrl => _rightCtrl;
    public bool Ctrl => _leftCtrl || _rightCtrl;
    public bool LeftShift => _leftShift;
    public bool RightShift => _rightShift;
    public bool Shift => _leftShift || _rightShift;
    public bool LeftAlt => _leftAlt;
    public bool RightAlt => _rightAlt;
    public bool AltGr => _rightAlt;
    public bool Alt => _leftAlt || _rightAlt;
    public bool LeftSuper => _leftSuper;
    public bool RightSuper => _rightSuper;
    public bool Super => _leftSuper || _rightSuper;

    public TerminalKeyboardController(
        TerminalKeyboardEventReceived? keyEventReceived = null,
        Action? activity = null,
        TerminalTextReceived? textReceived = null,
        Func<Key, int, int>? primaryCodepointProvider = null)
    {
        _keyEventReceived = keyEventReceived;
        _textReceived = textReceived;
        _activity = activity;
        _primaryCodepointProvider = primaryCodepointProvider;
    }

    public void HandleKeyDown(Key key, int scancode)
    {
        UpdateModifierState(key, pressed: true);
        StartOrUpdateHeldKey(key, scancode, TerminalKeyEventType.Press);
        int index = FindHeldKey(key, scancode);
        ref HeldKeyState state = ref _heldKeys[index];
        _activity?.Invoke();
        _keyEventReceived?.Invoke(key, scancode, state.PrimaryCodepoint, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
    }

    public void HandleKeyRepeat(Key key, int scancode)
    {
        int index = FindHeldKey(key, scancode);
        if (index < 0)
        {
            StartOrUpdateHeldKey(key, scancode, TerminalKeyEventType.Repeat);
            index = FindHeldKey(key, scancode);
        }
        ref HeldKeyState state = ref _heldKeys[index];
        _activeTextIndex = index;
        state.PendingTextPhase = TerminalKeyEventType.Repeat;
        _activity?.Invoke();
        _keyEventReceived?.Invoke(key, scancode, state.PrimaryCodepoint, TerminalKeyEventType.Repeat, ReadOnlySpan<char>.Empty);
    }

    public void HandleKeyUp(Key key, int scancode)
    {
        UpdateModifierState(key, pressed: false);
        int index = FindHeldKey(key, scancode);
        if (index < 0)
            return;

        ref HeldKeyState state = ref _heldKeys[index];
        Span<char> associated = stackalloc char[2];
        associated[0] = state.TextFirst;
        if (state.TextLength == 2) associated[1] = state.TextSecond;
        _activity?.Invoke();
        _keyEventReceived?.Invoke(key, scancode, state.PrimaryCodepoint, TerminalKeyEventType.Release, associated[..state.TextLength]);
        state.Active = false;
        state.TextLength = 0;
        if (_activeTextIndex == index)
            _activeTextIndex = FindMostRecentlyPressedHeldKey();
    }

    public void HandleUnicodeScalar(uint codepoint)
    {
        if (codepoint == 0 || codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF)
            return;
        if (_leftAlt || Ctrl && !_rightAlt)
            return;

        Span<char> text = stackalloc char[2];
        int length;
        if (codepoint <= 0xFFFF)
        {
            text[0] = (char)codepoint;
            length = 1;
        }
        else
        {
            uint adjusted = codepoint - 0x10000;
            text[0] = (char)(0xD800 + (adjusted >> 10));
            text[1] = (char)(0xDC00 + (adjusted & 0x3FF));
            length = 2;
        }

        if (_activeTextIndex >= 0 && _keyEventReceived != null)
        {
            ref HeldKeyState state = ref _heldKeys[_activeTextIndex];
            if (state.Active)
            {
                state.TextFirst = text[0];
                state.TextSecond = length == 2 ? text[1] : '\0';
                state.TextLength = length;
                _activity?.Invoke();
                _keyEventReceived(state.Key, state.Scancode, state.PrimaryCodepoint, state.PendingTextPhase, text[..length]);
                return;
            }
        }
        if (_textReceived is null) return;
        _activity?.Invoke();
        _textReceived(text[..length]);
    }

    private void StartOrUpdateHeldKey(Key key, int scancode, TerminalKeyEventType phase)
    {
        int index = FindHeldKey(key, scancode);
        if (index < 0)
            index = FindFreeHeldKey();
        ref HeldKeyState state = ref _heldKeys[index];
        if (!state.Active)
        {
            int primaryCodepoint = _primaryCodepointProvider?.Invoke(key, scancode) ?? 0;
            state.Key = key;
            state.Scancode = scancode;
            state.PrimaryCodepoint = IsUnicodeScalar(primaryCodepoint) ? primaryCodepoint : 0;
            state.TextLength = 0;
            state.PressOrder = ++_pressSequence;
            state.Active = true;
        }
        state.PendingTextPhase = phase;
        _activeTextIndex = index;
    }

    private void UpdateModifierState(Key key, bool pressed)
    {
        switch (key)
        {
            case Key.ControlLeft: _leftCtrl = pressed; break;
            case Key.ControlRight: _rightCtrl = pressed; break;
            case Key.ShiftLeft: _leftShift = pressed; break;
            case Key.ShiftRight: _rightShift = pressed; break;
            case Key.AltLeft: _leftAlt = pressed; break;
            case Key.AltRight: _rightAlt = pressed; break;
            case Key.SuperLeft: _leftSuper = pressed; break;
            case Key.SuperRight: _rightSuper = pressed; break;
        }
    }

    private int FindHeldKey(Key key, int scancode)
    {
        for (int i = 0; i < _heldKeys.Length; i++)
        {
            ref HeldKeyState state = ref _heldKeys[i];
            if (state.Active && state.Key == key && state.Scancode == scancode)
                return i;
        }
        return -1;
    }

    private int FindFreeHeldKey()
    {
        for (int i = 0; i < _heldKeys.Length; i++)
        {
            if (!_heldKeys[i].Active)
                return i;
        }
        int oldLength = _heldKeys.Length;
        Array.Resize(ref _heldKeys, oldLength * 2);
        return oldLength;
    }

    private int FindMostRecentlyPressedHeldKey()
    {
        int index = -1;
        long newest = long.MinValue;
        for (int i = 0; i < _heldKeys.Length; i++)
        {
            ref HeldKeyState state = ref _heldKeys[i];
            if (state.Active && state.PressOrder > newest)
            {
                newest = state.PressOrder;
                index = i;
            }
        }
        return index;
    }

    public void ResetState()
    {
        _leftCtrl = false;
        _rightCtrl = false;
        _leftShift = false;
        _rightShift = false;
        _leftAlt = false;
        _rightAlt = false;
        _leftSuper = false;
        _rightSuper = false;
        for (int i = 0; i < _heldKeys.Length; i++)
        {
            _heldKeys[i].Active = false;
            _heldKeys[i].TextLength = 0;
        }
        _activeTextIndex = -1;
        _pressSequence = 0;
    }

    private static bool IsUnicodeScalar(int value) =>
        value > 0 && value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF);
}
