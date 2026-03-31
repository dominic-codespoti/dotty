namespace Dotty.Abstractions.Themes;

/// <summary>
/// VS Code Dark+ theme - the default theme for Dotty.
/// 
/// Colors sourced from VS Code's default dark theme.
/// This is the recommended default as it provides excellent readability
/// and familiarity for VS Code users.
/// </summary>
public sealed class DarkPlusTheme : ColorSchemeBase
{
    // Colors from VS Code Dark+ theme
    // Background: #1E1E1E -> 0xFF1E1E1E
    // Foreground: #D4D4D4 -> 0xFFD4D4D4
    
    // ANSI colors from VS Code terminal
    // Black: #000000, Red: #CD3131, Green: #0DBC79, Yellow: #E5E510
    // Blue: #2472C8, Magenta: #BC3FBC, Cyan: #11A8CD, White: #E5E5E5
    // Bright Black: #666666, Bright Red: #F14C4C, Bright Green: #23D18B
    // Bright Yellow: #F5F543, Bright Blue: #3B8EEA, Bright Magenta: #D670D6
    // Bright Cyan: #29B8DB, Bright White: #E5E5E5

    public DarkPlusTheme() : base(
        background: 0xFF1E1E1E,      // #1E1E1E
        foreground: 0xFFD4D4D4,      // #D4D4D4
        ansiBlack: 0xFF000000,        // #000000
        ansiRed: 0xFFCD3131,          // #CD3131
        ansiGreen: 0xFF0DBC79,        // #0DBC79
        ansiYellow: 0xFFE5E510,       // #E5E510
        ansiBlue: 0xFF2472C8,         // #2472C8
        ansiMagenta: 0xFFBC3FBC,      // #BC3FBC
        ansiCyan: 0xFF11A8CD,         // #11A8CD
        ansiWhite: 0xFFE5E5E5,        // #E5E5E5
        ansiBrightBlack: 0xFF666666,  // #666666
        ansiBrightRed: 0xFFF14C4C,    // #F14C4C
        ansiBrightGreen: 0xFF23D18B,   // #23D18B
        ansiBrightYellow: 0xFFF5F543, // #F5F543
        ansiBrightBlue: 0xFF3B8EEA,   // #3B8EEA
        ansiBrightMagenta: 0xFFD670D6,// #D670D6
        ansiBrightCyan: 0xFF29B8DB,    // #29B8DB
        ansiBrightWhite: 0xFFFFFFFF    // #FFFFFF
    )
    {
    }
}
