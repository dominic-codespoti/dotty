// Sample Dotty Configuration File
// =================================
// Copy this file to your project and customize the values.
// The Source Generator will detect your IDottyConfig implementation
// and generate a static Config class with your settings.
//
// Place this file in: ~/.config/dotty/Config.cs or in your project root

using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

/// <summary>
/// Example custom configuration for Dotty terminal.
/// Uncomment and modify properties to customize your terminal.
/// </summary>
public partial class MyDottyConfig : IDottyConfig
{
    // =========================================================================
    // THEMING EXAMPLES - Choose one of the following approaches:
    // =========================================================================
    
    // Option 1: Use a built-in theme (recommended for most users)
    // -------------------------------------------------------------
    // Built-in dark themes:
    //   - DarkPlus (VS Code Dark+) - default, great readability
    //   - Dracula - vibrant colors, popular choice
    //   - OneDark (Atom) - muted, professional
    //   - GruvboxDark - warm, easy on eyes
    //   - CatppuccinMocha - pastel colors
    //   - TokyoNight - deep blues and purples
    //
    // Built-in light themes:
    //   - LightPlus (VS Code Light+) - clean, bright
    //   - OneLight - balanced light theme
    //   - GruvboxLight - warm light variant
    //   - CatppuccinLatte - pastel light variant
    //   - SolarizedLight - low contrast, eye-friendly
    //
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    
    // Option 2: Time-based automatic theme switching
    // ----------------------------------------------
    // Uncomment to use light theme during day (6am-6pm), dark theme at night
    // public IColorScheme? Colors => DateTime.Now.Hour is >= 6 and < 18 
    //     ? BuiltInThemes.LightPlus 
    //     : BuiltInThemes.DarkPlus;
    
    // Option 3: Create a custom theme
    // ------------------------------
    // Uncomment to define your own color scheme
    // public IColorScheme? Colors => new MyCustomTheme();
    
    // Option 4: Override specific colors from a base theme
    // ----------------------------------------------------
    // Use a built-in theme but override specific colors
    // public IColorScheme? Colors => new ThemeOverride(
    //     baseTheme: BuiltInThemes.DarkPlus,
    //     background: 0xFF000000,  // Pure black background
    //     foreground: 0xFFFFFFFF   // Pure white foreground
    // );
    
    // Option 5: Create a translucent theme
    // ----------------------------------------------------
    // Use a built-in theme but make it partially transparent
    // public IColorScheme? Colors => new TranslucentDarkTheme();
    
    // =========================================================================
    // TRANSPARENCY / OPACITY
    // =========================================================================
    
    // Window opacity can be controlled per theme by overriding the Opacity property.
    // Opacity is 0-100 where 100 is fully opaque (default) and 0 is fully transparent.
    // Recommended values for subtle effect: 85-95 (85% opaque = 15% transparent)
    //
    // Example: Translucent theme with 85% opacity
    // public IColorScheme? Colors => new TranslucentDarkTheme();
    //
    // Example: Time-based opacity (more transparent at night)
    // public IColorScheme? Colors => new TimeBasedOpacityTheme();
    //
    // Note: True "see-through" with blurred background requires platform-specific APIs:
    //   - Windows: DWM blur
    //   - macOS: NSVisualEffectView  
    //   - Linux: Depends on compositor
    // The basic opacity affects the entire window uniformly.
    
    // =========================================================================
    // FONT SETTINGS
    // =========================================================================
    
    // Font family stack - comma-separated list with fallbacks
    // The order matters: first available font is used
    // public string? FontFamily => "Fira Code, JetBrains Mono, Cascadia Code, monospace";
    
    // Font size in points (default: 15.0)
    // public double? FontSize => 13.0;
    
    // Cell padding in pixels (default: 1.5)
    // public double? CellPadding => 2.0;
    
    // Content padding around terminal area (Left, Top, Right, Bottom)
    // public Thickness? ContentPadding => new Thickness(4.0, 8.0, 4.0, 8.0);

    // =========================================================================
    // KEY BINDINGS
    // =========================================================================
    
    // Uncomment to use custom key bindings
    // public IKeyBindings? KeyBindings => new CustomKeyBindings();

    // =========================================================================
    // TERMINAL SETTINGS
    // =========================================================================
    
    // Number of scrollback lines to keep in memory (default: 10000)
    // public int? ScrollbackLines => 50000;
    
    // Time before inactive tab visuals are destroyed (default: 5000ms)
    // public int? InactiveTabDestroyDelayMs => 10000;

    // =========================================================================
    // WINDOW SETTINGS
    // =========================================================================
    
    // Initial terminal dimensions
    // public IWindowDimensions? InitialDimensions => new WindowDimensions
    // {
    //     Columns = 120,
    //     Rows = 40,
    //     Title = "My Terminal"
    // };

