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

    private static string Normalize(string? value) =>
        value is null ? string.Empty : value.Trim().Replace('_', '-').ToLowerInvariant();
}
