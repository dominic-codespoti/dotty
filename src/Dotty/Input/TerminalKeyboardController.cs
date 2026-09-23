using System;
using Silk.NET.Input;

namespace Dotty.Silk.Input;

public delegate void TerminalTextReceived(ReadOnlySpan<char> text);

public sealed class TerminalKeyboardController
{
    private readonly TerminalTextReceived? _textReceived;
    private readonly Action<Key, int>? _keyPressed;
    private readonly Action<char>? _characterReceived;
    private readonly Action? _activity;
    private readonly Func<long> _clockMilliseconds;
    private readonly long _initialDelayMs;
    private readonly long _repeatIntervalMs;

    private Key? _heldKey;
    private int _heldScancode;
    private char _heldTextFirst;
    private char _heldTextSecond;
    private int _heldTextLength;
    private char _pendingHighSurrogate;
    private long _nextKeyRepeatTimestampMs;

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
        Action<Key, int>? keyPressed = null,
        Action<char>? characterReceived = null,
        Action? activity = null,
        Func<long>? clockMilliseconds = null,
        long initialDelayMs = 400,
        long repeatIntervalMs = 33,
        TerminalTextReceived? textReceived = null)
    {
        _keyPressed = keyPressed;
        _characterReceived = characterReceived;
        _textReceived = textReceived;
        _activity = activity;
        _clockMilliseconds = clockMilliseconds ?? GetDefaultClockMilliseconds;
        _initialDelayMs = initialDelayMs;
        _repeatIntervalMs = repeatIntervalMs;
    }

    private static long GetDefaultClockMilliseconds()
    {
        return System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void HandleKeyDown(Key key, int scancode)
    {
        switch (key)
        {
            case Key.ControlLeft:
                _leftCtrl = true;
                return;
            case Key.ControlRight:
                _rightCtrl = true;
                return;
            case Key.ShiftLeft:
                _leftShift = true;
                return;
            case Key.ShiftRight:
                _rightShift = true;
                return;
            case Key.AltLeft:
                _leftAlt = true;
                return;
            case Key.AltRight:
                _rightAlt = true;
                return;
            case Key.SuperLeft:
                _leftSuper = true;
                return;
            case Key.SuperRight:
                _rightSuper = true;
                return;
        }

        long now = _clockMilliseconds();
        _heldKey = key;
        _heldScancode = scancode;
        _heldTextLength = 0;
        _pendingHighSurrogate = '\0';
        _nextKeyRepeatTimestampMs = now + _initialDelayMs;

        _activity?.Invoke();
        _keyPressed?.Invoke(key, scancode);
    }

    public void HandleKeyUp(Key key, int scancode)
    {
        switch (key)
        {
            case Key.ControlLeft:
                _leftCtrl = false;
                break;
            case Key.ControlRight:
                _rightCtrl = false;
                break;
            case Key.ShiftLeft:
                _leftShift = false;
                break;
            case Key.ShiftRight:
                _rightShift = false;
                break;
            case Key.AltLeft:
                _leftAlt = false;
                break;
            case Key.AltRight:
                _rightAlt = false;
                break;
            case Key.SuperLeft:
                _leftSuper = false;
                break;
            case Key.SuperRight:
                _rightSuper = false;
                break;
        }

        if (_heldKey == key)
        {
            _heldKey = null;
            _heldTextLength = 0;
        }
    }

    public void HandleKeyChar(char c)
    {
        // Right Alt is commonly reported with an implicit right Control key by
        // the platform. It is an input composition modifier, not a shortcut
        // modifier, so it must not suppress composed text.
        if (_leftAlt || Ctrl && !_rightAlt)
        {
            _pendingHighSurrogate = '\0';
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        if (_pendingHighSurrogate != '\0')
        {
            char high = _pendingHighSurrogate;
            _pendingHighSurrogate = '\0';
            if (char.IsLowSurrogate(c))
            {
                _heldTextFirst = high;
                _heldTextSecond = c;
                _heldTextLength = 2;
                Span<char> pair = stackalloc char[2] { high, c };
                EmitText(pair);
                return;
            }
        }

        if (char.IsLowSurrogate(c))
            return;

        _heldTextFirst = c;
        _heldTextLength = 1;
        Span<char> text = stackalloc char[1] { c };
        EmitText(text);
    }

    private void EmitText(ReadOnlySpan<char> text)
    {
        if (_textReceived != null)
        {
            _activity?.Invoke();
            _textReceived(text);
            return;
        }

        foreach (char character in text)
        {
            _activity?.Invoke();
            _characterReceived?.Invoke(character);
        }
    }



    public void Tick()
    {
        if (!_heldKey.HasValue)
        {
            return;
        }

        long now = _clockMilliseconds();
        if (now < _nextKeyRepeatTimestampMs)
        {
            return;
        }

        _nextKeyRepeatTimestampMs = now + _repeatIntervalMs;

        if (_heldTextLength != 0 && !_leftAlt && (!Ctrl || _rightAlt))
        {
            Span<char> text = stackalloc char[2];
            text[0] = _heldTextFirst;
            if (_heldTextLength == 2) text[1] = _heldTextSecond;
            EmitText(text[.._heldTextLength]);
        }
        else
        {
            _activity?.Invoke();
            _keyPressed?.Invoke(_heldKey.Value, _heldScancode);
        }
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
        _heldKey = null;
        _heldScancode = 0;
        _heldTextLength = 0;
        _pendingHighSurrogate = '\0';
        _nextKeyRepeatTimestampMs = 0;
    }
}