    // =========================================================================
    // CURSOR SETTINGS
    // =========================================================================
    
    // public ICursorSettings? Cursor => new CursorSettings
    // {
    //     Shape = CursorShape.Beam,
    //     Blink = true,
    //     BlinkIntervalMs = 600,
    //     Color = 0xFF00FF00,  // Green cursor
    //     ShowUnfocused = true
    // };

    // =========================================================================
    // UI COLORS
    // =========================================================================
    
    // Selection highlight color (ARGB format)
    // public uint? SelectionColor => 0xA03385DB;
    
    // Tab bar background color (ARGB format)
    // public uint? TabBarBackgroundColor => 0xFF1A1A1A;
}

// =========================================================================
// CUSTOM THEME EXAMPLE
// =========================================================================

/// <summary>
/// Example custom theme implementation.
/// Inherit from ColorSchemeBase to get validation and helper methods.
/// </summary>
public class MyCustomTheme : ColorSchemeBase
{
    public MyCustomTheme() : base(
        background: 0xFF1A1B26,      // Deep blue-gray
        foreground: 0xFFABB2BF,       // Soft white
        ansiBlack: 0xFF282C34,        // Dark gray
        ansiRed: 0xFFE06C75,          // Coral red
        ansiGreen: 0xFF98C379,        // Sage green
        ansiYellow: 0xFFE5C07B,       // Soft yellow
        ansiBlue: 0xFF61AFEF,         // Sky blue
        ansiMagenta: 0xFFC678DD,      // Soft purple
        ansiCyan: 0xFF56B6C2,         // Teal
        ansiWhite: 0xFFABB2BF,        // Light gray
        ansiBrightBlack: 0xFF5C6370,  // Medium gray
        ansiBrightRed: 0xFFFF7A85,    // Bright coral
        ansiBrightGreen: 0xFFB5E090,  // Light sage
        ansiBrightYellow: 0xFFFFD580,   // Light yellow
        ansiBrightBlue: 0xFF8CCBFF,   // Light sky blue
        ansiBrightMagenta: 0xFFE599FF,// Light purple
        ansiBrightCyan: 0xFF89DDFF,   // Light teal
        ansiBrightWhite: 0xFFFFFFFF   // Pure white
    )
    {
    }
}

/// <summary>
/// Theme override helper - allows overriding specific colors from a base theme.
/// </summary>
public class ThemeOverride : ColorSchemeBase
{
    public ThemeOverride(
        IColorScheme baseTheme,
        uint? background = null,
        uint? foreground = null,
        uint? ansiBlack = null,
        uint? ansiRed = null,
        uint? ansiGreen = null,
        uint? ansiYellow = null,
        uint? ansiBlue = null,
        uint? ansiMagenta = null,
        uint? ansiCyan = null,
        uint? ansiWhite = null,
        uint? ansiBrightBlack = null,
        uint? ansiBrightRed = null,
        uint? ansiBrightGreen = null,
        uint? ansiBrightYellow = null,
        uint? ansiBrightBlue = null,
        uint? ansiBrightMagenta = null,
        uint? ansiBrightCyan = null,
        uint? ansiBrightWhite = null)
        : base(
            background ?? baseTheme.Background,
            foreground ?? baseTheme.Foreground,
            ansiBlack ?? baseTheme.AnsiBlack,
            ansiRed ?? baseTheme.AnsiRed,
            ansiGreen ?? baseTheme.AnsiGreen,
            ansiYellow ?? baseTheme.AnsiYellow,
            ansiBlue ?? baseTheme.AnsiBlue,
            ansiMagenta ?? baseTheme.AnsiMagenta,
            ansiCyan ?? baseTheme.AnsiCyan,
            ansiWhite ?? baseTheme.AnsiWhite,
            ansiBrightBlack ?? baseTheme.AnsiBrightBlack,
            ansiBrightRed ?? baseTheme.AnsiBrightRed,
            ansiBrightGreen ?? baseTheme.AnsiBrightGreen,
            ansiBrightYellow ?? baseTheme.AnsiBrightYellow,
            ansiBrightBlue ?? baseTheme.AnsiBrightBlue,
            ansiBrightMagenta ?? baseTheme.AnsiBrightMagenta,
            ansiBrightCyan ?? baseTheme.AnsiBrightCyan,
            ansiBrightWhite ?? baseTheme.AnsiBrightWhite
        )
    {
    }
}

// =========================================================================
// TRANSLUCENT THEME EXAMPLES
// =========================================================================

