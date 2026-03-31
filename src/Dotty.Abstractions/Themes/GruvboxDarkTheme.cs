namespace Dotty.Abstractions.Themes;

/// <summary>
/// Gruvbox Dark theme - warm dark theme with earthy tones.
/// 
/// Gruvbox is designed to be easy on the eyes for long coding sessions.
/// It features warm, muted colors inspired by retro groove aesthetics.
/// 
/// https://github.com/morhetz/gruvbox
/// </summary>
public sealed class GruvboxDarkTheme : ColorSchemeBase
{
    // Gruvbox Dark color palette (dark mode, medium contrast)
    // Background: #282828 -> 0xFF282828
    // Foreground: #EBDBB2 -> 0xFFEBDBB2
    
    // ANSI colors
    // Black: #282828, Red: #CC241D, Green: #98971A, Yellow: #D79921
    // Blue: #458588, Magenta: #B16286, Cyan: #689D6A, White: #A89984
    // Bright Black: #928374, Bright Red: #FB4934, Bright Green: #B8BB26
    // Bright Yellow: #FABD2F, Bright Blue: #83A598, Bright Magenta: #D3869B
    // Bright Cyan: #8EC07C, Bright White: #EBDBB2

    public GruvboxDarkTheme() : base(
        background: 0xFF282828,       // #282828 (dark0)
        foreground: 0xFFEBDBB2,      // #EBDBB2 (light1)
        ansiBlack: 0xFF282828,       // #282828 (dark0)
        ansiRed: 0xFFCC241D,         // #CC241D (neutral_red)
        ansiGreen: 0xFF98971A,       // #98971A (neutral_green)
        ansiYellow: 0xFFD79921,       // #D79921 (neutral_yellow)
        ansiBlue: 0xFF458588,         // #458588 (neutral_blue)
        ansiMagenta: 0xFFB16286,      // #B16286 (neutral_purple)
        ansiCyan: 0xFF689D6A,         // #689D6A (neutral_aqua)
        ansiWhite: 0xFFA89984,        // #A89984 (light4)
        ansiBrightBlack: 0xFF928374,  // #928374 (gray)
        ansiBrightRed: 0xFFFB4934,   // #FB4934 (bright_red)
        ansiBrightGreen: 0xFFB8BB26,  // #B8BB26 (bright_green)
        ansiBrightYellow: 0xFFFABD2F, // #FABD2F (bright_yellow)
        ansiBrightBlue: 0xFF83A598,   // #83A598 (bright_blue)
        ansiBrightMagenta: 0xFFD3869B,// #D3869B (bright_purple)
        ansiBrightCyan: 0xFF8EC07C,   // #8EC07C (bright_aqua)
        ansiBrightWhite: 0xFFFBF1C7  // #FBF1C7 (light0)
    )
    {
    }
}
