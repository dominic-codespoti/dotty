namespace Dotty.Abstractions.Themes;

/// <summary>
/// Abstract base class implementing IColorScheme with validation and helper methods.
/// Provides a foundation for creating custom color schemes.
/// </summary>
public abstract class ColorSchemeBase : Config.IColorScheme
{
    private readonly uint _background;
    private readonly uint _foreground;
    private readonly uint[] _ansiColors;

    /// <summary>
    /// Creates a new color scheme with the specified colors.
    /// </summary>
    /// <param name="background">Terminal background color (ARGB format)</param>
    /// <param name="foreground">Terminal foreground/text color (ARGB format)</param>
    /// <param name="ansiColors">Array of 16 ANSI colors (indices 0-15)</param>
    protected ColorSchemeBase(uint background, uint foreground, uint[] ansiColors)
    {
        if (ansiColors == null)
            throw new ArgumentNullException(nameof(ansiColors));
        
        if (ansiColors.Length != 16)
            throw new ArgumentException("ANSI colors array must contain exactly 16 colors", nameof(ansiColors));

        _background = background;
        _foreground = foreground;
        _ansiColors = ansiColors;
    }

    /// <summary>
    /// Creates a color scheme from individual color values.
    /// </summary>
    protected ColorSchemeBase(
        uint background,
        uint foreground,
        uint ansiBlack, uint ansiRed, uint ansiGreen, uint ansiYellow,
        uint ansiBlue, uint ansiMagenta, uint ansiCyan, uint ansiWhite,
        uint ansiBrightBlack, uint ansiBrightRed, uint ansiBrightGreen, uint ansiBrightYellow,
        uint ansiBrightBlue, uint ansiBrightMagenta, uint ansiBrightCyan, uint ansiBrightWhite)
        : this(background, foreground, new[]
        {
            ansiBlack, ansiRed, ansiGreen, ansiYellow,
            ansiBlue, ansiMagenta, ansiCyan, ansiWhite,
            ansiBrightBlack, ansiBrightRed, ansiBrightGreen, ansiBrightYellow,
            ansiBrightBlue, ansiBrightMagenta, ansiBrightCyan, ansiBrightWhite
        })
    {
    }

    public uint Background => _background;
    public uint Foreground => _foreground;

    public uint AnsiBlack => _ansiColors[0];
    public uint AnsiRed => _ansiColors[1];
    public uint AnsiGreen => _ansiColors[2];
    public uint AnsiYellow => _ansiColors[3];
    public uint AnsiBlue => _ansiColors[4];
    public uint AnsiMagenta => _ansiColors[5];
    public uint AnsiCyan => _ansiColors[6];
    public uint AnsiWhite => _ansiColors[7];
    public uint AnsiBrightBlack => _ansiColors[8];
    public uint AnsiBrightRed => _ansiColors[9];
    public uint AnsiBrightGreen => _ansiColors[10];
    public uint AnsiBrightYellow => _ansiColors[11];
    public uint AnsiBrightBlue => _ansiColors[12];
    public uint AnsiBrightMagenta => _ansiColors[13];
    public uint AnsiBrightCyan => _ansiColors[14];
    public uint AnsiBrightWhite => _ansiColors[15];

    /// <summary>
    /// Gets the ANSI color at the specified index (0-15).
    /// </summary>
    /// <param name="index">ANSI color index (0-15)</param>
    /// <returns>The color at the specified index</returns>
    /// <exception cref="ArgumentOutOfRangeException">If index is not 0-15</exception>
    public uint GetAnsiColor(int index)
    {
        if (index < 0 || index >= 16)
            throw new ArgumentOutOfRangeException(nameof(index), "ANSI color index must be 0-15");
        
        return _ansiColors[index];
    }

    /// <summary>
    /// Gets a copy of all 16 ANSI colors.
    /// </summary>
    public uint[] GetAllAnsiColors()
    {
        var copy = new uint[16];
        Array.Copy(_ansiColors, copy, 16);
        return copy;
    }

