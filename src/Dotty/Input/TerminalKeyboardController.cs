using System;
using Silk.NET.Input;

namespace Dotty.Silk.Input;

public sealed class TerminalKeyboardController
{
    private readonly Action<Key, int>? _keyPressed;
    private readonly Action<char>? _characterReceived;
    private readonly Action<string>? _textReceived;
    private readonly Action? _activity;
    private readonly Func<long> _clockMilliseconds;
    private readonly long _initialDelayMs;
    private readonly long _repeatIntervalMs;

    private Key? _heldKey;
    private int _heldScancode;
    private char _heldChar;
    private char _pendingHighSurrogate;
    private long _nextKeyRepeatTimestampMs;

    public bool Ctrl { get; private set; }
    public bool Shift { get; private set; }
    public bool Alt { get; private set; }
    public bool Super { get; private set; }

    public TerminalKeyboardController(
        Action<Key, int>? keyPressed = null,
        Action<char>? characterReceived = null,
        Action? activity = null,
        Func<long>? clockMilliseconds = null,
        long initialDelayMs = 400,
        long repeatIntervalMs = 33,
        Action<string>? textReceived = null)
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
            case Key.ControlLeft or Key.ControlRight:
                Ctrl = true;
                return;
            case Key.ShiftLeft or Key.ShiftRight:
                Shift = true;
                return;
            case Key.AltLeft or Key.AltRight:
                Alt = true;
                return;
            case Key.SuperLeft or Key.SuperRight:
                Super = true;
                return;
        }

        long now = _clockMilliseconds();
        _heldKey = key;
        _heldScancode = scancode;
        _heldChar = '\0';
        _nextKeyRepeatTimestampMs = now + _initialDelayMs;

        _activity?.Invoke();
        _keyPressed?.Invoke(key, scancode);
    }

    public void HandleKeyUp(Key key, int scancode)
    {
        switch (key)
        {
            case Key.ControlLeft or Key.ControlRight:
                Ctrl = false;
                break;
            case Key.ShiftLeft or Key.ShiftRight:
                Shift = false;
                break;
            case Key.AltLeft or Key.AltRight:
                Alt = false;
                break;
            case Key.SuperLeft or Key.SuperRight:
                Super = false;
                break;
        }

        if (_heldKey == key)
        {
            _heldKey = null;
            _heldChar = '\0';
        }
    }

    public void HandleKeyChar(char c)
    {
        if (Ctrl || Alt)
        {
            _pendingHighSurrogate = '\0';
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            _heldChar = c;
            _activity?.Invoke();
            return;
        }

        if (_pendingHighSurrogate != '\0')
        {
            char high = _pendingHighSurrogate;
            _pendingHighSurrogate = '\0';
            if (char.IsLowSurrogate(c))
            {
                _heldChar = c;
                EmitText(new string(new[] { high, c }));
                return;
            }

            EmitText(high.ToString());
        }

        _heldChar = c;
        EmitText(c.ToString());
    }

    private void EmitText(string text)
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

        if (_heldChar != '\0' && !Ctrl && !Alt)
        {
            EmitText(_heldChar.ToString());
        }
        else
        {
            _keyPressed?.Invoke(_heldKey.Value, _heldScancode);
        }
    }
}
