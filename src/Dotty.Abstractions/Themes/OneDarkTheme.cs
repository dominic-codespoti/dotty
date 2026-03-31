namespace Dotty.Abstractions.Themes;

/// <summary>
/// One Dark theme - inspired by the Atom editor.
/// 
/// A subtle dark theme with muted, professional colors.
/// This was the default theme for Atom editor and is popular among developers.
/// 
/// https://github.com/atom/atom/tree/master/packages/one-dark-ui
/// </summary>
public sealed class OneDarkTheme : ColorSchemeBase
{
    // One Dark color palette
    // Background: #282C34 -> 0xFF282C34
    // Foreground: #ABB2BF -> 0xFFABB2BF
    
    // ANSI colors (adapted from One Dark syntax colors)
    // Black: #282C34, Red: #E06C75, Green: #98C379, Yellow: #E5C07B
    // Blue: #61AFEF, Magenta: #C678DD, Cyan: #56B6C2, White: #ABB2BF
    // Bright Black: #5C6370, Bright Red: #E06C75, Bright Green: #98C379
    // Bright Yellow: #E5C07B, Bright Blue: #61AFEF, Bright Magenta: #C678DD
    // Bright Cyan: #56B6C2, Bright White: #FFFFFF

    public OneDarkTheme() : base(
        background: 0xFF282C34,       // #282C34
        foreground: 0xFFABB2BF,       // #ABB2BF
        ansiBlack: 0xFF282C34,        // #282C34
        ansiRed: 0xFFE06C75,          // #E06C75 (red)
        ansiGreen: 0xFF98C379,        // #98C379 (green)
        ansiYellow: 0xFFE5C07B,       // #E5C07B (yellow/orange)
        ansiBlue: 0xFF61AFEF,         // #61AFEF (blue)
        ansiMagenta: 0xFFC678DD,      // #C678DD (purple/magenta)
        ansiCyan: 0xFF56B6C2,         // #56B6C2 (cyan)
        ansiWhite: 0xFFABB2BF,        // #ABB2BF (white/gray)
        ansiBrightBlack: 0xFF5C6370,  // #5C6370 (bright black)
        ansiBrightRed: 0xFFFF7A85,    // #FF7A85 (bright red)
        ansiBrightGreen: 0xFFB5E090,  // #B5E090 (bright green)
        ansiBrightYellow: 0xFFFFD58F, // #FFD58F (bright yellow)
        ansiBrightBlue: 0xFF8CCBFF,   // #8CCBFF (bright blue)
        ansiBrightMagenta: 0xFFE599FF,// #E599FF (bright magenta)
        ansiBrightCyan: 0xFF89DDFF,   // #89DDFF (bright cyan)
        ansiBrightWhite: 0xFFFFFFFF   // #FFFFFF (bright white)
    )
    {
    }
}
