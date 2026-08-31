using System.Collections.Generic;
using Dotty.Silk.Input;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalKeyboardControllerTests
{
    private sealed class FakeClock
    {
        public long CurrentTimeMs { get; set; } = 1000;
        public void Advance(long ms) => CurrentTimeMs += ms;
    }

    [Fact]
    public void ModifierKeys_UpdateState_AndDoNotEmitKeyPressed()
    {
        var clock = new FakeClock();
        var keyEvents = new List<(Key Key, int Scancode)>();
        var charEvents = new List<char>();
        int activityCount = 0;

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            characterReceived: c => charEvents.Add(c),
            activity: () => activityCount++,
            clockMilliseconds: () => clock.CurrentTimeMs);

        // Control
        controller.HandleKeyDown(Key.ControlLeft, 1);
        Assert.True(controller.Ctrl);
        controller.HandleKeyUp(Key.ControlLeft, 1);
        Assert.False(controller.Ctrl);

        controller.HandleKeyDown(Key.ControlRight, 2);
        Assert.True(controller.Ctrl);
        controller.HandleKeyUp(Key.ControlRight, 2);
        Assert.False(controller.Ctrl);

        // Shift
        controller.HandleKeyDown(Key.ShiftLeft, 3);
        Assert.True(controller.Shift);
        controller.HandleKeyUp(Key.ShiftLeft, 3);
        Assert.False(controller.Shift);

        controller.HandleKeyDown(Key.ShiftRight, 4);
        Assert.True(controller.Shift);
        controller.HandleKeyUp(Key.ShiftRight, 4);
        Assert.False(controller.Shift);

        // Alt
        controller.HandleKeyDown(Key.AltLeft, 5);
        Assert.True(controller.Alt);
        controller.HandleKeyUp(Key.AltLeft, 5);
        Assert.False(controller.Alt);

        controller.HandleKeyDown(Key.AltRight, 6);
        Assert.True(controller.Alt);
        controller.HandleKeyUp(Key.AltRight, 6);
        Assert.False(controller.Alt);

        // Super
        controller.HandleKeyDown(Key.SuperLeft, 7);
        Assert.True(controller.Super);
        controller.HandleKeyUp(Key.SuperLeft, 7);
        Assert.False(controller.Super);

        controller.HandleKeyDown(Key.SuperRight, 8);
        Assert.True(controller.Super);
        controller.HandleKeyUp(Key.SuperRight, 8);
        Assert.False(controller.Super);

        // Modifier presses should not trigger keyPressed, characterReceived, or activity
        Assert.Empty(keyEvents);
        Assert.Empty(charEvents);
        Assert.Equal(0, activityCount);
    }

    [Fact]
    public void NonModifierKeyDown_DispatchesImmediately_AndTriggersActivity()
    {
        var clock = new FakeClock();
        var keyEvents = new List<(Key Key, int Scancode)>();
        int activityCount = 0;

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            activity: () => activityCount++,
            clockMilliseconds: () => clock.CurrentTimeMs);

        controller.HandleKeyDown(Key.A, 30);

        Assert.Single(keyEvents);
        Assert.Equal((Key.A, 30), keyEvents[0]);
        Assert.Equal(1, activityCount);
    }

    [Fact]
    public void KeyRepeat_DoesNotRepeat_BeforeInitialDelay()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        controller.HandleKeyDown(Key.Up, 10);
        Assert.Single(keyEvents);

        // Tick before initial delay
        clock.Advance(399);
        controller.Tick();
        Assert.Single(keyEvents);
    }

    [Fact]
    public void KeyRepeat_RepeatsAtConfiguredInterval()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        controller.HandleKeyDown(Key.Left, 15);
        Assert.Single(keyEvents);

        // Reach initial delay threshold
        clock.Advance(400);
        controller.Tick();
        Assert.Equal(2, keyEvents.Count);

        // Advance less than repeat interval
        clock.Advance(20);
        controller.Tick();
        Assert.Equal(2, keyEvents.Count);

        // Complete repeat interval
        clock.Advance(13);
        controller.Tick();
        Assert.Equal(3, keyEvents.Count);

        // Another repeat interval
        clock.Advance(33);
        controller.Tick();
        Assert.Equal(4, keyEvents.Count);
    }

    [Fact]
    public void CharacterReceived_DispatchesImmediately_AndRepeatsStoredChar()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();
        var charEvents = new List<char>();
        int activityCount = 0;

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            characterReceived: c => charEvents.Add(c),
            activity: () => activityCount++,
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleKeyChar('a');

        Assert.Single(keyEvents);
        Assert.Single(charEvents);
        Assert.Equal('a', charEvents[0]);
        Assert.Equal(2, activityCount); // 1 for KeyDown + 1 for KeyChar

        // Advance to repeat threshold
        clock.Advance(400);
        controller.Tick();

        // Repeating should emit characterReceived, NOT keyPressed
        Assert.Single(keyEvents);
        Assert.Equal(2, charEvents.Count);
        Assert.Equal('a', charEvents[1]);
    }

    [Fact]
    public void CharacterReceived_SuppressedUnderCtrlOrAlt_AndSwitchesToKeyRepeat()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();
        var charEvents = new List<char>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            characterReceived: c => charEvents.Add(c),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        // 1. When Ctrl is down, HandleKeyChar should be ignored completely
        controller.HandleKeyDown(Key.ControlLeft, 1);
        controller.HandleKeyDown(Key.C, 20);
        controller.HandleKeyChar('c');

        Assert.Single(keyEvents);
        Assert.Empty(charEvents);

        // Repeating under Ctrl should repeat keyPressed, not char
        clock.Advance(400);
        controller.Tick();

        Assert.Equal(2, keyEvents.Count);
        Assert.Equal(Key.C, keyEvents[1].Key);
        Assert.Empty(charEvents);

        // Release Key.C and Ctrl
        controller.HandleKeyUp(Key.C, 20);
        controller.HandleKeyUp(Key.ControlLeft, 1);

        // 2. Test Alt suppresses characterReceived
        controller.HandleKeyDown(Key.AltLeft, 2);
        controller.HandleKeyDown(Key.X, 21);
        controller.HandleKeyChar('x');

        Assert.Equal(3, keyEvents.Count); // C initial, C repeat, X initial
        Assert.Empty(charEvents);

        clock.Advance(400);
        controller.Tick();
        Assert.Equal(4, keyEvents.Count);
        Assert.Equal(Key.X, keyEvents[3].Key);
        Assert.Empty(charEvents);
    }

    [Fact]
    public void DynamicModifierChange_SwitchesStoredCharToKeyRepeat()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();
        var charEvents = new List<char>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            characterReceived: c => charEvents.Add(c),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        // Start typing normal char
        controller.HandleKeyDown(Key.A, 30);
        controller.HandleKeyChar('a');
        Assert.Single(keyEvents);
        Assert.Single(charEvents);

        // Press Ctrl while key is held
        controller.HandleKeyDown(Key.ControlLeft, 1);

        // Tick on repeat: since Ctrl is now active, repeat should emit keyPressed instead of char
        clock.Advance(400);
        controller.Tick();

        Assert.Equal(2, keyEvents.Count);
        Assert.Equal(Key.A, keyEvents[1].Key);
        Assert.Single(charEvents);
    }

    [Fact]
    public void KeyUp_ClearsHeldKeyAndChar_StoppingRepeats()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();
        var charEvents = new List<char>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            characterReceived: c => charEvents.Add(c),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        controller.HandleKeyDown(Key.B, 31);
        controller.HandleKeyChar('b');
        Assert.Single(keyEvents);
        Assert.Single(charEvents);

        // Release the key
        controller.HandleKeyUp(Key.B, 31);

        // Advance time and tick: no repeat events should fire
        clock.Advance(1000);
        controller.Tick();

        Assert.Single(keyEvents);
        Assert.Single(charEvents);
    }

    [Fact]
    public void KeyUp_WithDifferentKey_DoesNotClearHeldKey()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var keyEvents = new List<(Key Key, int Scancode)>();

        var controller = new TerminalKeyboardController(
            keyPressed: (k, s) => keyEvents.Add((k, s)),
            clockMilliseconds: () => clock.CurrentTimeMs,
            initialDelayMs: 400,
            repeatIntervalMs: 33);

        controller.HandleKeyDown(Key.B, 31);
        Assert.Single(keyEvents);

        // Release a different key
        controller.HandleKeyUp(Key.C, 32);

        // Held key B should still repeat
        clock.Advance(400);
        controller.Tick();

        Assert.Equal(2, keyEvents.Count);
        Assert.Equal(Key.B, keyEvents[1].Key);
    }

    [Fact]
    public void NullCallbacks_AreSafeAndDoNotThrow()
    {
        var clock = new FakeClock { CurrentTimeMs = 1000 };
        var controller = new TerminalKeyboardController(clockMilliseconds: () => clock.CurrentTimeMs);

        // None of these should throw NullReferenceException
        controller.HandleKeyDown(Key.ControlLeft, 1);
        controller.HandleKeyDown(Key.A, 30);
        controller.HandleKeyChar('a');
        clock.Advance(400);
        controller.Tick();
        controller.HandleKeyUp(Key.A, 30);
        controller.HandleKeyUp(Key.ControlLeft, 1);
    }
    [Fact]
    public void SurrogatePair_IsDeliveredAsOneTextPayload()
    {
        var payloads = new List<string>();
        var controller = new TerminalKeyboardController(
            textReceived: payloads.Add);

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleKeyChar('\uD83D');
        Assert.Empty(payloads);

        controller.HandleKeyChar('\uDE00');

        Assert.Single(payloads);
        Assert.Equal("😀", payloads[0]);
    }
}
