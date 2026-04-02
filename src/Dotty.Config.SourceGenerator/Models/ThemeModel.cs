namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record representing a theme with all color properties.
/// </summary>
public record ThemeModel
{
    public string CanonicalName { get; init; } = "DarkPlus";
    public uint Background { get; init; } = 0xFF1E1E1E;
    public uint Foreground { get; init; } = 0xFFD4D4D4;
    public byte Opacity { get; init; } = 100;
    public uint[] AnsiColors { get; init; } = DefaultAnsiColors;

    private static readonly uint[] DefaultAnsiColors = new uint[]
    {
        0xFF000000,  // Black (30)
        0xFFCD3131,  // Red (31)
        0xFF0DBC79,  // Green (32)
        0xFFE5E510,  // Yellow (33)
        0xFF2472C8,  // Blue (34)
        0xFFBC3FBC,  // Magenta (35)
        0xFF11A8CD,  // Cyan (36)
        0xFFE5E5E5,  // White (37)
        0xFF666666,  // Bright Black (90)
        0xFFF14C4C,  // Bright Red (91)
        0xFF23D18B,  // Bright Green (92)
        0xFFF5F543,  // Bright Yellow (93)
        0xFF3B8EEA,  // Bright Blue (94)
        0xFFD670D6,  // Bright Magenta (95)
        0xFF29B8DB,  // Bright Cyan (96)
        0xFFFFFFFF,  // Bright White (97)
    };

    /// <summary>
    /// DarkPlus theme (default).
    /// </summary>
    public static ThemeModel DarkPlus => new()
    {
        CanonicalName = "DarkPlus",
        Background = 0xFF1E1E1E,
        Foreground = 0xFFD4D4D4,
        Opacity = 100,
        AnsiColors = DefaultAnsiColors
    };
}
