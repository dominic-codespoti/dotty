using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Silk.Input;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Replays recorded GLFW event sequences through the real controller and dispatcher
/// (<see cref="KeyPipelineHarness"/>). Defect ids refer to plan-12 §2.
/// </summary>
public sealed class KeyPipelineHarnessTests
{
    public static TheoryData<string> LayoutNames()
    {
        var data = new TheoryData<string>();
        foreach (var layout in KeyboardLayoutFixtures.All())
            data.Add(layout.Name);
        return data;
    }

    private static LayoutFixture Layout(string name)
    {
        foreach (var layout in KeyboardLayoutFixtures.All())
        {
            if (layout.Name == name)
                return layout;
        }
        throw new System.ArgumentException(name, nameof(name));
    }

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void PlainKeysTypeTheLayoutCharacter(string layoutName)
    {
        var layout = Layout(layoutName);
        var platform = layout == KeyboardLayoutFixtures.MacAbc ? KeyPlatform.Cocoa : KeyPlatform.X11;
        using var harness = new KeyPipelineHarness(layout, platform);

        harness.Type(Key.Q, Key.W, Key.Y, Key.Comma);
        harness.Chord(Key.A, Key.ShiftLeft);

        Assert.Equal(
            layout[Key.Q].Plain + layout[Key.W].Plain + layout[Key.Y].Plain + layout[Key.Comma].Plain + layout[Key.A].Shifted,
            harness.Output);
    }

    [Theory]
    [InlineData(0x00, TerminalKeyModifiers.None)]
    [InlineData(0x01, TerminalKeyModifiers.Shift)]
    [InlineData(0x02, TerminalKeyModifiers.Control)]
    [InlineData(0x04, TerminalKeyModifiers.Alt)]
    [InlineData(0x08, TerminalKeyModifiers.Meta)]
    [InlineData(0x10, TerminalKeyModifiers.CapsLock)]
    [InlineData(0x20, TerminalKeyModifiers.NumLock)]
    [InlineData(0x06, TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt)]
    [InlineData(0x3F, TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt |
        TerminalKeyModifiers.Meta | TerminalKeyModifiers.CapsLock | TerminalKeyModifiers.NumLock)]
    public void GlfwModifierBitsMapToTerminalModifiers(int glfwMods, TerminalKeyModifiers expected)
    {
        Assert.Equal(expected, KeyboardModifierState.FromGlfwModifiers(glfwMods));
    }

