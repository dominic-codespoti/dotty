namespace Dotty.Abstractions.Config;

/// <summary>
/// Color scheme configuration for the terminal.
/// All colors are in ARGB format (0xAARRGGBB) or RGB format (0xRRGGBB).
/// </summary>
public interface IColorScheme
{
    /// <summary>
    /// Terminal background color (ARGB format).
    /// </summary>
    uint Background { get; }

    /// <summary>
    /// Terminal foreground/text color (ARGB format).
    /// </summary>
    uint Foreground { get; }

    /// <summary>
    /// ANSI Black (color 0) - ARGB format.
    /// </summary>
    uint AnsiBlack { get; }

    /// <summary>
    /// ANSI Red (color 1) - ARGB format.
    /// </summary>
    uint AnsiRed { get; }

    /// <summary>
    /// ANSI Green (color 2) - ARGB format.
    /// </summary>
    uint AnsiGreen { get; }

    /// <summary>
    /// ANSI Yellow (color 3) - ARGB format.
    /// </summary>
    uint AnsiYellow { get; }

    /// <summary>
    /// ANSI Blue (color 4) - ARGB format.
    /// </summary>
    uint AnsiBlue { get; }

    /// <summary>
    /// ANSI Magenta (color 5) - ARGB format.
    /// </summary>
    uint AnsiMagenta { get; }

    /// <summary>
    /// ANSI Cyan (color 6) - ARGB format.
    /// </summary>
    uint AnsiCyan { get; }

    /// <summary>
    /// ANSI White (color 7) - ARGB format.
    /// </summary>
    uint AnsiWhite { get; }

    /// <summary>
    /// ANSI Bright Black (color 8) - ARGB format.
    /// </summary>
    uint AnsiBrightBlack { get; }

    /// <summary>
    /// ANSI Bright Red (color 9) - ARGB format.
    /// </summary>
    uint AnsiBrightRed { get; }

    /// <summary>
    /// ANSI Bright Green (color 10) - ARGB format.
    /// </summary>
    uint AnsiBrightGreen { get; }

    /// <summary>
    /// ANSI Bright Yellow (color 11) - ARGB format.
    /// </summary>
    uint AnsiBrightYellow { get; }

    /// <summary>
    /// ANSI Bright Blue (color 12) - ARGB format.
    /// </summary>
    uint AnsiBrightBlue { get; }

    /// <summary>
    /// ANSI Bright Magenta (color 13) - ARGB format.
    /// </summary>
    uint AnsiBrightMagenta { get; }

    /// <summary>
    /// ANSI Bright Cyan (color 14) - ARGB format.
    /// </summary>
    uint AnsiBrightCyan { get; }

    /// <summary>
    /// ANSI Bright White (color 15) - ARGB format.
    /// </summary>
    uint AnsiBrightWhite { get; }

    /// <summary>
    /// Window background opacity (0-100, where 100 is fully opaque, 0 is fully transparent)
    /// </summary>
    byte Opacity { get; }
}