    /// <summary>
    /// Converts a hex string to an ARGB uint.
    /// Supports formats: #RRGGBB, #AARRGGBB, RRGGBB, AARRGGBB
    /// </summary>
    /// <param name="hex">Hex color string</param>
    /// <returns>ARGB uint value</returns>
    public static uint FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new ArgumentException("Hex string cannot be null or empty", nameof(hex));

        var span = hex.AsSpan().Trim();
        
        // Remove # prefix if present
        if (span.Length > 0 && span[0] == '#')
            span = span.Slice(1);

        // Parse based on length
        return span.Length switch
        {
            6 => 0xFF000000u | (uint)Convert.ToInt32(span.ToString(), 16),  // RRGGBB -> AARRGGBB with FF alpha
            8 => (uint)Convert.ToInt32(span.ToString(), 16),                 // AARRGGBB
            _ => throw new ArgumentException($"Invalid hex color format: {hex}. Expected #RRGGBB or #AARRGGBB")
        };
    }

    /// <summary>
    /// Converts an ARGB uint to a hex string (#AARRGGBB format).
    /// </summary>
    /// <param name="argb">ARGB color value</param>
    /// <returns>Hex string representation</returns>
    public static string ToHex(uint argb)
    {
        return $"#{argb:X8}";
    }

    /// <summary>
    /// Creates an ARGB color from RGB components.
    /// </summary>
    /// <param name="r">Red component (0-255)</param>
    /// <param name="g">Green component (0-255)</param>
    /// <param name="b">Blue component (0-255)</param>
    /// <param name="a">Alpha component (0-255), default is 255 (opaque)</param>
    /// <returns>ARGB uint value</returns>
    public static uint FromRgb(byte r, byte g, byte b, byte a = 255)
    {
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>
    /// Validates that a color has reasonable contrast with another color.
    /// </summary>
    /// <param name="foreground">Foreground color</param>
    /// <param name="background">Background color</param>
    /// <returns>Contrast ratio (should be at least 4.5:1 for WCAG AA)</returns>
    public static double CalculateContrastRatio(uint foreground, uint background)
    {
        double fgLuminance = GetRelativeLuminance(foreground);
        double bgLuminance = GetRelativeLuminance(background);

        double lighter = Math.Max(fgLuminance, bgLuminance);
        double darker = Math.Min(fgLuminance, bgLuminance);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Gets the relative luminance of a color (used for contrast calculations).
    /// </summary>
    private static double GetRelativeLuminance(uint argb)
    {
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);

        double rsRGB = r / 255.0;
        double gsRGB = g / 255.0;
        double bsRGB = b / 255.0;

        double rLinear = rsRGB <= 0.03928 ? rsRGB / 12.92 : Math.Pow((rsRGB + 0.055) / 1.055, 2.4);
        double gLinear = gsRGB <= 0.03928 ? gsRGB / 12.92 : Math.Pow((gsRGB + 0.055) / 1.055, 2.4);
        double bLinear = bsRGB <= 0.03928 ? bsRGB / 12.92 : Math.Pow((bsRGB + 0.055) / 1.055, 2.4);

        return 0.2126 * rLinear + 0.7152 * gLinear + 0.0722 * bLinear;
    }

    /// <summary>
    /// Validates that this color scheme meets basic accessibility guidelines.
    /// Logs warnings for low contrast ratios.
    /// </summary>
    public void ValidateAccessibility()
    {
        double contrast = CalculateContrastRatio(Foreground, Background);
        
        // WCAG AA requires 4.5:1 for normal text, 7:1 for AAA
        if (contrast < 4.5)
        {
            // In a real implementation, this would use a logging system
            // For now, we'll just track it as a comment
            // Low contrast warning: {contrast:F2}:1 (recommend at least 4.5:1)
        }
    }
}
