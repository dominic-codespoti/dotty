using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Input;
using Silk.NET.Input;

namespace Dotty.App.Tests;

/// <summary>Windowing backends whose GLFW key/char delivery rules the harness reproduces.</summary>
public enum KeyPlatform
{
    X11,
    Wayland,
    Win32,
    Cocoa,
}

/// <summary>
/// Key-event replay harness (plan-12 WI-0). Drives the real <see cref="TerminalKeyboardController"/> and
/// <see cref="TerminalKeyboardDispatcher"/> with the event sequences GLFW produces for a layout fixture on a
/// platform: key press → character → key release, repeat → character, with the native <c>mods</c> bit
/// field each event carries. The host's Ctrl/Shift/Alt/AltGr/Super/lock state is read from the controller's
/// <see cref="KeyboardModifierState"/>, exactly as <c>DottyWindowHost</c> does, never from settable flags.
/// </summary>
/// <remarks>
/// Platform rules mirrored from the vendored GLFW (<c>vendor/glfw/source/src</c>):
/// <list type="bullet">
/// <item>Characters below U+0020 and in U+007F–U+009F are never delivered (<c>input.c</c>, <c>_glfwInputChar</c>).</item>
/// <item>X11/Wayland: a character is "plain" unless Ctrl or Alt (Mod1) is in the state; AltGr is ISO_Level3_Shift
/// (Mod5) and sets no GLFW modifier bit (<c>x11_window.c</c> translateState, <c>wl_window.c</c>).</item>
/// <item>Win32: AltGr reports Ctrl+Alt and GLFW drops the synthetic Left Ctrl key event; Alt chords arrive as
/// <c>WM_SYSCHAR</c>, which is not plain (<c>win32_window.c</c>).</item>
/// <item>Cocoa: Option composes characters; a character is plain unless Command (Super) is held (<c>cocoa_window.m</c>).</item>
/// </list>
/// </remarks>
internal sealed class KeyPipelineHarness : IDisposable
{
    private const int GlfwModShift = 0x1;
    private const int GlfwModControl = 0x2;
    private const int GlfwModAlt = 0x4;
    private const int GlfwModSuper = 0x8;
    private const int GlfwModCapsLock = 0x10;
    private const int GlfwModNumLock = 0x20;

    private readonly HashSet<Key> _held = new();
    private readonly HarnessHost _host;

    public KeyPipelineHarness(LayoutFixture layout, KeyPlatform platform, int rows = 24, int columns = 80)
    {
        Layout = layout;
        Platform = platform;
        Controller = new TerminalKeyboardController(
            keyEventReceived: (key, scancode, primary, type, text) => Dispatcher.HandleKeyEvent(key, scancode, primary, type, text),
            textReceived: text => Dispatcher.HandleCommittedText(text),
            primaryCodepointProvider: (key, _) => layout.PrimaryCodepoint(key));
        _host = new HarnessHost(Controller.Modifiers)
        {
            ActiveTab = new TerminalTab(ptyFactory: SilentPty.Create, rows: rows, columns: columns)
        };
        Dispatcher = new TerminalKeyboardDispatcher(_host);
    }

    public LayoutFixture Layout { get; }
    public KeyPlatform Platform { get; }
    public TerminalKeyboardController Controller { get; }
    public TerminalKeyboardDispatcher Dispatcher { get; }
    public TerminalTab Tab => _host.ActiveTab!;
    public LeafPane Pane => Tab.ActivePane;
    public int CopyCount => _host.CopyCount;

    /// <summary>Num Lock toggle state reported in the native modifier bits.</summary>
    public bool NumLock { get; set; }

    /// <summary>Caps Lock toggle state reported in the native modifier bits.</summary>
    public bool CapsLock { get; set; }

    /// <summary>Everything written to the PTY, decoded as UTF-8.</summary>
    public string Output => Encoding.UTF8.GetString(_host.InputBytes.ToArray());

    public void ClearOutput() => _host.InputBytes.Clear();

    /// <summary>Feeds terminal output (e.g. <c>CSI &gt; 1 u</c>) to the active session's parser.</summary>
    public void Feed(string sequence) => Tab.Session.Parser.Feed(Encoding.UTF8.GetBytes(sequence));

    public void Press(Key key)
    {
        _held.Add(key);
        int mods = NativeModifiers();
        Controller.Modifiers.SetNativeGlfwModifiers(mods);
        Controller.HandleKeyDown(key, Scancode(key));
        DeliverText(key, mods);
    }

    public void Repeat(Key key)
    {
        int mods = NativeModifiers();
        Controller.Modifiers.SetNativeGlfwModifiers(mods);
        Controller.HandleKeyRepeat(key, Scancode(key));
        DeliverText(key, mods);
    }

    public void Release(Key key)
    {
        _held.Remove(key);
        Controller.Modifiers.SetNativeGlfwModifiers(NativeModifiers());
        Controller.HandleKeyUp(key, Scancode(key));
    }

    /// <summary>Presses and releases <paramref name="key"/>.</summary>
    public void Tap(Key key)
    {
        Press(key);
        Release(key);
    }

    /// <summary>Presses <paramref name="modifiers"/> in order, taps <paramref name="key"/>, releases the modifiers in reverse.</summary>
    public void Chord(Key key, params Key[] modifiers)
    {
        foreach (Key modifier in modifiers)
            Press(modifier);
        Tap(key);
        for (int i = modifiers.Length - 1; i >= 0; i--)
            Release(modifiers[i]);
    }

    public void Type(params Key[] keys)
    {
        foreach (Key key in keys)
            Tap(key);
    }

    private static int Scancode(Key key) => (int)key;