    // D1: the AltGr press was consumed and the composed character that follows it dropped.
    [Theory]
    [InlineData(KeyPlatform.X11)]
    [InlineData(KeyPlatform.Wayland)]
    [InlineData(KeyPlatform.Win32)]
    public void QwertzAltGrQ_TypesAtSign(KeyPlatform platform)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, platform);

        harness.Chord(Key.Q, Key.AltRight);

        Assert.Equal("@", harness.Output);
    }

    [Fact]
    public void QwertzAltGrQ_KittyReportAllKeysCarriesComposedText()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, KeyPlatform.X11);
        harness.Feed("\x1b[>24u");

        harness.Chord(Key.Q, Key.AltRight);

        Assert.Equal("\x1b[57449;1u\x1b[113;1;64u", harness.Output);
    }

    [Fact]
    public void QwertzAltGrText_GoesIntoTheSearchQuery()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, KeyPlatform.X11);
        harness.Press(Key.ControlLeft);
        harness.Chord(Key.F, Key.ShiftLeft);
        harness.Release(Key.ControlLeft);

        harness.Chord(Key.Q, Key.AltRight);
        harness.Chord(Key.Number8, Key.AltRight);

        Assert.Equal("@[", harness.Dispatcher.SearchQuery);
        Assert.Equal(string.Empty, harness.Output);
    }

    // D1, US half: Right Alt is a plain Alt on layouts without AltGr and must act as Meta.
    [Theory]
    [InlineData(KeyPlatform.X11)]
    [InlineData(KeyPlatform.Wayland)]
    [InlineData(KeyPlatform.Win32)]
    public void UsRightAltA_SendsEscPrefix(KeyPlatform platform)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, platform);

        harness.Chord(Key.A, Key.AltRight);

        Assert.Equal("\u001ba", harness.Output);
    }

    // D3: legacy Alt chords were built from the unshifted key name and lost Shift.
    [Theory]
    [InlineData(KeyPlatform.X11, Key.Comma, "\x1b<")]
    [InlineData(KeyPlatform.X11, Key.A, "\u001bA")]
    [InlineData(KeyPlatform.X11, Key.Number5, "\x1b%")]
    [InlineData(KeyPlatform.Wayland, Key.Period, "\x1b>")]
    [InlineData(KeyPlatform.Win32, Key.Comma, "\x1b<")]
    [InlineData(KeyPlatform.Win32, Key.Slash, "\x1b?")]
    public void AltShiftChord_KeepsShift(KeyPlatform platform, Key key, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, platform);

        harness.Chord(key, Key.AltLeft, Key.ShiftLeft);

        Assert.Equal(expected, harness.Output);
    }

    // D4: Ctrl+letter on a non-Latin layout wrote nothing.
    [Theory]
    [InlineData(KeyPlatform.X11, Key.C, "\x03")]
    [InlineData(KeyPlatform.Wayland, Key.D, "\x04")]
    [InlineData(KeyPlatform.Win32, Key.Z, "\x1a")]
    public void RussianCtrlLetter_SendsControlCodeOfThePhysicalKey(KeyPlatform platform, Key key, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Russian, platform);

        harness.Chord(key, Key.ControlLeft);

        Assert.Equal(expected, harness.Output);
    }

    [Fact]
    public void RussianCtrlShiftC_TriggersCopyBinding()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Russian, KeyPlatform.X11);

        harness.Chord(Key.C, Key.ControlLeft, Key.ShiftLeft);

        Assert.Equal(1, harness.CopyCount);
        Assert.Equal(string.Empty, harness.Output);
    }

    // D2: Option on macOS composes characters by default.
    [Fact]
    public void MacGermanOptionL_ComposesAtSign()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, KeyPlatform.Cocoa);

        harness.Chord(Key.L, Key.AltLeft);

        Assert.Equal("@", harness.Output);
    }

    // D26: Super chords are shortcuts, not text.
    [Fact]
    public void SuperC_IsNotTyped()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);

        harness.Chord(Key.C, Key.SuperLeft);

        Assert.Equal(string.Empty, harness.Output);
    }

    [Theory]
    [InlineData(null, null, false, AltKeyRole.Meta, AltKeyRole.ComposeOrMeta, true)]
    [InlineData("auto", "auto", false, AltKeyRole.Meta, AltKeyRole.ComposeOrMeta, true)]
    [InlineData(null, null, true, AltKeyRole.Compose, AltKeyRole.Compose, false)]
    [InlineData("meta", "auto", true, AltKeyRole.Meta, AltKeyRole.Compose, false)]
    [InlineData("COMPOSE", " Meta ", false, AltKeyRole.Compose, AltKeyRole.Meta, true)]
    [InlineData("bogus", "", false, AltKeyRole.Meta, AltKeyRole.ComposeOrMeta, true)]
    [InlineData("compose", "compose", true, AltKeyRole.Compose, AltKeyRole.Compose, false)]
    public void KeyboardPolicyResolvesConfigPerPlatform(string? left, string? right, bool macOS,
        AltKeyRole expectedLeft, AltKeyRole expectedRight, bool metaUsesCommittedText)
    {
        Assert.Equal(new KeyboardPolicy(expectedLeft, expectedRight, metaUsesCommittedText, HonorsNumLock: !macOS),
            KeyboardPolicy.Resolve(left, right, macOS));
    }

    [Theory]
    [InlineData(KeyPlatform.X11)]
    [InlineData(KeyPlatform.Win32)]
    public void QwertzRightAltAsMeta_SendsEscAndKeyName(KeyPlatform platform)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, platform) { RightAlt = "meta" };

        harness.Chord(Key.Q, Key.AltRight);

        Assert.Equal("\x1bq", harness.Output);
    }

    [Theory]
    [InlineData(false, "\x1bl")]
    [InlineData(true, "\x1bL")]
    public void MacOptionAsMeta_SendsEscAndKeyName(bool shift, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, KeyPlatform.Cocoa) { LeftAlt = "meta" };

        if (shift)
            harness.Chord(Key.L, Key.AltLeft, Key.ShiftLeft);
        else
            harness.Chord(Key.L, Key.AltLeft);

        Assert.Equal(expected, harness.Output);
    }

    [Fact]
    public void MacRightOptionComposesByDefault()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.MacAbc, KeyPlatform.Cocoa);

        harness.Chord(Key.C, Key.AltRight);

        Assert.Equal("ç", harness.Output);
    }

    [Fact]
    public void LinuxLeftAltAsCompose_TypesTheCharacter()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11) { LeftAlt = "compose" };

        harness.Chord(Key.A, Key.AltLeft);

        Assert.Equal("a", harness.Output);
    }

    [Fact]
    public void MetaChordRepeat_SendsEscAndCharacterPerRepeat()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);

        harness.Press(Key.AltLeft);
        harness.Press(Key.B);
        harness.Repeat(Key.B);
        harness.Release(Key.B);
        harness.Release(Key.AltLeft);

        Assert.Equal("\u001bb\u001bb", harness.Output);
    }

    [Fact]
    public void AltDigitBinding_MatchesMetaSideOnly()
    {
        // alt+2 is the built-in SwitchTab2 binding: it consumes the Meta chord, while AltGr+2 on a German
        // layout is Compose and types its character.
        using var meta = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        meta.Chord(Key.Number2, Key.AltLeft);
        Assert.Equal(string.Empty, meta.Output);

        using var compose = new KeyPipelineHarness(KeyboardLayoutFixtures.QwertzAltGr, KeyPlatform.X11);
        compose.Chord(Key.Number2, Key.AltRight);
        Assert.Equal("²", compose.Output);
    }

    // D5: a bare modifier press snapped the scrolled pane to the bottom and Shift cleared the selection.
    [Theory]
    [InlineData(Key.ControlLeft)]
    [InlineData(Key.AltLeft)]
    [InlineData(Key.SuperLeft)]
    [InlineData(Key.ShiftLeft)]
    [InlineData(Key.CapsLock)]
    public void BareModifierPress_KeepsScrollOffsetAndSelection(Key modifier)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        var pane = harness.Pane;
        CreateScrollback(pane, 10);
        pane.ScrollUp(3, pane.Session.Adapter.Buffer.ScrollbackCount);
        pane.Selection.StartSelection(0, 0);

        harness.Press(modifier);

        Assert.Equal(3, pane.ScrollOffset);
        Assert.True(pane.Selection.HasSelection);
        harness.Release(modifier);
        Assert.Equal(3, pane.ScrollOffset);
        Assert.True(pane.Selection.HasSelection);
    }

    [Fact]
    public void TextKeyPress_SnapsToBottomAndClearsSelection()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        var pane = harness.Pane;
        CreateScrollback(pane, 10);
        pane.ScrollUp(3, pane.Session.Adapter.Buffer.ScrollbackCount);
        pane.Selection.StartSelection(0, 0);

        harness.Tap(Key.X);

        Assert.Equal(0, pane.ScrollOffset);
        Assert.False(pane.Selection.HasSelection);
        Assert.Equal("x", harness.Output);
    }

    [Fact]
    public void ShiftPressBeforeCtrlShiftC_KeepsTheSelectionForCopy()
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        harness.Pane.Selection.StartSelection(0, 0);

        harness.Chord(Key.C, Key.ShiftLeft, Key.ControlLeft);

        Assert.Equal(1, harness.CopyCount);
        Assert.True(harness.Pane.Selection.HasSelection);
    }

    // D7: Home/End ignored DECCKM.
    [Theory]
    [InlineData(Key.Home, "\x1bOH")]
    [InlineData(Key.End, "\x1bOF")]
    public void HomeEndInApplicationCursorMode_UseSs3(Key key, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11);
        harness.Feed("\x1b[?1h");

        harness.Tap(key);

        Assert.Equal(expected, harness.Output);
    }

    // D8: with Num Lock off the keypad digits are navigation keys; GLFW still names them "0".."9".
    [Theory]
    [InlineData(Key.Keypad8, false, "\x1b[A")]
    [InlineData(Key.Keypad2, false, "\x1b[B")]
    [InlineData(Key.Keypad6, false, "\x1b[C")]
    [InlineData(Key.Keypad4, false, "\x1b[D")]
    [InlineData(Key.Keypad7, false, "\x1b[H")]
    [InlineData(Key.Keypad1, false, "\x1b[F")]
    [InlineData(Key.Keypad5, false, "\x1b[E")]
    [InlineData(Key.Keypad9, false, "\x1b[5~")]
    [InlineData(Key.Keypad3, false, "\x1b[6~")]
    [InlineData(Key.Keypad0, false, "\x1b[2~")]
    [InlineData(Key.KeypadDecimal, false, "\x1b[3~")]
    [InlineData(Key.Keypad8, true, "\x1bOA")]
    public void KeypadWithNumLockOff_SendsNavigationKeys(Key key, bool applicationCursor, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11) { NumLock = false };
        if (applicationCursor)
            harness.Feed("\x1b[?1h");

        harness.Tap(key);

        Assert.Equal(expected, harness.Output);
    }

    [Theory]
    [InlineData(KeyPlatform.X11, true, "8")]
    [InlineData(KeyPlatform.Win32, true, "8")]
    [InlineData(KeyPlatform.Cocoa, false, "8")]
    public void KeypadDigit_TypesOnceWhenNumLockIsOnOrThePlatformHasNone(KeyPlatform platform, bool numLock, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, platform) { NumLock = numLock };

        harness.Tap(Key.Keypad8);

        Assert.Equal(expected, harness.Output);
    }

    // D8: keypad application mode must win over the key's name and the delivered digit.
    [Theory]
    [InlineData(Key.Keypad8, "\x1bOx")]
    [InlineData(Key.KeypadAdd, "\x1bOm")]
    [InlineData(Key.KeypadDecimal, "\x1bOn")]
    public void KeypadInApplicationMode_SendsSs3OnlyOnce(Key key, string expected)
    {
        using var harness = new KeyPipelineHarness(KeyboardLayoutFixtures.Qwerty, KeyPlatform.X11) { NumLock = true };
        harness.Feed("\x1b=");

        harness.Tap(key);

        Assert.Equal(expected, harness.Output);
    }

    private static void CreateScrollback(LeafPane pane, int lines)
    {
        var buffer = pane.Session.Adapter.Buffer;
        buffer.SetCursor(buffer.Rows - 1, 0);
        for (int i = 0; i < lines; i++)
            buffer.LineFeed();
    }
}
