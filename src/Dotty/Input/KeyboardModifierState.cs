using Dotty.Runtime.Input;
using Silk.NET.Input;

namespace Dotty.Silk.Input;

/// <summary>
/// Side-specific modifier keys tracked from physical key events, merged with the effective
/// modifier state that GLFW reports with every native key and character event. Headless so the
/// host and the key replay harness share one definition of "Ctrl/Shift/Alt/AltGr/Super held".
/// </summary>
public sealed class KeyboardModifierState
{
    private const int GlfwModShift = 0x1;
    private const int GlfwModControl = 0x2;
    private const int GlfwModAlt = 0x4;
    private const int GlfwModSuper = 0x8;
    private const int GlfwModCapsLock = 0x10;
    private const int GlfwModNumLock = 0x20;

    private bool _leftCtrl;
    private bool _rightCtrl;
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _leftSuper;
    private bool _rightSuper;
    private TerminalKeyModifiers _native;

    public bool LeftCtrl => _leftCtrl;
    public bool RightCtrl => _rightCtrl;
    public bool LeftShift => _leftShift;
    public bool RightShift => _rightShift;
    public bool LeftAlt => _leftAlt;
    public bool RightAlt => _rightAlt;
    public bool LeftSuper => _leftSuper;
    public bool RightSuper => _rightSuper;

    /// <summary>A physical Ctrl key is held (ignores the native modifier bits).</summary>
    public bool PhysicalCtrl => _leftCtrl || _rightCtrl;

    /// <summary>The modifier bits GLFW reported with the most recent native event, in Dotty's order.</summary>
    public TerminalKeyModifiers NativeModifiers => _native;

    /// <summary>Alt/Option roles (decision K1). The host refreshes it from <c>keyboard.*</c> before each native key event.</summary>
    public KeyboardPolicy Policy { get; set; } = KeyboardPolicy.ForCurrentPlatform(null, null);

    /// <summary>
    /// Ctrl is held. A native Ctrl bit with no Ctrl key event while an Alt key is held is the synthetic Ctrl that
    /// Windows adds to AltGr, and does not count.
    /// </summary>
    public bool Ctrl => PhysicalCtrl || ((_native & TerminalKeyModifiers.Control) != 0 && !_leftAlt && !_rightAlt);
    public bool Shift => _leftShift || _rightShift || (_native & TerminalKeyModifiers.Shift) != 0;

    /// <summary>A Meta-role Alt is held, or the native state reports Alt without a tracked Alt key.</summary>
    public bool Alt =>
        (_leftAlt && !Composes(Policy.LeftAlt)) ||
        (_rightAlt && !Composes(Policy.RightAlt)) ||
        (!_leftAlt && !_rightAlt && (_native & TerminalKeyModifiers.Alt) != 0);

    /// <summary>A Compose-role Alt (AltGr, Option) is held: characters it produces are text, not shortcuts.</summary>
    public bool AltGr => (_leftAlt && Composes(Policy.LeftAlt)) || (_rightAlt && Composes(Policy.RightAlt));

    public bool Super => _leftSuper || _rightSuper || (_native & TerminalKeyModifiers.Meta) != 0;

    /// <summary>
    /// The character delivered with a Meta chord is the key's layout character (Shift applied), so the chord can be
    /// sent as <c>ESC</c> + that character. False on macOS and when the layout consumed Alt as AltGr, where the
    /// delivered character is a composed one and the key name is used instead.
    /// </summary>
    public bool MetaUsesCommittedText => Policy.MetaUsesCommittedText && !AltConsumedByLayout;

    // AltGr / ISO Level 3: no Alt bit (X11/Wayland) or Ctrl+Alt without a physical Ctrl key (Windows).
    private bool AltConsumedByLayout =>
        (_native & TerminalKeyModifiers.Alt) == 0 ||
        ((_native & TerminalKeyModifiers.Control) != 0 && !PhysicalCtrl);

    private bool Composes(AltKeyRole role) => role switch
    {
        AltKeyRole.Meta => false,
        AltKeyRole.Compose => true,
        _ => AltConsumedByLayout,
    };

    public TerminalKeyModifiers LockModifiers => _native & (TerminalKeyModifiers.CapsLock | TerminalKeyModifiers.NumLock);

    /// <summary>Records a physical modifier key transition; non-modifier keys are ignored.</summary>
    public void Update(Key key, bool pressed)
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

    /// <summary>Stores the GLFW <c>mods</c> bit field delivered with a native key or character event.</summary>
    public void SetNativeGlfwModifiers(int glfwModifiers) => _native = FromGlfwModifiers(glfwModifiers);

    /// <summary>Converts GLFW modifier bits (shift 1, ctrl 2, alt 4, super 8, caps 16, num 32) to <see cref="TerminalKeyModifiers"/>.</summary>
    public static TerminalKeyModifiers FromGlfwModifiers(int glfwModifiers)
    {
        var result = TerminalKeyModifiers.None;
        if ((glfwModifiers & GlfwModShift) != 0) result |= TerminalKeyModifiers.Shift;
        if ((glfwModifiers & GlfwModControl) != 0) result |= TerminalKeyModifiers.Control;
        if ((glfwModifiers & GlfwModAlt) != 0) result |= TerminalKeyModifiers.Alt;
        if ((glfwModifiers & GlfwModSuper) != 0) result |= TerminalKeyModifiers.Meta;
        if ((glfwModifiers & GlfwModCapsLock) != 0) result |= TerminalKeyModifiers.CapsLock;
        if ((glfwModifiers & GlfwModNumLock) != 0) result |= TerminalKeyModifiers.NumLock;
        return result;
    }

    public void Reset()
    {
        _leftCtrl = false;
        _rightCtrl = false;
        _leftShift = false;
        _rightShift = false;
        _leftAlt = false;
        _rightAlt = false;
        _leftSuper = false;
        _rightSuper = false;
        _native = TerminalKeyModifiers.None;
    }
}
