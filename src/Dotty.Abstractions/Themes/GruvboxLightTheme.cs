namespace Dotty.Abstractions.Themes;

/// <summary>
/// Gruvbox Light theme - warm light theme with earthy tones.
/// 
/// The light variant of the popular Gruvbox theme.
/// Features warm, muted colors that are easy on the eyes.
/// 
/// https://github.com/morhetz/gruvbox
/// </summary>
public sealed class GruvboxLightTheme : ColorSchemeBase
{
    // Gruvbox Light color palette (light mode, medium contrast)
    // Background: #FBF1C7 -> 0xFFFBF1C7 (light0)
    // Foreground: #3C3836 -> 0xFF3C3836 (dark0)
    
    // ANSI colors
    // Black: #3C3836, Red: #CC241D, Green: #98971A, Yellow: #D79921
    // Blue: #458588, Magenta: #B16286, Cyan: #689D6A, White: #7C6F64
    // Bright Black: #928374, Bright Red: #9D0006, Bright Green: #79740E
    // Bright Yellow: #B57614, Bright Blue: #076678, Bright Magenta: #8F3F71
    // Bright Cyan: #427B58, Bright White: #3C3836

    public GruvboxLightTheme() : base(
        background: 0xFFFBF1C7,       // #FBF1C7 (light0)
        foreground: 0xFF3C3836,       // #3C3836 (dark0)
        ansiBlack: 0xFF3C3836,        // #3C3836 (dark0)
        ansiRed: 0xFFCC241D,         // #CC241D (neutral_red)
        ansiGreen: 0xFF98971A,        // #98971A (neutral_green)
        ansiYellow: 0xFFD79921,        // #D79921 (neutral_yellow)
        ansiBlue: 0xFF458588,         // #458588 (neutral_blue)
        ansiMagenta: 0xFFB16286,      // #B16286 (neutral_purple)
        ansiCyan: 0xFF689D6A,         // #689D6A (neutral_aqua)
        ansiWhite: 0xFF7C6F64,        // #7C6F64 (light4)
        ansiBrightBlack: 0xFF928374,  // #928374 (gray)
        ansiBrightRed: 0xFF9D0006,   // #9D0006 (faded_red)
        ansiBrightGreen: 0xFF79740E,  // #79740E (faded_green)
        ansiBrightYellow: 0xFFB57614, // #B57614 (faded_yellow)
        ansiBrightBlue: 0xFF076678,   // #076678 (faded_blue)
        ansiBrightMagenta: 0xFF8F3F71,// #8F3F71 (faded_purple)
        ansiBrightCyan: 0xFF427B58,   // #427B58 (faded_aqua)
        ansiBrightWhite: 0xFF282828   // #282828 (dark0_hard)
    )
    {
    }
}
