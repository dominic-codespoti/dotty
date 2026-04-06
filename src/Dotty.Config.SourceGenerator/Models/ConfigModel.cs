namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record containing all configuration properties extracted from IDottyConfig implementations.
/// </summary>
public record ConfigModel
{
    // Font Settings
    public string FontFamily { get; init; } = DottyDefaults.FontFamily;
    public double FontSize { get; init; } = DottyDefaults.FontSize;
    public double CellPadding { get; init; } = 0.0;
    public ThicknessModel ContentPadding { get; init; } = new(0, 0, 0, 0);

    // Terminal Settings
    public int ScrollbackLines { get; init; } = DottyDefaults.ScrollbackLines;
    public int InactiveTabDestroyDelayMs { get; init; } = DottyDefaults.InactiveTabDestroyDelayMs;

    // UI Colors (ARGB format)
    public uint SelectionColor { get; init; } = DottyDefaults.SelectionColor;
    public uint TabBarBackgroundColor { get; init; } = DottyDefaults.TabBarBackgroundColor;

    // Window Settings
    public WindowDimensionsModel InitialDimensions { get; init; } = new(80, 24, "Dotty", false);

    // Cursor Settings
    public CursorModel Cursor { get; init; } = new();

    // Window Opacity & Transparency
    public byte WindowOpacity { get; init; } = DottyDefaults.WindowOpacity;
    public string Transparency { get; init; } = DottyDefaults.Transparency.ToString();

    // Theme
    public ThemeModel Theme { get; init; } = ThemeModel.DarkPlus;

    /// <summary>
    /// Default configuration model with all values from DottyDefaults.
    /// </summary>
    public static ConfigModel Default => new();
}

/// <summary>
/// Default values from DottyDefaults for use in the source generator.
/// </summary>
internal static class DottyDefaults
{
    public const string FontFamily = "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Cascadia Code, Consolas, Liberation Mono, Noto Sans Mono, monospace, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols";
    public const double FontSize = 15.0;
    public const double CellPadding = 0.0;
    public const int ScrollbackLines = 5000;
    public const int InactiveTabDestroyDelayMs = 5000;
    public const uint SelectionColor = 0xA03385DB;
    public const uint TabBarBackgroundColor = 0xFF1A1A1A;
    public const int InitialColumns = 80;
    public const int InitialRows = 24;
    public const string WindowTitle = "Dotty";
    public const bool StartFullscreen = false;
    public const string CursorShape = "Block";
    public const bool CursorBlink = true;
    public const int CursorBlinkIntervalMs = 500;
    public const uint CursorColor = 0xFFD4D4D4;
    public const bool CursorShowUnfocused = false;
    public const byte WindowOpacity = 100;
    public const string Transparency = "None";
    public const string DefaultThemeName = "DarkPlus";
}
