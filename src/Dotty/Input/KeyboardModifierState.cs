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

    public bool Ctrl => _leftCtrl || _rightCtrl || (_native & TerminalKeyModifiers.Control) != 0;
    public bool Shift => _leftShift || _rightShift || (_native & TerminalKeyModifiers.Shift) != 0;
    public bool Alt => _leftAlt || _rightAlt || (_native & TerminalKeyModifiers.Alt) != 0;
    public bool AltGr => _rightAlt;
    public bool Super => _leftSuper || _rightSuper || (_native & TerminalKeyModifiers.Meta) != 0;
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