    // Win32 AltGr is a synthetic Left Ctrl plus Right Alt; GLFW discards the synthetic Left Ctrl key event,
    // so a Right Alt press on an AltGr layout carries Ctrl+Alt in its mods without any Ctrl key event.
    private bool RightAltIsLevel3 => Layout.HasAltGr && Platform != KeyPlatform.Cocoa;

    private bool Held(Key left, Key right) => _held.Contains(left) || _held.Contains(right);

    private int NativeModifiers()
    {
        int mods = 0;
        if (Held(Key.ShiftLeft, Key.ShiftRight)) mods |= GlfwModShift;
        if (Held(Key.ControlLeft, Key.ControlRight)) mods |= GlfwModControl;
        if (Held(Key.SuperLeft, Key.SuperRight)) mods |= GlfwModSuper;
        if (_held.Contains(Key.AltLeft)) mods |= GlfwModAlt;
        if (_held.Contains(Key.AltRight))
        {
            if (!RightAltIsLevel3)
                mods |= GlfwModAlt;
            else if (Platform == KeyPlatform.Win32)
                mods |= GlfwModControl | GlfwModAlt;
        }
        if (CapsLock) mods |= GlfwModCapsLock;
        if (NumLock) mods |= GlfwModNumLock;
        return mods;
    }

    private void DeliverText(Key key, int mods)
    {
        string? text = ProduceText(key, mods);
        if (string.IsNullOrEmpty(text))
            return;
        bool plain = IsPlain(mods);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int codepoint = char.ConvertToUtf32(text, i);
            if (codepoint < 32 || codepoint is > 126 and < 160)
                continue;
            if (!plain)
                continue;
            Controller.HandleUnicodeScalar((uint)codepoint);
        }
    }

    private bool IsPlain(int mods) => Platform switch
    {
        KeyPlatform.Cocoa => (mods & GlfwModSuper) == 0,
        // WM_SYSCHAR: Alt without Ctrl (Ctrl+Alt is AltGr and produces WM_CHAR).
        KeyPlatform.Win32 => (mods & GlfwModAlt) == 0 || (mods & GlfwModControl) != 0,
        _ => (mods & (GlfwModControl | GlfwModAlt)) == 0,
    };

    private string? ProduceText(Key key, int mods)
    {
        if (IsModifierKey(key))
            return null;
        KeyChars chars = Layout[key];
        bool shift = (mods & GlfwModShift) != 0;
        if (key is >= Key.Keypad0 and <= Key.KeypadEqual)
            return NumLock ? chars.Plain : null;

        bool physicalCtrl = Held(Key.ControlLeft, Key.ControlRight);
        bool altGr = _held.Contains(Key.AltRight) && RightAltIsLevel3;
        bool option = Platform == KeyPlatform.Cocoa && Held(Key.AltLeft, Key.AltRight);
        string? text = shift ? chars.Shifted : chars.Plain;

        if (Held(Key.SuperLeft, Key.SuperRight))
            return Platform == KeyPlatform.Win32 ? null : text;
        if (physicalCtrl)
        {
            if (Platform is KeyPlatform.Win32 or KeyPlatform.Cocoa)
                return null;
            // XLookupString applies the Ctrl transform to @..~ (letters become C0, which GLFW drops);
            // other keysyms (e.g. Cyrillic) are returned unchanged.
            if (text is { Length: 1 } && text[0] is >= '@' and <= '~')
                return ((char)(text[0] & 0x1f)).ToString();
            return text;
        }
        if (altGr)
            return chars.AltGr;
        if (option)
            return shift ? chars.OptionShifted : chars.Option;
        return text;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.ShiftLeft or Key.ShiftRight or Key.ControlLeft or Key.ControlRight or
        Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight or
        Key.CapsLock or Key.NumLock or Key.ScrollLock or Key.Menu;

    public void Dispose()
    {
        Dispatcher.Dispose();
        _host.Dispose();
    }

    private sealed class HarnessHost : ITerminalKeyboardHost, IDisposable
    {
        private readonly KeyboardModifierState _modifiers;

        public HarnessHost(KeyboardModifierState modifiers)
        {
            _modifiers = modifiers;
            LuaHost = new LuaScriptHost(new FakeLuaHostServices(TabManager), TabManager);
            LuaHost.Evaluate(new DottyUserConfig(), null);
        }

        public TerminalTabManager TabManager { get; } = new(SilentPty.Create);
        public TerminalTab? ActiveTab { get; set; }
        public LuaScriptHost LuaHost { get; }
        public KeybindingManager Keybindings { get; } = new();
        public ContextMenuModel? ActiveContextMenu { get; set; }
        public int Rows => 24;
        public bool Ctrl => _modifiers.Ctrl;
        public bool Shift => _modifiers.Shift;
        public bool Alt => _modifiers.Alt;
        public bool AltGr => _modifiers.AltGr;
        public bool Super => _modifiers.Super;
        public TerminalKeyModifiers LockModifiers => _modifiers.LockModifiers;
        public List<byte> InputBytes { get; } = new();
        public int CopyCount { get; private set; }

        public void CopySelection() => CopyCount++;
        public void PasteClipboard() { }
        public void ToggleFullscreen() { }
        public void ZoomIn() { }
        public void ZoomOut() { }
        public void ResetZoom() { }
        public void DuplicateTab(TerminalTab activeTab) { }
        public void CloseOtherTabs(TerminalTab activeTab) { }
        public void Quit() { }
        public void CreateTab(TerminalTab activeTab) { }
        public void ClearTerminal(TerminalTab activeTab) { }

        public void WriteInput(TerminalTab activeTab, ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
                InputBytes.Add(bytes[i]);
        }

        public void Dispose()
        {
            ActiveTab?.Dispose();
            LuaHost.Dispose();
            TabManager.Dispose();
        }
    }
}
