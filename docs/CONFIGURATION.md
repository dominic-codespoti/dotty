# Dotty Configuration System

Dotty uses a C# Source Generator-based configuration system that allows users to customize terminal settings using C# code that gets compiled into the application at build time.

## Overview

The configuration system consists of:

1. **Configuration Interfaces** (`Dotty.Abstractions.Config`) - Define the contract for configuration
2. **Source Generator** (`Dotty.Config.SourceGenerator`) - Scans for IDottyConfig implementations and generates a static `Config` class
3. **Generated Code** (`Dotty.Generated` namespace) - Static configuration values available at runtime
4. **ConfigBridge** (`Dotty.App.Configuration`) - Helper to convert generated values to Avalonia types

## Quick Start

### NuGet Package

Dotty.Abstractions is available on NuGet.org, providing full IntelliSense and LSP support for your configuration:

**Package:** [Dotty.Abstractions 0.1.0](https://www.nuget.org/packages/Dotty.Abstractions/)

```bash
dotnet add package Dotty.Abstractions --version 0.1.0
```

### 1. First Run (Automatic Config Generation)

On first startup, Dotty automatically creates a default configuration project:

- **Linux/macOS**: `~/.config/dotty/Dotty.UserConfig/` (XDG Base Directory)
- **Windows**: `%APPDATA%/dotty/Dotty.UserConfig/`

The project structure includes:
```
~/.config/dotty/Dotty.UserConfig/
├── Dotty.UserConfig.csproj    # Project file with NuGet reference
├── Config.cs                  # Your configuration file
└── obj/                       # Build artifacts
```

You'll see a message:
```
✓ Created default config project: /home/username/.config/dotty/Dotty.UserConfig/

Open in your IDE for IntelliSense support:
  code ~/.config/dotty/Dotty.UserConfig/     # VS Code
  rider ~/.config/dotty/Dotty.UserConfig/    # JetBrains Rider

Then edit Config.cs and rebuild dotty to apply changes.
```

The generated file contains:
- Sensible defaults (DarkPlus theme, JetBrains Mono 15pt)
- Helpful comments explaining all options
- Uncomment examples for easy customization
- Full IntelliSense via the NuGet package reference
- Exact current defaults (never out of sync with code)

### 2. Open in Your IDE (Recommended)

For the best experience with IntelliSense, syntax highlighting, and LSP support:

```bash
# VS Code
code ~/.config/dotty/Dotty.UserConfig/

# JetBrains Rider
rider ~/.config/dotty/Dotty.UserConfig/

# Or any editor that supports C# LSP
```

The `.csproj` file includes the `Dotty.Abstractions` NuGet package, giving you:
- Full IntelliSense for all configuration options
- Real-time error detection
- Go-to-definition for types and themes
- Theme preview in tooltips

### 3. Customizing Your Configuration

Edit the `Config.cs` file in your IDE:

```csharp
// Change the theme
public IColorScheme? Colors => BuiltInThemes.Dracula;

// Adjust font size
public double? FontSize => 14.0;

// Increase scrollback
public int? ScrollbackLines => 50000;
```

### 4. Rebuilding to Apply Changes

After editing, rebuild Dotty to apply your configuration changes:

```bash
dotnet build
# or
dotnet build -c Release
```

The Source Generator will pick up your changes and generate a new static `Config` class.

> **Note:** You only need to rebuild the main Dotty application. The configuration project (`Dotty.UserConfig`) is compiled automatically as part of the build process.

### 5. Regenerating Config (Optional)

To regenerate a fresh config project:

```bash
dotty --generate-config
```

⚠️ This will overwrite your existing config! Back up first.

## Configuration Options

### Font Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FontFamily` | `string?` | JetBrains Mono | Comma-separated font stack |
| `FontSize` | `double?` | 15.0 | Font size in points |
| `CellPadding` | `double?` | 1.5 | Cell padding in pixels |
| `ContentPadding` | `Thickness?` | 0,0,0,0 | Padding around terminal area |

### Color Scheme (IColorScheme)

All colors in ARGB format (0xAARRGGBB):

- `Background` - Terminal background
- `Foreground` - Text color
- `AnsiBlack` through `AnsiWhite` (0-7) - Standard ANSI colors
- `AnsiBrightBlack` through `AnsiBrightWhite` (8-15) - Bright ANSI colors

### Key Bindings (IKeyBindings)

Key bindings map Avalonia `Key` + `KeyModifiers` to `TerminalAction`:

```csharp
public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
{
    if (key == Key.T && modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        return TerminalAction.NewTab;
    
    return null; // Use default for unhandled keys
}
```

Available actions:
- `NewTab`, `CloseTab`, `NextTab`, `PreviousTab`
- `SwitchTab1` through `SwitchTab9`
- `Copy`, `Paste`, `Clear`
- `ToggleFullscreen`, `ZoomIn`, `ZoomOut`, `ResetZoom`
- `Search`, `DuplicateTab`, `CloseOtherTabs`
- `Quit`

### Terminal Settings

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ScrollbackLines` | `int?` | 10000 | Scrollback buffer size |
| `InactiveTabDestroyDelayMs` | `int?` | 5000 | Delay before destroying inactive tab visuals |
| `SelectionColor` | `uint?` | 0xA03385DB | Selection highlight color (ARGB) |
| `TabBarBackgroundColor` | `uint?` | 0xFF1A1A1A | Tab bar background (ARGB) |

### Window Opacity

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Opacity` (on theme) | `byte` | 100 | Window opacity 0-100 (100 = fully opaque) |

Window transparency is controlled through the color scheme's `Opacity` property:

```csharp
// Create a translucent theme
public class TranslucentTheme : DarkPlusTheme
{
    public override byte Opacity => 85; // 85% opaque, 15% transparent
}

public partial class MyConfig : IDottyConfig
{
    public IColorScheme? Colors => new TranslucentTheme();
}
```

### Window Settings (IWindowDimensions)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Columns` | `int` | 80 | Initial terminal columns |
| `Rows` | `int` | 24 | Initial terminal rows |
| `WidthPixels` | `int?` | null | Optional fixed width in pixels |
| `HeightPixels` | `int?` | null | Optional fixed height in pixels |
| `StartFullscreen` | `bool` | false | Start in fullscreen mode |
| `Title` | `string?` | "Dotty" | Window title |

### Cursor Settings (ICursorSettings)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Shape` | `CursorShape` | Block | Block, Beam, or Underline |
| `Blink` | `bool` | true | Cursor blinking |
| `BlinkIntervalMs` | `int` | 500 | Blink interval in milliseconds |
| `Color` | `uint?` | null | Cursor color (null = use foreground) |
| `ShowUnfocused` | `bool` | false | Show cursor when terminal not focused |

## Theming

Dotty provides a comprehensive theming system with built-in popular themes and support for custom themes.

### Using Built-in Themes

The easiest way to customize colors is to use a built-in theme:

```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public partial class MyConfig : IDottyConfig
{
    // Use a built-in theme
    public IColorScheme? Colors => BuiltInThemes.Dracula;
}
```

### Available Built-in Themes

#### Dark Themes

| Theme | Description |
|-------|-------------|
| `BuiltInThemes.DarkPlus` | VS Code Dark+ theme (default) - great readability, familiar to VS Code users |
| `BuiltInThemes.Dracula` | Popular dark theme with vibrant, saturated colors |
| `BuiltInThemes.OneDark` | Inspired by Atom editor - muted, professional appearance |
| `BuiltInThemes.GruvboxDark` | Warm dark theme with earthy tones - easy on the eyes |
| `BuiltInThemes.CatppuccinMocha` | Pastel dark theme with soft, soothing colors |
| `BuiltInThemes.TokyoNight` | Modern theme with deep blues and purples |

#### Light Themes

| Theme | Description |
|-------|-------------|
| `BuiltInThemes.LightPlus` | VS Code Light+ theme - clean and bright |
| `BuiltInThemes.OneLight` | Balanced light theme - refined alternative to plain white |
| `BuiltInThemes.GruvboxLight` | Warm light variant of Gruvbox |
| `BuiltInThemes.CatppuccinLatte` | Light counterpart to Catppuccin Mocha |
| `BuiltInThemes.SolarizedLight` | Low-contrast theme designed to reduce eye strain |

### Dynamic Theme Switching

Since Dotty uses AOT compilation, runtime theme switching is limited but possible using compile-time expressions:

```csharp
// Time-based theme switching (day/night)
public IColorScheme? Colors => DateTime.Now.Hour is >= 6 and < 18 
    ? BuiltInThemes.LightPlus 
    : BuiltInThemes.DarkPlus;

// You can also use environment variables or other compile-time conditions
```

### Creating Custom Themes

#### Option 1: Inherit from ColorSchemeBase (Recommended)

```csharp
using Dotty.Abstractions.Themes;

public class MyCustomTheme : ColorSchemeBase
{
    public MyCustomTheme() : base(
        background: 0xFF1A1A1A,        // Dark gray
        foreground: 0xFFF0F0F0,        // Light gray
        ansiBlack: 0xFF000000,
        ansiRed: 0xFFFF0000,
        ansiGreen: 0xFF00FF00,
        ansiYellow: 0xFFFFFF00,
        ansiBlue: 0xFF0000FF,
        ansiMagenta: 0xFFFF00FF,
        ansiCyan: 0xFF00FFFF,
        ansiWhite: 0xFFFFFFFF,
        ansiBrightBlack: 0xFF555555,
        ansiBrightRed: 0xFFFF5555,
        ansiBrightGreen: 0xFF55FF55,
        ansiBrightYellow: 0xFFFFFF55,
        ansiBrightBlue: 0xFF5555FF,
        ansiBrightMagenta: 0xFFFF55FF,
        ansiBrightCyan: 0xFF55FFFF,
        ansiBrightWhite: 0xFFFFFFFF
    )
    {
    }
    
    // Optional: Override opacity (0-100, default is 100)
    public override byte Opacity => 95; // 5% transparent
}

// Use it in your config
public partial class MyConfig : IDottyConfig
{
    public IColorScheme? Colors => new MyCustomTheme();
}
}
```

#### Option 2: Implement IColorScheme Directly

```csharp
using Dotty.Abstractions.Config;

public class MySimpleTheme : IColorScheme
{
    public uint Background => 0xFF1A1A1A;
    public uint Foreground => 0xFFF0F0F0;
    
    public uint AnsiBlack => 0xFF000000;
    public uint AnsiRed => 0xFFFF0000;
    // ... implement all 16 ANSI colors
}
```

#### Option 3: Override Specific Colors from Base Theme

```csharp
using Dotty.Abstractions.Themes;

public partial class MyConfig : IDottyConfig
{
    // Use Dracula but with a pure black background
    public IColorScheme? Colors => new ThemeOverride(
        baseTheme: BuiltInThemes.Dracula,
        background: 0xFF000000,
        foreground: 0xFFFFFFFF
    );
}

// ThemeOverride helper class (from samples/Config.cs)
public class ThemeOverride : ColorSchemeBase
{
    public ThemeOverride(
        IColorScheme baseTheme,
        uint? background = null,
        uint? foreground = null,
        /* ... other color overrides ... */)
        : base(
            background ?? baseTheme.Background,
            foreground ?? baseTheme.Foreground,
            // ... pass through other colors with fallbacks
        )
    {
    }
}
```

### Color Format

Colors are specified in ARGB format (Alpha, Red, Green, Blue) as unsigned 32-bit integers:

```csharp
// Format: 0xAARRGGBB
uint color = 0xFFFF0000;      // Pure red (opaque)
uint color = 0xFF00FF00;      // Pure green (opaque)
uint color = 0xFF0000FF;      // Pure blue (opaque)
uint color = 0xFF1E1E1E;      // Dark gray (VS Code Dark+ background)
uint color = 0xFFD4D4D4;      // Light gray (VS Code Dark+ foreground)
```

For fully opaque colors, the alpha component (first byte) should be `0xFF`.

### Helper Methods

The `ColorSchemeBase` class provides utility methods:

```csharp
// Convert from hex string
uint color = ColorSchemeBase.FromHex("#FF5733");     // Returns 0xFFFF5733
uint color = ColorSchemeBase.FromHex("#80FF5733");   // Returns 0x80FF5733 (with alpha)

// Convert to hex string
string hex = ColorSchemeBase.ToHex(0xFFFF5733);      // Returns "#FFFF5733"

// Create from RGB components
uint color = ColorSchemeBase.FromRgb(255, 87, 51);   // Returns 0xFFFF5733
uint color = ColorSchemeBase.FromRgb(255, 87, 51, 128); // With alpha

// Calculate contrast ratio (for accessibility)
double contrast = ColorSchemeBase.CalculateContrastRatio(foreground, background);
// WCAG AA requires at least 4.5:1 for normal text
```

### Getting Themes by Name

```csharp
// Get theme by name (case-insensitive, with variants)
var theme = BuiltInThemes.GetByName("dracula");     // Returns Dracula theme
var theme = BuiltInThemes.GetByName("dark-plus");   // Returns DarkPlus theme
var theme = BuiltInThemes.GetByName("unknown");     // Returns DarkPlus (default)

// Access theme arrays
var allDarkThemes = BuiltInThemes.DarkThemes;       // All dark themes
var allLightThemes = BuiltInThemes.LightThemes;     // All light themes
var allThemes = BuiltInThemes.AllThemes;            // All themes
```

## Using Generated Configuration

The source generator creates a static `Config` class in the `Dotty.Generated` namespace:

```csharp
// Access configuration values
string fontFamily = Dotty.Generated.Config.FontFamily;
double fontSize = Dotty.Generated.Config.FontSize;
uint background = Dotty.Generated.Config.Background;

// Get Avalonia types using ConfigBridge
using Dotty.App.Configuration;

FontFamily family = ConfigBridge.GetFontFamily();
IBrush backgroundBrush = ConfigBridge.GetBackgroundBrush();
Color bgColor = ConfigBridge.GetBackgroundColor();

// Get ANSI colors
IBrush redBrush = ConfigBridge.GetAnsiColorBrush(1);   // ANSI Red
IBrush blueBrush = ConfigBridge.GetAnsiColorBrush(4);  // ANSI Blue

// Key bindings
TerminalAction? action = Dotty.Generated.Config.GetActionForKey(
    Avalonia.Input.Key.T, 
    Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift
);
```

## AOT Compatibility

All generated code is AOT-compatible:
- No reflection
- All values are constants or switch expressions
- No runtime code generation
- Fully trimmable

## Technical Details

### Source Generator Flow

1. **Compilation Scan**: The generator scans the compilation for classes implementing `IDottyConfig`
2. **Analysis**: If a config class is found, its properties are analyzed
3. **Code Generation**: Three files are generated:
   - `Dotty.Generated.Config.g.cs` - Static configuration class
   - `Dotty.Generated.ColorScheme.g.cs` - Color scheme record
   - `Dotty.Generated.KeyBindings.g.cs` - Key binding helpers and enum

### Generated Config Class

```csharp
public static class Config
{
    public static string FontFamily => "JetBrains Mono";
    public static double FontSize => 15.0;
    public static uint Background => 0xFF1E1E1E;  // From selected theme
    // ... more properties
    
    public static ColorScheme Colors => ColorScheme.Default;
    
    public static TerminalAction? GetActionForKey(Key key, KeyModifiers modifiers)
    {
        return (key, modifiers) switch
        {
            (Key.T, KeyModifiers.Control | KeyModifiers.Shift) => TerminalAction.NewTab,
            // ... more bindings
            _ => null
        };
    }
}
```

## Sample Configurations

See `/home/dom/projects/dotnet-term/samples/Config.cs` for complete examples:
- Using built-in themes
- Time-based theme switching
- Creating custom themes
- Overriding theme colors
- Custom key bindings
- Cursor settings

## Troubleshooting

### Config changes not reflecting

1. Ensure your config class implements `IDottyConfig`
2. Make the class `partial`
3. Rebuild the project to trigger source generation

### IntelliSense not working

The generated code will have IntelliSense after the first successful build. For full LSP support:

1. Open the `~/.config/dotty/Dotty.UserConfig/` folder in your IDE (not just the single file)
2. Ensure the `Dotty.Abstractions` NuGet package is restored: `dotnet restore`
3. The source generator runs at compile-time and the generated files are included in the compilation

If IntelliSense is still not working:
- Check that your IDE supports C# LSP (VS Code with C# extension, Rider, etc.)
- Verify the `.csproj` file has the NuGet package reference
- Try running `dotnet build` once from the `Dotty.UserConfig` directory

### AOT build errors

Ensure you're using constant values (literals) in your config class. The source generator needs to be able to extract these values at build time.

### Theme colors look wrong

- Make sure colors are in ARGB format (0xAARRGGBB)
- For fully opaque colors, use 0xFF as the alpha component
- Check that your theme implements all 18 color properties (Background, Foreground, 16 ANSI colors)

---

## See Also

- [Themes Guide](THEMES.md) - Detailed theme documentation with color swatches
- [Architecture Overview](architecture.md) - Dotty's technical architecture
- [Sample Configurations](/home/dom/projects/dotnet-term/samples/Config.cs) - Complete configuration examples
