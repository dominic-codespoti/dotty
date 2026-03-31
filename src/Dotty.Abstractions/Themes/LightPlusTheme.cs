namespace Dotty.Abstractions.Themes;

/// <summary>
/// VS Code Light+ theme - a clean, bright theme.
/// 
/// A clean light theme suitable for well-lit environments.
/// This is the default light theme in VS Code.
/// </summary>
public sealed class LightPlusTheme : ColorSchemeBase
{
    // VS Code Light+ color palette
    // Background: #FFFFFF -> 0xFFFFFFFF
    // Foreground: #000000 -> 0xFF000000
    
    // ANSI colors from VS Code light terminal
    // Black: #000000, Red: #CD3131, Green: #00BC00, Yellow: #949800
    // Blue: #0451A5, Magenta: #BC05BC, Cyan: #0598BC, White: #555555
    // Bright Black: #666666, Bright Red: #F14C4C, Bright Green: #16C60C
    // Bright Yellow: #B5BA00, Bright Blue: #0A6BC8, Bright Magenta: #BC05BC
    // Bright Cyan: #0598BC, Bright White: #A5A5A5

    public LightPlusTheme() : base(
        background: 0xFFFFFFFF,       // #FFFFFF
        foreground: 0xFF000000,       // #000000
        ansiBlack: 0xFF000000,        // #000000
        ansiRed: 0xFFCD3131,          // #CD3131
        ansiGreen: 0xFF00BC00,        // #00BC00
        ansiYellow: 0xFF949800,       // #949800
        ansiBlue: 0xFF0451A5,         // #0451A5
        ansiMagenta: 0xFFBC05BC,      // #BC05BC
        ansiCyan: 0xFF0598BC,         // #0598BC
        ansiWhite: 0xFF555555,        // #555555
        ansiBrightBlack: 0xFF666666,  // #666666
        ansiBrightRed: 0xFFF14C4C,    // #F14C4C
        ansiBrightGreen: 0xFF16C60C,  // #16C60C
        ansiBrightYellow: 0xFFB5BA00, // #B5BA00
        ansiBrightBlue: 0xFF0A6BC8,   // #0A6BC8
        ansiBrightMagenta: 0xFFBC05BC,// #BC05BC
        ansiBrightCyan: 0xFF0598BC,   // #0598BC
        ansiBrightWhite: 0xFFA5A5A5   // #A5A5A5
    )
    {
    }
}
