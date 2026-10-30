namespace Dotty.Runtime.Config;

/// <summary>Modifier that, while held, keeps pointer input local instead of reporting it to a mouse-tracking application.</summary>
public enum MouseReportBypassModifier
{
    None,
    Shift,
    Ctrl,
    Alt,
    Super,
}

/// <summary>
/// Resolved wheel and reporting settings from the <c>mouse</c> config object (see <see cref="MouseOptions.ResolveInput"/>).
/// <paramref name="ScrollMultiplier"/> is lines per wheel notch for local scrolling and alternate scroll;
/// <paramref name="AlternateScroll"/> false disables the mode-1007 wheel-to-cursor-keys translation.
/// </summary>
public readonly record struct MouseInputSettings(
    double ScrollMultiplier,
    bool AlternateScroll,
    MouseReportBypassModifier ReportBypassModifier)
{
    public static MouseInputSettings Default { get; } =
        new(MouseOptions.DefaultScrollMultiplier, true, MouseOptions.DefaultReportBypassModifier);
}
