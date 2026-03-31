namespace Dotty.Abstractions.Themes;

/// <summary>
/// One Light theme - the light counterpart to One Dark.
/// 
/// A balanced light theme with professional, muted colors.
/// Good for users who prefer light themes but want something more refined than plain white.
/// </summary>
public sealed class OneLightTheme : ColorSchemeBase
{
    // One Light color palette
    // Background: #FAFAFA -> 0xFFFAFAFA
    // Foreground: #383A42 -> 0xFF383A42
    
    // ANSI colors (adapted from One Light syntax colors)
    // Black: #383A42, Red: #E45649, Green: #50A14F, Yellow: #C18401
    // Blue: #4078F2, Magenta: #A626A4, Cyan: #0184BC, White: #A0A1A7
    // Bright Black: #4F525D, Bright Red: #E45649, Bright Green: #50A14F
    // Bright Yellow: #C18401, Bright Blue: #4078F2, Bright Magenta: #A626A4
    // Bright Cyan: #0184BC, Bright White: #FFFFFF

    public OneLightTheme() : base(
        background: 0xFFFAFAFA,       // #FAFAFA
        foreground: 0xFF383A42,       // #383A42
        ansiBlack: 0xFF383A42,        // #383A42
        ansiRed: 0xFFE45649,          // #E45649
        ansiGreen: 0xFF50A14F,        // #50A14F
        ansiYellow: 0xFFC18401,       // #C18401
        ansiBlue: 0xFF4078F2,         // #4078F2
        ansiMagenta: 0xFFA626A4,      // #A626A4
        ansiCyan: 0xFF0184BC,         // #0184BC
        ansiWhite: 0xFFA0A1A7,        // #A0A1A7
        ansiBrightBlack: 0xFF4F525D,  // #4F525D
        ansiBrightRed: 0xFFFF6E66,    // lighter red
        ansiBrightGreen: 0xFF6BC468,  // lighter green
        ansiBrightYellow: 0xFFD9940F, // lighter yellow
        ansiBrightBlue: 0xFF6394FF,   // lighter blue
        ansiBrightMagenta: 0xFFC053BE,// lighter magenta
        ansiBrightCyan: 0xFF38B7F0,   // lighter cyan
        ansiBrightWhite: 0xFFFFFFFF   // #FFFFFF
    )
    {
    }

    /// <summary>
    /// Window background opacity (0-100). Default is 100 (fully opaque).
    /// </summary>
    public override byte Opacity => 100;
}
