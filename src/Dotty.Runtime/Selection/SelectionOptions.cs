using System;
using Dotty.Runtime.Config;

namespace Dotty.Runtime.Selection;

/// <summary>Modifier that turns a local drag into a rectangular (block) selection (<c>selection.rectangleModifier</c>).</summary>
public enum RectangleSelectionModifier
{
    None,
    Shift,
    Ctrl,
    Alt,
    Super,
}

/// <summary>Resolved <c>selection</c> configuration (see <see cref="SelectionOptions.Resolve"/>).</summary>
public readonly record struct SelectionSettings(
    string WordChars,
    bool ClearOnCopy,
    bool ClearOnTyping,
    RectangleSelectionModifier RectangleModifier)
{
    public static SelectionSettings Default { get; } = new(
        SelectionOptions.DefaultWordChars, ClearOnCopy: true, ClearOnTyping: true, RectangleSelectionModifier.Alt);
}

/// <summary>Lenient parsing of the <c>selection</c> config object; unknown values fall back to the defaults.</summary>
public static class SelectionOptions
{
    /// <summary>Punctuation kept inside a double-clicked word by default: path and URL friendly.</summary>
    public const string DefaultWordChars = "@-./_~?&=%+#";

    /// <summary><c>alt</c> (default), <c>ctrl</c> (or <c>control</c>), <c>shift</c>, <c>super</c> or <c>none</c>; case-insensitive, trimmed.</summary>
    public static RectangleSelectionModifier ParseRectangleModifier(string? value)
    {
        if (Is(value, "alt")) return RectangleSelectionModifier.Alt;
        if (Is(value, "ctrl") || Is(value, "control")) return RectangleSelectionModifier.Ctrl;
        if (Is(value, "shift")) return RectangleSelectionModifier.Shift;
        if (Is(value, "super")) return RectangleSelectionModifier.Super;
        if (Is(value, "none")) return RectangleSelectionModifier.None;
        return RectangleSelectionModifier.Alt;
    }

    /// <summary>Resolves the live settings; a null word-character string means the default set, an empty one means none.</summary>
    public static SelectionSettings Resolve(SelectionUserConfig? config)
    {
        if (config is null) return SelectionSettings.Default;
        return new SelectionSettings(
            config.WordChars ?? DefaultWordChars,
            config.ClearOnCopy,
            config.ClearOnTyping,
            ParseRectangleModifier(config.RectangleModifier));
    }

    private static bool Is(string? value, string expected) =>
        value is not null && value.AsSpan().Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
}
