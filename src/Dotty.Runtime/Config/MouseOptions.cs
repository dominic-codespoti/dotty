using System;

namespace Dotty.Runtime.Config;

/// <summary>Where a finished local selection is copied automatically.</summary>
public enum CopyOnSelectMode
{
    None,
    Primary,
    Clipboard,
    Both,
}

/// <summary>What a middle click does in a pane that is not reporting mouse events.</summary>
public enum MiddleClickAction
{
    PrimaryPaste,
    ClipboardPaste,
    Ignore,
}

/// <summary>Pure parsers for the <c>mouse</c> config strings. They never throw; unknown values yield the default.</summary>
public static class MouseOptions
{
    public const CopyOnSelectMode DefaultCopyOnSelect = CopyOnSelectMode.None;
    public const MiddleClickAction DefaultMiddleClick = MiddleClickAction.ClipboardPaste;
    public const double DefaultScrollMultiplier = 3.0;
    public const double MinScrollMultiplier = 0.01;
    public const double MaxScrollMultiplier = 100.0;
    public const MouseReportBypassModifier DefaultReportBypassModifier = MouseReportBypassModifier.Shift;

    /// <summary>
    /// Case-insensitive, trimmed. Also accepts the legacy Ghostty boolean spellings given as strings (the setting is a
    /// JSON string, so an unquoted <c>true</c> is a type mismatch): <c>"true"</c>/<c>"on"</c> mean <c>primary</c> on Linux and
    /// <c>clipboard</c> elsewhere (<paramref name="isLinux"/>), <c>"false"</c>/<c>"off"</c> mean <c>none</c>.
    /// </summary>
    public static CopyOnSelectMode ParseCopyOnSelect(string? value, bool isLinux)
    {
        string v = Normalize(value);
        return v switch
        {
            "none" or "false" or "off" => CopyOnSelectMode.None,
            "primary" => CopyOnSelectMode.Primary,
            "clipboard" => CopyOnSelectMode.Clipboard,
            "both" => CopyOnSelectMode.Both,
            "true" or "on" => isLinux ? CopyOnSelectMode.Primary : CopyOnSelectMode.Clipboard,
            _ => DefaultCopyOnSelect,
        };
    }

    public static CopyOnSelectMode ParseCopyOnSelect(string? value) =>
        ParseCopyOnSelect(value, OperatingSystem.IsLinux());

    /// <summary>Case-insensitive, trimmed; <c>-</c> and <c>_</c> are interchangeable.</summary>
    public static MiddleClickAction ParseMiddleClick(string? value) => Normalize(value) switch
    {
        "primary-paste" => MiddleClickAction.PrimaryPaste,
        "clipboard-paste" => MiddleClickAction.ClipboardPaste,
        "ignore" or "none" => MiddleClickAction.Ignore,
        _ => DefaultMiddleClick,
    };

    /// <summary>Lines per wheel notch, clamped to 0.01 through 100; NaN and infinities mean the default (3).</summary>
    public static double ClampScrollMultiplier(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinScrollMultiplier, MaxScrollMultiplier) : DefaultScrollMultiplier;

    /// <summary><c>auto</c> (default) translates the wheel per mode 1007; <c>off</c> never does. Case-insensitive, trimmed, allocation-free.</summary>
    public static bool ParseAlternateScroll(string? value) => !Is(value, "off");

    /// <summary><c>shift</c> (default), <c>ctrl</c> (or <c>control</c>), <c>alt</c>, <c>super</c> or <c>none</c>. Case-insensitive, trimmed, allocation-free.</summary>
    public static MouseReportBypassModifier ParseReportBypassModifier(string? value)
    {
        if (Is(value, "shift")) return MouseReportBypassModifier.Shift;
        if (Is(value, "ctrl") || Is(value, "control")) return MouseReportBypassModifier.Ctrl;
        if (Is(value, "alt")) return MouseReportBypassModifier.Alt;
        if (Is(value, "super")) return MouseReportBypassModifier.Super;
        if (Is(value, "none")) return MouseReportBypassModifier.None;
        return DefaultReportBypassModifier;
    }

    /// <summary>Resolves the wheel and reporting settings; called per pointer event, so it does not allocate.</summary>
    public static MouseInputSettings ResolveInput(MouseUserConfig config) => new(
        ClampScrollMultiplier(config.ScrollMultiplier),
        ParseAlternateScroll(config.AlternateScroll),
        ParseReportBypassModifier(config.ReportBypassModifier));

    private static bool Is(string? value, string expected) =>
        value is not null && value.AsSpan().Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        value is null ? string.Empty : value.Trim().Replace('_', '-').ToLowerInvariant();
}
