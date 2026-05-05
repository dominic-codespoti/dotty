using System;
using Dotty.Abstractions.Config;

namespace Dotty.App.Services;

public class RuntimeSettingsData
{
    // Font
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public double? CellPadding { get; set; }

    // Content padding
    public double? ContentPaddingLeft { get; set; }
    public double? ContentPaddingTop { get; set; }
    public double? ContentPaddingRight { get; set; }
    public double? ContentPaddingBottom { get; set; }

    // Colors (ARGB hex strings like "#1a1b26")
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    public string? SelectionColor { get; set; }
    public string? TabBarBackgroundColor { get; set; }
    public string? Theme { get; set; }

    // Cursor
    public string? CursorShape { get; set; }
    public bool? CursorBlink { get; set; }
    public double? CursorBlinkIntervalMs { get; set; }

    // Terminal
    public int? ScrollbackLines { get; set; }
    public int? InactiveTabDestroyDelayMs { get; set; }

    // Window
    public byte? WindowOpacity { get; set; }
    public string? Transparency { get; set; }

}

public static class RuntimeSettings
{
    private static RuntimeSettingsData s_current = new();
    public static RuntimeSettingsData Current => s_current;

    public static event EventHandler? Changed;

    internal static void Apply(RuntimeSettingsData data)
    {
        s_current = data;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string GetFontFamily() => Current.FontFamily ?? global::Dotty.Generated.Config.FontFamily;
    public static double GetFontSize() => Current.FontSize ?? global::Dotty.Generated.Config.FontSize;
    public static double GetCellPadding() => Current.CellPadding ?? global::Dotty.Generated.Config.CellPadding;
    public static double GetCursorBlinkIntervalMs() => Current.CursorBlinkIntervalMs ?? global::Dotty.Generated.Config.CursorBlinkIntervalMs;
    public static byte GetWindowOpacity() => Current.WindowOpacity ?? global::Dotty.Generated.Config.WindowOpacity;
    public static int GetScrollbackLines() => Current.ScrollbackLines ?? global::Dotty.Generated.Config.ScrollbackLines;
    public static int GetInactiveTabDestroyDelayMs() => Current.InactiveTabDestroyDelayMs ?? global::Dotty.Generated.Config.InactiveTabDestroyDelayMs;

    public static TransparencyLevel GetTransparency()
    {
        if (Current.Transparency != null && Enum.TryParse<TransparencyLevel>(Current.Transparency, out var t))
            return t;
        return global::Dotty.Generated.Config.Transparency;
    }
}
