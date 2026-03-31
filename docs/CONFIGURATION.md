# Dotty Configuration System

Dotty uses a C# Source Generator-based configuration system that allows users to customize terminal settings using C# code that gets compiled into the application at build time.

## Overview

The configuration system consists of:

1. **Configuration Interfaces** (`Dotty.Abstractions.Config`) - Define the contract for configuration
2. **Source Generator** (`Dotty.Config.SourceGenerator`) - Scans for IDottyConfig implementations and generates a static `Config` class
3. **Generated Code** (`Dotty.Generated` namespace) - Static configuration values available at runtime
4. **ConfigBridge** (`Dotty.App.Configuration`) - Helper to convert generated values to Avalonia types

## Quick Start

### 1. Using Default Configuration

If you don't provide a custom configuration, Dotty will use sensible defaults that match the current hardcoded values:

- Font: JetBrains Mono, 15pt
- Background: Near-black (#F2000000)
- Foreground: Light gray (#D4D4D4)
- ANSI colors: Standard 16-color palette
- Scrollback: 10000 lines

### 2. Creating a Custom Configuration

Create a C# file that implements `IDottyConfig`:

```csharp
using Dotty.Abstractions.Config;

namespace MyDottyConfig;

public partial class MyConfig : IDottyConfig
{
    // Font settings
    public string? FontFamily => "Fira Code, monospace";
    public double? FontSize => 14.0;
    
    // Color scheme
    public IColorScheme? Colors => new MyDarkTheme();
    
    // Key bindings
    public IKeyBindings? KeyBindings => new MyKeyBindings();
    
    // Other settings...
    public int? ScrollbackLines => 50000;
}

public class MyDarkTheme : IColorScheme
{
    public uint Background => 0xFF181818;
    public uint Foreground => 0xFFF8F8F2;
    // ... ANSI colors
}
```

### 3. Configuration Locations

The source generator will scan for IDottyConfig implementations in:
- Your project directory
- `~/.config/dotty/Config.cs` (user config directory)
- Any C# file in the compilation that implements IDottyConfig

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
    public static uint Background => 0xF2000000;
    // ... more properties
    
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
- Dark theme (Monokai-inspired)
- Light theme
- Custom key bindings
- Cursor settings

## Troubleshooting

### Config changes not reflecting

1. Ensure your config class implements `IDottyConfig`
2. Make the class `partial`
3. Rebuild the project to trigger source generation

### IntelliSense not working

The generated code will have IntelliSense after the first successful build. The source generator runs at compile-time and the generated files are included in the compilation.

### AOT build errors

Ensure you're using constant values (literals) in your config class. The source generator needs to be able to extract these values at build time.

---

## Advanced Features (Coming Soon)

The Dotty configuration system is built on a **Source Generator architecture** that enables powerful future enhancements while maintaining AOT compatibility. While the current system provides all essential features for customizing your terminal, several advanced capabilities are planned for future releases.

### Future Possibilities

**Conditional & Context-Aware Configs:**
- Environment variable-based configuration values
- Time-based theme switching (automatic day/night modes)
- OS-specific defaults (macOS vs Linux font preferences)
- Per-host configurations for different machines

**Advanced Keybindings:**
- Conditional bindings that adapt when running under tmux/screen
- Chord/key sequences (leader key style like tmux or vim)
- Mode-based bindings (vim-style normal/insert modes)
- Application-specific keymaps

**Config Composition & Inheritance:**
- Inherit from preset configurations (Dark Modern, Light Modern, etc.)
- Mix multiple configuration sources together
- Profile-based configs (dev, server, presentation modes)
- Shell-specific profiles (zsh, fish, bash optimizations)

**Validation & Safety:**
- Compile-time validation attributes
- Range checking for numeric values
- Font existence validation at build time
- Color format validation
- Custom validation rules

**Advanced Font Handling:**
- Font chains with specific Unicode range fallbacks
- Emoji and CJK-specific font handling
- Dynamic ligature detection
- OpenType feature toggles (ss01, calt, etc.)

**Smart Defaults:**
- Auto-detecting optimal font size based on screen DPI
- Shell integration profiles with automatic detection
- Conditional GPU acceleration based on environment
- Dynamic scrollback sizing based on available RAM

### Documentation

For detailed information about these future enhancements:

- **[Configuration Advanced](CONFIGURATION_ADVANCED.md)** - Comprehensive documentation of all potential future features with code examples
- **[Configuration Roadmap](CONFIGURATION_ROADMAP.md)** - Prioritized roadmap with implementation timelines and complexity assessments

### Important Notes

> **Current System is Production-Ready**: The existing configuration system is stable, fully supported, and will remain compatible with all future enhancements. These advanced features are additive - you can start simple and gradually adopt new capabilities.

> **AOT Compatibility Maintained**: All proposed features are designed to work with Ahead-of-Time (AOT) compilation. The Source Generator evaluates expressions at build time to produce efficient, trimmable code.

> **No Timeline Commitments**: The roadmap documents possibilities, not commitments. Features will be added based on user needs and architectural readiness.

> **Simple Config is Stable**: Even as advanced features are added, the simple property-based configuration style you're using today will continue to work exactly as it does now.

---

## See Also

- [Architecture Overview](architecture.md) - Dotty's technical architecture
- [Sample Configurations](/home/dom/projects/dotnet-term/samples/Config.cs) - Complete configuration examples
