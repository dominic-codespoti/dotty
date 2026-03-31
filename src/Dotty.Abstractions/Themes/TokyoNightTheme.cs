namespace Dotty.Abstractions.Themes;

/// <summary>
/// Tokyo Night theme - modern dark theme with deep blues and purples.
/// 
/// A clean, dark theme that celebrates the lights of Downtown Tokyo at night.
/// Features deep blues and purples with vibrant accent colors.
/// 
/// https://github.com/enkia/tokyo-night-vscode-theme
/// </summary>
public sealed class TokyoNightTheme : ColorSchemeBase
{
    // Tokyo Night color palette
    // Background: #1A1B26 -> 0xFF1A1B26 (bg)
    // Foreground: #A9B1D6 -> 0xFFA9B1D6 (fg)
    
    // ANSI colors
    // Black: #414868, Red: #F7768E, Green: #73DACA, Yellow: #E0AF68
    // Blue: #7AA2F7, Magenta: #BB9AF7, Cyan: #7DCFFF, White: #787C99
    // Bright variants

    public TokyoNightTheme() : base(
        background: 0xFF1A1B26,       // #1A1B26 (bg)
        foreground: 0xFFA9B1D6,       // #A9B1D6 (fg)
        ansiBlack: 0xFF414868,         // #414868 (dark3)
        ansiRed: 0xFFF7768E,          // #F7768E (red)
        ansiGreen: 0xFF73DACA,        // #73DACA (green)
        ansiYellow: 0xFFE0AF68,        // #E0AF68 (yellow/orange)
        ansiBlue: 0xFF7AA2F7,         // #7AA2F7 (blue)
        ansiMagenta: 0xFFBB9AF7,      // #BB9AF7 (purple/magenta)
        ansiCyan: 0xFF7DCFFF,         // #7DCFFF (cyan)
        ansiWhite: 0xFF787C99,        // #787C99 (dark5)
        ansiBrightBlack: 0xFF565F89,  // #565F89 (terminal_black)
        ansiBrightRed: 0xFFFF8EA0,    // lighter red
        ansiBrightGreen: 0xFF8BECC8,  // lighter green
        ansiBrightYellow: 0xFFFFD88A, // lighter yellow
        ansiBrightBlue: 0xFF9AB8FF,   // lighter blue
        ansiBrightMagenta: 0xFFD4BFFF,// lighter magenta
        ansiBrightCyan: 0xFF9ED7FF,   // lighter cyan
        ansiBrightWhite: 0xFFC0CAF5   // #C0CAF5 (fg_highlight)
    )
    {
    }

    /// <summary>
    /// Window background opacity (0-100). Default is 100 (fully opaque).
    /// </summary>
    public override byte Opacity => 100;
}
