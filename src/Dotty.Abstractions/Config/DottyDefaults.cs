using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.Abstractions.Config;

/// <summary>
/// Centralized default values for Dotty terminal emulator.
/// This class serves as the single source of truth for all default configuration values.
/// </summary>
public static class DottyDefaults
{
    // Font Settings
    // Default font.family per OS (decision F3): an ordered list that is also the fallback order. No Nerd
    // Font is assumed: box drawing, blocks and Powerline come from built-in sprites.
    public const string FontFamilyLinux = "monospace, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono, Noto Color Emoji";
    public const string FontFamilyMacOS = "Menlo, SF Mono, Monaco, Apple Color Emoji, Apple Symbols";
    public const string FontFamilyWindows = "Cascadia Mono, Consolas, Segoe UI Emoji, Segoe UI Symbol";
    public static readonly string FontFamily =
        OperatingSystem.IsMacOS() ? FontFamilyMacOS : OperatingSystem.IsWindows() ? FontFamilyWindows : FontFamilyLinux;
    public const string FontWeight = "regular";
    public const double FontSize = 15.0;
    public const double ContentPaddingLeft = 0.0;
    public const double ContentPaddingTop = 0.0;
    public const double ContentPaddingRight = 0.0;
    public const double ContentPaddingBottom = 0.0;

    // Terminal Settings
    public const int ScrollbackLines = 5000;
    public const long ScrollbackLimitBytes = 64L * 1024 * 1024;

    // Kitty graphics protocol (enabled stays false until the renderer lands)
    public const bool KittyGraphicsEnabled = false;
    public const long KittyImageStorageLimitBytes = 320L * 1024 * 1024;
    public const long KittyMaxPayloadBytes = 65L * 1024 * 1024;
    public const int KittyMaxImageDimension = 10000;
    public const int KittyMaxPlacementsPerScreen = 8192;
    /// <summary>Experimental: request ConPTY passthrough mode (Windows) so graphics APC reaches the terminal.</summary>
    public const bool GraphicsWindowsPassthrough = false;

    // UI Colors (ARGB format)
    public const uint SelectionColor = 0xA03385DB;
    public const uint TabBarBackgroundColor = 0xFF1A1A1A;

    /// <summary>
    /// Gets the content padding as a Thickness.
    /// </summary>
    public static Thickness ContentPadding => new Thickness(
        ContentPaddingLeft,
        ContentPaddingTop,
        ContentPaddingRight,
        ContentPaddingBottom
    );

    // Window Settings
    public const int WindowColumns = 80;
    public const int WindowRows = 24;
    public const int InitialColumns = WindowColumns;
    public const int InitialRows = WindowRows;
    public const bool StartFullscreen = false;
    public static readonly string WindowTitle = "Dotty";

    // Cursor Settings
    public const TerminalCursorShape CursorShape = TerminalCursorShape.Block;
    public const bool CursorBlink = true;
    public const int CursorBlinkIntervalMs = 500;
    public const uint CursorColor = 0xFFD4D4D4;  // Default to foreground color (light gray)
    public const bool CursorShowUnfocused = false;

    // Transparency Settings
    public const TransparencyLevel Transparency = TransparencyLevel.None;
    public const byte WindowOpacity = 100;

    // Theme Settings
    public static readonly IColorScheme DefaultColorScheme = BuiltInThemes.DarkPlus;
    public const string DefaultThemeName = "DarkPlus";

    /// <summary>
    /// Gets the initial font size, checking environment variable override first.
    /// </summary>
    public static double GetInitialFontSize()
    {
        var env = Environment.GetEnvironmentVariable("DOTTY_FONT_SIZE");
        if (!string.IsNullOrWhiteSpace(env) &&
            double.TryParse(env, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return FontSize;
    }
}
