namespace Dotty.Abstractions.Themes;

/// <summary>
/// Catppuccin Mocha theme - soothing dark theme with pastel colors.
/// 
/// Catppuccin is a community-driven pastel theme that aims to be the middle ground
/// between low and high contrast themes. Mocha is the dark variant.
/// 
/// https://github.com/catppuccin/catppuccin
/// </summary>
public sealed class CatppuccinMochaTheme : ColorSchemeBase
{
    // Catppuccin Mocha color palette
    // Background: #1E1E2E -> 0xFF1E1E2E (base)
    // Foreground: #CDD6F4 -> 0xFFCDD6F4 (text)
    
    // ANSI colors mapped from Catppuccin palette
    // Using Catppuccin surface0-overlay2 for ANSI palette
    // Black: #45475A (surface1), Red: #F38BA8, Green: #A6E3A1
    // Yellow: #F9E2AF, Blue: #89B4FA, Magenta: #F5C2E7
    // Cyan: #94E2D5, White: #BAC2DE
    // Bright variants are slightly lighter versions

    public CatppuccinMochaTheme() : base(
        background: 0xFF1E1E2E,       // #1E1E2E (base)
        foreground: 0xFFCDD6F4,       // #CDD6F4 (text)
        ansiBlack: 0xFF45475A,        // #45475A (surface1)
        ansiRed: 0xFFF38BA8,          // #F38BA8 (red)
        ansiGreen: 0xFFA6E3A1,        // #A6E3A1 (green)
        ansiYellow: 0xFFF9E2AF,        // #F9E2AF (yellow)
        ansiBlue: 0xFF89B4FA,         // #89B4FA (blue)
        ansiMagenta: 0xFFF5C2E7,      // #F5C2E7 (pink/magenta)
        ansiCyan: 0xFF94E2D5,         // #94E2D5 (teal/cyan)
        ansiWhite: 0xFFBAC2DE,        // #BAC2DE (subtext1)
        ansiBrightBlack: 0xFF585B70,  // #585B70 (surface2)
        ansiBrightRed: 0xFFFFA1C1,    // lighter red
        ansiBrightGreen: 0xFFB9F0B4,  // lighter green
        ansiBrightYellow: 0xFFFFEFA1, // lighter yellow
        ansiBrightBlue: 0xFFA3C9FF,   // lighter blue
        ansiBrightMagenta: 0xFFFFDBF7,// lighter magenta
        ansiBrightCyan: 0xFFAAEFDE,   // lighter cyan
        ansiBrightWhite: 0xFFFFFFFF    // white
    )
    {
    }

    /// <summary>
    /// Window background opacity (0-100). Default is 100 (fully opaque).
    /// </summary>
    public override byte Opacity => 100;
}
