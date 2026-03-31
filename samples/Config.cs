// Sample Dotty Configuration File
// =================================
// Copy this file to your project and customize the values.
// The Source Generator will detect your IDottyConfig implementation
// and generate a static Config class with your settings.
//
// Place this file in: ~/.config/dotty/Config.cs or in your project root

using Dotty.Abstractions.Config;

namespace Dotty.UserConfig;

/// <summary>
/// Example custom configuration for Dotty terminal.
/// Uncomment and modify properties to customize your terminal.
/// </summary>
public partial class MyDottyConfig : IDottyConfig
{
    // Font Settings
    // =============
    // Font family stack - comma-separated list with fallbacks
    // public string? FontFamily => "Fira Code, JetBrains Mono, Cascadia Code, monospace";
    
    // Font size in points
    // public double? FontSize => 13.0;
    
    // Cell padding in pixels
    // public double? CellPadding => 2.0;
    
    // Content padding around terminal area (Left, Top, Right, Bottom)
    // public Thickness? ContentPadding => new Thickness(4.0, 8.0, 4.0, 8.0);

    // Color Scheme
    // ============
    // Uncomment to use a custom color scheme
    // public IColorScheme? Colors => new DarkTheme();
    
    // Key Bindings
    // ============
    // Uncomment to use custom key bindings
    // public IKeyBindings? KeyBindings => new CustomKeyBindings();

    // Terminal Settings
    // =================
    // Number of scrollback lines to keep in memory
    // public int? ScrollbackLines => 50000;
    
    // Time before inactive tab visuals are destroyed (ms)
    // public int? InactiveTabDestroyDelayMs => 10000;

    // Window Settings
    // ===============
    // Initial terminal dimensions
    // public IWindowDimensions? InitialDimensions => new WindowDimensions
    // {
    //     Columns = 120,
    //     Rows = 40,
    //     Title = "My Terminal"
    // };

    // Cursor Settings
    // ===============
    // public ICursorSettings? Cursor => new CursorSettings
    // {
    //     Shape = CursorShape.Beam,
    //     Blink = true,
    //     BlinkIntervalMs = 600,
    //     Color = 0xFF00FF00,  // Green cursor
    //     ShowUnfocused = true
    // };

    // Selection Color (ARGB format)
    // public uint? SelectionColor => 0xA03385DB;
    
    // Tab Bar Background Color (ARGB format)
    // public uint? TabBarBackgroundColor => 0xFF1A1A1A;
}

/// <summary>
/// Example dark theme color scheme.
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
    public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
    {
        // Example: Custom key bindings
        // Return null to use the default bindings
        
        // Uncomment to add custom bindings:
        // if (key == Key.F12 && modifiers == KeyModifiers.None)
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
