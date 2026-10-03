using System.Collections.Generic;
using Dotty.Runtime.Input;
using Dotty.Silk.Input;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalKeyboardControllerTests
{
    private readonly record struct Event(Key Key, int Scancode, int PrimaryCodepoint, TerminalKeyEventType Type, string Text);

    [Fact]
    public void PhysicalKeyReportsPrimaryCodepointAndPressReleasePhases()
    {
        var events = new List<Event>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())),
            primaryCodepointProvider: static (key, scancode) => key == Key.A ? 'a' : 0);

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleKeyUp(Key.A, 30);

        Assert.Equal(new[]
        {
            new Event(Key.A, 30, 'a', TerminalKeyEventType.Press, string.Empty),
            new Event(Key.A, 30, 'a', TerminalKeyEventType.Release, string.Empty),
        }, events);
    }

    [Fact]
    public void NativeUnicodeScalarIsAssociatedWithHeldKeyAndSupplementaryScalarIsPreserved()
    {
        var events = new List<Event>();
        var textEvents = new List<string>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())),
            textReceived: text => textEvents.Add(text.ToString()));

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleUnicodeScalar(0x1F642);
        controller.HandleKeyUp(Key.A, 30);
        controller.HandleUnicodeScalar('x');

        Assert.Equal(new[]
        {
            new Event(Key.A, 30, 0, TerminalKeyEventType.Press, string.Empty),
            new Event(Key.A, 30, 0, TerminalKeyEventType.Press, "🙂"),
            new Event(Key.A, 30, 0, TerminalKeyEventType.Release, "🙂"),
        }, events);
        Assert.Equal(new[] { "x" }, textEvents);
    }

    [Fact]
    public void NativeRepeatPhaseIsPreservedOnAssociatedUnicodeScalar()
    {
        var events = new List<Event>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())),
            primaryCodepointProvider: static (key, scancode) => key == Key.A ? 'a' : 0);

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleUnicodeScalar('a');
        controller.HandleKeyRepeat(Key.A, 30);
        controller.HandleUnicodeScalar('a');
        controller.HandleKeyUp(Key.A, 30);

        Assert.Equal(new[]
        {
            TerminalKeyEventType.Press,
            TerminalKeyEventType.Press,
            TerminalKeyEventType.Repeat,
            TerminalKeyEventType.Repeat,
            TerminalKeyEventType.Release,
        }, events.ConvertAll(static item => item.Type));
        Assert.Equal("a", events[3].Text);
    }

    [Fact]
    public void MultipleHeldKeysKeepTheirOwnTextAndReleaseIdentity()
    {
        var events = new List<Event>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())));

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleUnicodeScalar('a');
        controller.HandleKeyDown(Key.B, 48);
        controller.HandleUnicodeScalar('b');
        controller.HandleKeyUp(Key.A, 30);
        controller.HandleKeyUp(Key.B, 48);

        Assert.Equal(new[]
        {
            new Event(Key.A, 30, 0, TerminalKeyEventType.Press, string.Empty),
            new Event(Key.A, 30, 0, TerminalKeyEventType.Press, "a"),
            new Event(Key.B, 48, 0, TerminalKeyEventType.Press, string.Empty),
            new Event(Key.B, 48, 0, TerminalKeyEventType.Press, "b"),
            new Event(Key.A, 30, 0, TerminalKeyEventType.Release, "a"),
            new Event(Key.B, 48, 0, TerminalKeyEventType.Release, "b"),
        }, events);
    }

    [Fact]
    public void NativeRepeatSelectsTheRepeatedHeldKeyForFollowingCommittedText()
    {
        var events = new List<Event>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())));

        controller.HandleKeyDown(Key.A, 30);
        controller.HandleUnicodeScalar('a');
        controller.HandleKeyDown(Key.B, 48);
        controller.HandleUnicodeScalar('b');
        controller.HandleKeyRepeat(Key.A, 30);
        controller.HandleUnicodeScalar('a');

        Assert.Equal(new Event(Key.A, 30, 0, TerminalKeyEventType.Repeat, "a"), events[^1]);
    }

    [Fact]
    public void ModifierTrackingEmitsPhysicalKeyPhasesAndResetClearsState()
    {
        var events = new List<Event>();
        var controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) =>
                events.Add(new Event(key, scancode, primary, type, text.ToString())));

        controller.HandleKeyDown(Key.ControlLeft, 1);
        controller.HandleKeyDown(Key.ShiftRight, 2);
        Assert.True(controller.Ctrl);
        Assert.True(controller.Shift);
        Assert.Equal(new[] { Key.ControlLeft, Key.ShiftRight }, events.ConvertAll(static item => item.Key));

        controller.HandleKeyUp(Key.ControlLeft, 1);
        controller.HandleKeyUp(Key.ShiftRight, 2);
        Assert.False(controller.Ctrl);
        Assert.False(controller.Shift);
        Assert.Equal(new[]
        {
            TerminalKeyEventType.Press,
            TerminalKeyEventType.Press,
            TerminalKeyEventType.Release,
            TerminalKeyEventType.Release,
        }, events.ConvertAll(static item => item.Type));

        controller.HandleKeyDown(Key.ControlLeft, 1);
        controller.ResetState();
        Assert.False(controller.Ctrl);
    }
}
