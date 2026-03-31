namespace Dotty.Abstractions.Themes;

/// <summary>
/// Solarized Light theme - carefully selected low-contrast colors.
/// 
/// Designed to reduce eye strain with carefully selected colors
/// that work well together. Features a distinctive beige background.
/// 
/// https://ethanschoonover.com/solarized/
/// </summary>
public sealed class SolarizedLightTheme : ColorSchemeBase
{
    // Solarized Light color palette
    // Background: #FDF6E3 -> 0xFFFDF6E3 (base3)
    // Foreground: #657B83 -> 0xFF657B83 (base00)
    
    // ANSI colors from Solarized palette
    // Black: #073642 (base02), Red: #DC322F, Green: #859900
    // Yellow: #B58900, Blue: #268BD2, Magenta: #D33682
    // Cyan: #2AA198, White: #EEE8D5 (base2)
    // Bright Black: #002B36 (base03), Bright Red: #CB4B16 (orange)
    // Bright Green: #586E75 (base01), Bright Yellow: #657B83 (base00)
    // Bright Blue: #839496 (base0), Bright Magenta: #6C71C4 (violet)
    // Bright Cyan: #93A1A1 (base1), Bright White: #FDF6E3 (base3)

    public SolarizedLightTheme() : base(
        background: 0xFFFDF6E3,       // #FDF6E3 (base3)
        foreground: 0xFF657B83,       // #657B83 (base00)
        ansiBlack: 0xFF073642,        // #073642 (base02)
        ansiRed: 0xFFDC322F,          // #DC322F (red)
        ansiGreen: 0xFF859900,        // #859900 (green)
        ansiYellow: 0xFFB58900,       // #B58900 (yellow)
        ansiBlue: 0xFF268BD2,         // #268BD2 (blue)
        ansiMagenta: 0xFFD33682,      // #D33682 (magenta)
        ansiCyan: 0xFF2AA198,         // #2AA198 (cyan)
        ansiWhite: 0xFFEEE8D5,        // #EEE8D5 (base2)
        ansiBrightBlack: 0xFF002B36,  // #002B36 (base03)
        ansiBrightRed: 0xFFCB4B16,   // #CB4B16 (orange)
        ansiBrightGreen: 0xFF586E75,  // #586E75 (base01)
        ansiBrightYellow: 0xFF657B83, // #657B83 (base00)
        ansiBrightBlue: 0xFF839496,   // #839496 (base0)
        ansiBrightMagenta: 0xFF6C71C4,// #6C71C4 (violet)
        ansiBrightCyan: 0xFF93A1A1,   // #93A1A1 (base1)
        ansiBrightWhite: 0xFFFDF6E3   // #FDF6E3 (base3)
    )
    {
    }
}
