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
    public const string FontFamily = "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Cascadia Code, Consolas, Liberation Mono, Noto Sans Mono, monospace, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols";
    public const double FontSize = 15.0;
    public const double ContentPaddingLeft = 0.0;
    public const double ContentPaddingTop = 0.0;
    public const double ContentPaddingRight = 0.0;
    public const double ContentPaddingBottom = 0.0;

    // Terminal Settings
    public const int ScrollbackLines = 5000;
    public const int InactiveTabDestroyDelayMs = 5000;

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
    public const CursorShape CursorShape = Config.CursorShape.Block;
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

    /// <summary>
    /// Gets the default window dimensions.
    /// </summary>
    public static IWindowDimensions GetDefaultWindowDimensions()
    {
        return new DefaultWindowDimensions(
            InitialColumns,
            InitialRows,
            null,
            null,
            StartFullscreen,
            WindowTitle
        );
    }

    /// <summary>
    /// Gets the default cursor settings.
    /// </summary>
    public static ICursorSettings GetDefaultCursorSettings()
    {
        return new DefaultCursorSettings(
            CursorShape,
            CursorBlink,
            CursorBlinkIntervalMs,
            null,  // Use foreground color
            CursorShowUnfocused
        );
    }
}

/// <summary>
/// Default window dimensions implementation.
/// </summary>
public record DefaultWindowDimensions(
    int Columns,
    int Rows,
    int? WidthPixels,
    int? HeightPixels,
    bool StartFullscreen,
    string? Title
) : IWindowDimensions;

/// <summary>
/// Default cursor settings implementation.
/// </summary>
public record DefaultCursorSettings(
    CursorShape Shape,
    bool Blink,
    int BlinkIntervalMs,
    uint? Color,
    bool ShowUnfocused
) : ICursorSettings;
