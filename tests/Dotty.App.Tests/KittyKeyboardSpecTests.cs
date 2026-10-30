using System;
using System.Text;
using Dotty.Runtime.Input;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Kitty keyboard protocol conformance (decision K6, plan-12 WI-4). Each row cites the section of the published
/// specification (https://sw.kovidgoyal.net/kitty/keyboard-protocol/) whose text it asserts.
/// </summary>
public sealed class KittyKeyboardSpecTests
{
    private const TerminalKeyEventType Press = TerminalKeyEventType.Press;
    private const TerminalKeyEventType Release = TerminalKeyEventType.Release;

    public static TheoryData<string, int, TerminalKey, TerminalKeyModifiers, TerminalKeyEventType, int, int, string> SpecTable() => new()
    {
        // K6(a) "Disambiguate escape codes": "The only exceptions are the Enter, Tab and Backspace keys which
        // still generate the same bytes as in legacy mode".
        { "Disambiguate: Enter stays legacy", 1, TerminalKey.Enter, TerminalKeyModifiers.None, Press, 0, 0, "\r" },
        { "Disambiguate: Tab stays legacy", 1, TerminalKey.Tab, TerminalKeyModifiers.None, Press, 0, 0, "\t" },
        { "Disambiguate: Backspace stays legacy", 1, TerminalKey.Backspace, TerminalKeyModifiers.None, Press, 0, 0, "\u007f" },
        { "Disambiguate: Enter stays legacy with Num Lock", 1, TerminalKey.Enter, TerminalKeyModifiers.NumLock, Press, 0, 0, "\r" },
        { "Disambiguate: modified Enter is CSI u", 1, TerminalKey.Enter, TerminalKeyModifiers.Control, Press, 0, 0, "\u001b[13;5u" },
        { "Disambiguate: Shift+Tab is CSI u (fixterms errata: shift+tab is not a separate key)", 1, TerminalKey.Tab, TerminalKeyModifiers.Shift, Press, 0, 0, "\u001b[9;2u" },
        // K6(a) "Report event types": "The Enter, Tab and Backspace keys will not have release events unless
        // Report all keys as escape codes is also set".
        { "Event types: no Enter release", 3, TerminalKey.Enter, TerminalKeyModifiers.None, Release, 0, 0, "" },
        { "Event types: no Backspace release", 3, TerminalKey.Backspace, TerminalKeyModifiers.None, Release, 0, 0, "" },
        { "Event types: no Tab release even when modified", 3, TerminalKey.Tab, TerminalKeyModifiers.Shift, Release, 0, 0, "" },
        { "Report all keys: Enter release", 11, TerminalKey.Enter, TerminalKeyModifiers.None, Release, 0, 0, "\u001b[13;1:3u" },
        { "Report all keys: Enter is CSI u", 8, TerminalKey.Enter, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[13;1u" },
        // K6(b) "Report all keys as escape codes": "Additionally, with this mode, events for pressing modifier
        // keys are reported."
        { "Disambiguate: no Shift key event", 1, TerminalKey.ShiftLeft, TerminalKeyModifiers.Shift, Press, 0, 0, "" },
        { "Event types: no Ctrl key event", 3, TerminalKey.ControlRight, TerminalKeyModifiers.Control, Release, 0, 0, "" },
        { "Disambiguate: no Caps Lock key event", 1, TerminalKey.CapsLock, TerminalKeyModifiers.None, Press, 0, 0, "" },
        { "Disambiguate: no Num Lock key event", 1, TerminalKey.NumLock, TerminalKeyModifiers.NumLock, Press, 0, 0, "" },
        { "Report all keys: Shift key event", 8, TerminalKey.ShiftLeft, TerminalKeyModifiers.Shift, Press, 0, 0, "\u001b[57441;2u" },
        // K6(c) "Disambiguate escape codes": "Note that the Lock modifiers are not reported for text producing
        // keys, to keep them usable in legacy programs."
        { "Disambiguate: a with Num Lock is text", 1, TerminalKey.A, TerminalKeyModifiers.NumLock, Press, 'a', 0, "" },
        { "Disambiguate: a with Caps and Num Lock is text", 1, TerminalKey.A, TerminalKeyModifiers.CapsLock | TerminalKeyModifiers.NumLock, Press, 'a', 0, "" },
        { "Disambiguate: Ctrl+a drops the lock bits", 1, TerminalKey.A, TerminalKeyModifiers.Control | TerminalKeyModifiers.NumLock, Press, 'a', 0, "\u001b[97;5u" },
        { "Report all keys: lock bits reported", 8, TerminalKey.A, TerminalKeyModifiers.NumLock, Press, 'a', 0, "\u001b[97;129u" },
        { "Disambiguate: Esc is not a text key and keeps the lock bits", 1, TerminalKey.Escape, TerminalKeyModifiers.NumLock, Press, 0, 0, "\u001b[27;129u" },
        // K6(d) "Key codes": "Note that the shifted key must be present only if shift is also present in the
        // modifiers."
        { "Alternate keys: Ctrl+Shift+a has the shifted key", 5, TerminalKey.A, TerminalKeyModifiers.Control | TerminalKeyModifiers.Shift, Press, 'a', 'A', "\u001b[97:65;6u" },
        { "Alternate keys: Alt+Shift+, has the shifted key", 5, TerminalKey.Comma, TerminalKeyModifiers.Alt | TerminalKeyModifiers.Shift, Press, ',', '<', "\u001b[44:60;4u" },
        { "Alternate keys: no shifted key without Shift", 5, TerminalKey.A, TerminalKeyModifiers.Control, Press, 'a', 'A', "\u001b[97;5u" },
        { "Alternate keys: Shift+a under report-all", 12, TerminalKey.A, TerminalKeyModifiers.Shift, Press, 'a', 'A', "\u001b[97:65;2u" },
        // Beyond K6, "Functional key definitions": keys with legacy forms use CSI 1;m letter / CSI number;m ~
        // ("The escape codes above of the form CSI 1 letter will omit the 1 if there are no modifiers").
        { "Functional: Up", 1, TerminalKey.Up, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[A" },
        { "Functional: Ctrl+Up", 1, TerminalKey.Up, TerminalKeyModifiers.Control, Press, 0, 0, "\u001b[1;5A" },
        { "Functional: Up keeps the lock bits (not a text key)", 1, TerminalKey.Up, TerminalKeyModifiers.NumLock, Press, 0, 0, "\u001b[1;129A" },
        { "Functional: Insert", 1, TerminalKey.Insert, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[2~" },
        { "Functional: Shift+Delete", 1, TerminalKey.Delete, TerminalKeyModifiers.Shift, Press, 0, 0, "\u001b[3;2~" },
        { "Functional: Page Down", 8, TerminalKey.PageDown, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[6~" },
        { "Functional: Home release", 3, TerminalKey.Home, TerminalKeyModifiers.None, Release, 0, 0, "\u001b[1;1:3H" },
        { "Functional: Shift+End", 1, TerminalKey.End, TerminalKeyModifiers.Shift, Press, 0, 0, "\u001b[1;2F" },
        { "Functional: F1", 1, TerminalKey.F1, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[P" },
        { "Functional: F3 is 13 ~ (CSI R removed)", 1, TerminalKey.F3, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[13~" },
        { "Functional: Alt+F5", 1, TerminalKey.F5, TerminalKeyModifiers.Alt, Press, 0, 0, "\u001b[15;3~" },
        { "Functional: F12", 8, TerminalKey.F12, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[24~" },
        { "Functional: KP_BEGIN", 1, TerminalKey.KeypadBegin, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[E" },
        { "Functional: F13", 1, TerminalKey.F13, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[57376;1u" },
        { "Functional: KP_LEFT", 1, TerminalKey.KeypadLeft, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[57417;1u" },
        { "Disambiguate: Esc", 1, TerminalKey.Escape, TerminalKeyModifiers.None, Press, 0, 0, "\u001b[27;1u" },
        { "Disambiguate: keypad digit is text", 1, TerminalKey.Keypad5, TerminalKeyModifiers.NumLock, Press, 0, 0, "" },
        { "Disambiguate: Shift+Space is text", 1, TerminalKey.Space, TerminalKeyModifiers.Shift, Press, 0, 0, "" },
    };

    [Theory]
    [MemberData(nameof(SpecTable))]
    public void EncoderFollowsTheSpecification(string rule, int flags, TerminalKey key, TerminalKeyModifiers modifiers,
        TerminalKeyEventType eventType, int primaryCodepoint, int shiftedCodepoint, string expected)
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[64];

        int length = encoder.EncodeKeyEvent(key, modifiers, primaryCodepoint, eventType, ReadOnlySpan<char>.Empty,
            buffer, flags, shiftedCodepoint: shiftedCodepoint);

        Assert.True(expected == Encoding.ASCII.GetString(buffer[..length]), rule);
    }

    // Golden byte sessions replayed through the real controller and dispatcher on X11 with Num Lock on:
    // a, Enter, Up, Ctrl+C (press and release each).
    [Theory]
    [InlineData("\u001b[>1u", "a\r\u001b[1;129A\u001b[99;5u")]
    [InlineData("\u001b[>3u", "a\r\u001b[1;129:1A\u001b[1;129:3A\u001b[99;5:1u\u001b[99;5:3u")]
    [InlineData("\u001b[>11u",
        "\u001b[97;129:1u\u001b[97;129:3u\u001b[13;129:1u\u001b[13;129:3u\u001b[1;129:1A\u001b[1;129:3A" +
        "\u001b[57442;133:1u\u001b[99;133:1u\u001b[99;133:3u\u001b[57442;129:3u")]
    [InlineData("\u001b[>31u",
        "\u001b[97;129:1;97u\u001b[97;129:3u\u001b[13;129:1u\u001b[13;129:3u\u001b[1;129:1A\u001b[1;129:3A" +
        "\u001b[57442;133:1u\u001b[99;133:1u\u001b[99;133:3u\u001b[57442;129:3u")]
    public void GoldenSessionWithNumLockOn(string push, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11) { NumLock = true };
        harness.Feed(push);

        harness.Tap(Key.A);
        harness.Tap(Key.Enter);
        harness.Tap(Key.Up);
        harness.Chord(Key.C, Key.ControlLeft);

        Assert.Equal(expected, harness.Output);
    }

    [Theory]
    [InlineData(Key.Comma, "\u001b[44:60;4u")]
    [InlineData(Key.A, "\u001b[97:65;4u")]
    public void AltShiftChord_ReportsTheShiftedKeyFromTheLayoutCharacter(Key key, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        harness.Feed("\u001b[>5u");

        harness.Chord(key, Key.AltLeft, Key.ShiftLeft);

        Assert.Equal(expected, harness.Output);
    }

    [Fact]
    public void CtrlShiftLetter_ReportsTheUpperCaseShiftedKey()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        harness.Feed("\u001b[>5u");

        // ctrl+shift+e is not a built-in binding.
        harness.Chord(Key.E, Key.ControlLeft, Key.ShiftLeft);

        Assert.Equal("\u001b[101:69;6u", harness.Output);
    }
}
