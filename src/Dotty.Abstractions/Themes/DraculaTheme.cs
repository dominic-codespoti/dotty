namespace Dotty.Abstractions.Themes;

/// <summary>
/// Dracula theme - a popular dark theme with vibrant colors.
/// 
/// Dracula is one of the most popular themes in the developer community,
/// featuring a dark purple background with bright, saturated colors.
/// 
/// https://draculatheme.com
/// </summary>
public sealed class DraculaTheme : ColorSchemeBase
{
    // Dracula color palette
    // Background: #282A36 -> 0xFF282A36
    // Foreground: #F8F8F2 -> 0xFFF8F8F2
    
    // ANSI colors
    // Black: #21222C, Red: #FF5555, Green: #50FA7B, Yellow: #F1FA8C
    // Blue: #BD93F9, Magenta: #FF79C6, Cyan: #8BE9FD, White: #F8F8F2
    // Bright variants (same as normal for Dracula)

    public DraculaTheme() : base(
        background: 0xFF282A36,       // #282A36
        foreground: 0xFFF8F8F2,      // #F8F8F2
        ansiBlack: 0xFF21222C,        // #21222C
        ansiRed: 0xFFFF5555,          // #FF5555
        ansiGreen: 0xFF50FA7B,       // #50FA7B
        ansiYellow: 0xFFF1FA8C,       // #F1FA8C
        ansiBlue: 0xFFBD93F9,         // #BD93F9 (purple, but acts as blue in Dracula)
        ansiMagenta: 0xFFFF79C6,      // #FF79C6 (pink, acts as magenta)
        ansiCyan: 0xFF8BE9FD,         // #8BE9FD (cyan)
        ansiWhite: 0xFFF8F8F2,       // #F8F8F2
        ansiBrightBlack: 0xFF6272A4, // #6272A4 (comment color)
        ansiBrightRed: 0xFFFF6E6E,    // #FF6E6E
        ansiBrightGreen: 0xFF69FF94,  // #69FF94
        ansiBrightYellow: 0xFFFFFFA5, // #FFFFA5
        ansiBrightBlue: 0xFFD6ACFF,   // #D6ACFF
        ansiBrightMagenta: 0xFFFF92DF,// #FF92DF
        ansiBrightCyan: 0xFFA4FFFF,   // #A4FFFF
        ansiBrightWhite: 0xFFFFFFFF   // #FFFFFF
    )
    {
    }
}
