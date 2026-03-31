namespace Dotty.Abstractions.Themes;

/// <summary>
/// Catppuccin Latte theme - light counterpart to Catppuccin Mocha.
/// 
/// The light variant of the popular Catppuccin pastel theme.
/// Features soft, warm colors with excellent readability.
/// 
/// https://github.com/catppuccin/catppuccin
/// </summary>
public sealed class CatppuccinLatteTheme : ColorSchemeBase
{
    // Catppuccin Latte color palette
    // Background: #EFF1F5 -> 0xFFEFF1F5 (base)
    // Foreground: #4C4F69 -> 0xFF4C4F69 (text)
    
    // ANSI colors mapped from Catppuccin Latte palette
    // Using surface colors for the palette
    // Black: #5C5F77 (subtext0), Red: #D20F39, Green: #40A02B
    // Yellow: #DF8E1D, Blue: #1E66F5, Magenta: #EA76CB
    // Cyan: #179299, White: #ACB0BE (surface2)
    // Bright variants

    public CatppuccinLatteTheme() : base(
        background: 0xFFEFF1F5,       // #EFF1F5 (base)
        foreground: 0xFF4C4F69,       // #4C4F69 (text)
        ansiBlack: 0xFF5C5F77,        // #5C5F77 (subtext0)
        ansiRed: 0xFFD20F39,          // #D20F39 (red)
        ansiGreen: 0xFF40A02B,        // #40A02B (green)
        ansiYellow: 0xFFDF8E1D,       // #DF8E1D (yellow)
        ansiBlue: 0xFF1E66F5,         // #1E66F5 (blue)
        ansiMagenta: 0xFFEA76CB,      // #EA76CB (pink/magenta)
        ansiCyan: 0xFF179299,         // #179299 (teal/cyan)
        ansiWhite: 0xFFACB0BE,        // #ACB0BE (surface2)
        ansiBrightBlack: 0xFF6C6F85,  // #6C6F85 (subtext1)
        ansiBrightRed: 0xFFEE324C,    // lighter red
        ansiBrightGreen: 0xFF56C150,  // lighter green
        ansiBrightYellow: 0xFFF0AB39, // lighter yellow
        ansiBrightBlue: 0xFF4C89FF,   // lighter blue
        ansiBrightMagenta: 0xFFF495DA,// lighter magenta
        ansiBrightCyan: 0xFF2AB6B2,   // lighter cyan
        ansiBrightWhite: 0xFFCCD0DA   // #CCD0DA (surface1)
    )
    {
    }

    /// <summary>
    /// Window background opacity (0-100). Default is 100 (fully opaque).
    /// </summary>
    public override byte Opacity => 100;
}