/// <summary>
/// Example translucent dark theme with 85% opacity (15% transparent).
/// This gives a subtle see-through effect without hurting readability.
/// </summary>
public class TranslucentDarkTheme : DarkPlusTheme
{
    public override byte Opacity => 85; // 85% opaque, 15% transparent
}

/// <summary>
/// Example theme that changes opacity based on time of day.
/// More transparent at night (90%), fully opaque during day.
/// </summary>
public class TimeBasedOpacityTheme : DarkPlusTheme
{
    public override byte Opacity => DateTime.Now.Hour is >= 20 or < 6 ? 90 : 100;
}

/// <summary>
/// Example highly transparent theme (for advanced users).
/// 70% opacity creates a strong see-through effect.
/// </summary>
public class HighlyTransparentTheme : DarkPlusTheme
{
    // Only recommended with strong background colors or for specific use cases
    public override byte Opacity => 70;
}

// =========================================================================
// LEGACY EXAMPLES (for reference)
// =========================================================================

/// <summary>
/// Example dark theme color scheme (Monokai-inspired).
/// </summary>
public class DarkTheme : IColorScheme
{
    // Background: Dark gray (#181818)
    public uint Background => 0xFF181818;
    
    // Foreground: Light beige (#F8F8F2)
    public uint Foreground => 0xFFF8F8F2;

    // ANSI 16-color palette (Monokai-inspired)
    public uint AnsiBlack => 0xFF272822;
    public uint AnsiRed => 0xFFF92672;
    public uint AnsiGreen => 0xFFA6E22E;
    public uint AnsiYellow => 0xFFF4BF75;
    public uint AnsiBlue => 0xFF66D9EF;
    public uint AnsiMagenta => 0xFFAE81FF;
    public uint AnsiCyan => 0xFFA1EFE4;
    public uint AnsiWhite => 0xFFF8F8F2;
    public uint AnsiBrightBlack => 0xFF75715E;
    public uint AnsiBrightRed => 0xFFF92672;
    public uint AnsiBrightGreen => 0xFFA6E22E;
    public uint AnsiBrightYellow => 0xFFF4BF75;
    public uint AnsiBrightBlue => 0xFF66D9EF;
    public uint AnsiBrightMagenta => 0xFFAE81FF;
    public uint AnsiBrightCyan => 0xFFA1EFE4;
    public uint AnsiBrightWhite => 0xFFF9F8F5;
}

/// <summary>
/// Example light theme color scheme.
/// </summary>
public class LightTheme : IColorScheme
{
    public uint Background => 0xFFFFFFFF;
    public uint Foreground => 0xFF000000;
    
    public uint AnsiBlack => 0xFF000000;
    public uint AnsiRed => 0xFFCD0000;
    public uint AnsiGreen => 0xFF00CD00;
    public uint AnsiYellow => 0xFFCDCD00;
    public uint AnsiBlue => 0xFF0000EE;
    public uint AnsiMagenta => 0xFFCD00CD;
    public uint AnsiCyan => 0xFF00CDCD;
    public uint AnsiWhite => 0xFFE5E5E5;
    public uint AnsiBrightBlack => 0xFF7F7F7F;
    public uint AnsiBrightRed => 0xFFFF0000;
    public uint AnsiBrightGreen => 0xFF00FF00;
    public uint AnsiBrightYellow => 0xFFFFFF00;
    public uint AnsiBrightBlue => 0xFF5C5CFF;
    public uint AnsiBrightMagenta => 0xFFFF00FF;
    public uint AnsiBrightCyan => 0xFF00FFFF;
    public uint AnsiBrightWhite => 0xFFFFFFFF;
}

/// <summary>
/// Example custom key bindings.
/// </summary>
public class CustomKeyBindings : IKeyBindings
{
    public TerminalAction? GetAction(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)
    {
        // Example: Custom key bindings
        // Return null to use the default bindings
        
        // Uncomment to add custom bindings:
        // if (key == Avalonia.Input.Key.F12 && modifiers == Avalonia.Input.KeyModifiers.None)
        //     return TerminalAction.ToggleFullscreen;
        
        return null;  // Use defaults
    }
}

/// <summary>
/// Example window dimensions implementation.
/// </summary>
public class WindowDimensions : IWindowDimensions
{
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public int? WidthPixels { get; init; } = null;
    public int? HeightPixels { get; init; } = null;
    public bool StartFullscreen { get; init; } = false;
    public string? Title { get; init; } = "Dotty";
}

/// <summary>
/// Example cursor settings implementation.
/// </summary>
public class CursorSettings : ICursorSettings
{
    public CursorShape Shape { get; init; } = CursorShape.Block;
    public bool Blink { get; init; } = true;
    public int BlinkIntervalMs { get; init; } = 500;
    public uint? Color { get; init; } = null;
    public bool ShowUnfocused { get; init; } = false;
}
