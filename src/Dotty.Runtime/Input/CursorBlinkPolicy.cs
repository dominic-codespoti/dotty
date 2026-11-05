namespace Dotty.Runtime.Input;

/// <summary>
/// Pure cursor-blink decision: the application's DECSCUSR blink when it has set a style, otherwise
/// <c>cursor.blink</c>; an operating-system preference that disables blinking (when the platform exposes one)
/// keeps the cursor steady either way.
/// </summary>
public static class CursorBlinkPolicy
{
    /// <param name="appStyleIsDefault">True until DECSCUSR sets a style (and after DECSCUSR 0, DECSTR, RIS).</param>
    /// <param name="appBlinking">Blink bit of the application's DECSCUSR style.</param>
    /// <param name="configBlink"><c>cursor.blink</c>.</param>
    /// <param name="systemAllowsBlink">OS preference: false when blinking is disabled, null when unknown.</param>
    public static bool Blinks(bool appStyleIsDefault, bool appBlinking, bool configBlink, bool? systemAllowsBlink) =>
        systemAllowsBlink != false && (appStyleIsDefault ? configBlink : appBlinking);
}
