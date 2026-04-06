# Dotty Source Generator Configuration System - Implementation Summary

## Overview

Successfully implemented a C# Source Generator-based configuration system for the Dotty terminal emulator. The system allows users to customize terminal settings using C# configuration files that are compiled into the application at build time.

## Files Created

### 1. Source Generator Project
**Path**: `/home/dom/projects/dotnet-term/src/Dotty.Config.SourceGenerator/`

- **Dotty.Config.SourceGenerator.csproj** - Project file targeting .NET Standard 2.0 with Microsoft.CodeAnalysis.CSharp packages
- **ConfigGenerator.cs** - The actual source generator implementing `IIncrementalGenerator` that:
  - Scans for `IDottyConfig` implementations in the compilation
  - Generates three source files:
    - `Dotty.Generated.Config.g.cs` - Static Config class with all configuration values
    - `Dotty.Generated.ColorScheme.g.cs` - ColorScheme record with ANSI colors
    - `Dotty.Generated.KeyBindings.g.cs` - TerminalAction enum and key binding helpers

### 2. Configuration Abstractions
**Path**: `/home/dom/projects/dotnet-term/src/Dotty.Abstractions/Config/`

- **IDottyConfig.cs** - Main configuration interface with properties for:
  - Font settings (family, size, padding)
  - Color scheme
  - Key bindings
  - Scrollback lines
  - Window dimensions
  - Cursor settings
  - UI colors (selection, tab bar)
  
- **IColorScheme.cs** - Color scheme interface with 16 ANSI colors + background/foreground

- **IKeyBindings.cs** - Key bindings interface with Key/KeyModifiers enums and GetAction method

- **TerminalAction.cs** - Enum defining all available terminal actions (NewTab, CloseTab, Copy, Paste, etc.)

- **IWindowDimensions.cs** - Window configuration (columns, rows, title, fullscreen)

- **ICursorSettings.cs** - Cursor settings (shape, blink, color)

### 3. Dotty.App Integration
**Path**: `/home/dom/projects/dotnet-term/src/Dotty.App/Configuration/`

- **DefaultConfig.cs** - Default implementation of IDottyConfig with values matching the original hardcoded defaults
- **ConfigBridge.cs** - Helper class to convert generated configuration values to Avalonia types (Color, Brush, Thickness, FontFamily)

### 4. Sample Configuration
**Path**: `/home/dom/projects/dotnet-term/samples/Config.cs`

Complete sample showing how users can customize:
- Dark theme (Monokai-inspired)
- Light theme
- Custom key bindings
- Window dimensions
- Cursor settings

### 5. Documentation
**Path**: `/home/dom/projects/dotnet-term/docs/CONFIGURATION.md`

Comprehensive documentation covering:
- Quick start guide
- All configuration options
- Using generated configuration
- AOT compatibility
- Technical details

### 6. Updated Files

- **Dotty.sln** - Added Dotty.Config.SourceGenerator project reference
- **Dotty.App.csproj** - Added source generator reference with `ReferenceOutputAssembly=false` and `OutputItemType=Analyzer`
- **Services/Defaults.cs** - Updated to use generated config values
- **Views/MainWindow.axaml.cs** - Updated to use config for title, background color, and tab destroy delay
- **Controls/Canvas/TerminalCanvas.cs** - Updated to use config for font, padding, and selection color
- **Controls/TerminalGrid.axaml.cs** - Updated to use config for padding and cursor blink interval

## Configuration Values Generated

### Font Settings
- **FontFamily**: "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Cascadia Code, Consolas, Liberation Mono, Noto Sans Mono, monospace, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols"
- **FontSize**: 15.0
- **CellPadding**: 1.5
- **ContentPadding**: 0, 0, 0, 0

### Color Settings
- **Background**: 0xF2000000 (near-black with alpha)
- **Foreground**: 0xFFD4D4D4 (light gray)
- **SelectionColor**: 0xA03385DB (blue selection)
- **TabBarBackgroundColor**: 0xFF1A1A1A (dark gray)

### Terminal Settings
- **ScrollbackLines**: 10000
- **InactiveTabDestroyDelayMs**: 5000

### Window Settings
- **InitialColumns**: 80
- **InitialRows**: 24
- **WindowTitle**: "Dotty"
- **StartFullscreen**: false

### Cursor Settings
- **CursorShape**: "Block"
- **CursorBlink**: true
- **CursorBlinkIntervalMs**: 500
- **CursorColor**: null (uses foreground)
- **CursorShowUnfocused**: false

### ANSI 16-Color Palette
Standard ANSI colors from SgrColor.cs (0-15) including all standard and bright variants.

## Key Bindings Generated

The source generator creates an efficient switch expression for key handling:

```csharp
public static TerminalAction? GetActionForKey(Key key, KeyModifiers modifiers)
{
    return (key, modifiers) switch
    {
        (Key.T, KeyModifiers.Control | KeyModifiers.Shift) => TerminalAction.NewTab,
        (Key.W, KeyModifiers.Control | KeyModifiers.Shift) => TerminalAction.CloseTab,
        (Key.Tab, KeyModifiers.Control) => TerminalAction.NextTab,
        (Key.C, KeyModifiers.Control | KeyModifiers.Shift) => TerminalAction.Copy,
        (Key.V, KeyModifiers.Control | KeyModifiers.Shift) => TerminalAction.Paste,
        // ... more bindings
        _ => null
    };
}
```

Default key bindings include:
- **Ctrl+Shift+T**: New Tab
- **Ctrl+Shift+W**: Close Tab
- **Ctrl+Tab**: Next Tab
- **Ctrl+Shift+Tab**: Previous Tab
- **Ctrl+Shift+C**: Copy
- **Ctrl+Shift+V**: Paste
- **Ctrl+(1-9)**: Switch to tab 1-9
- **F11**: Toggle Fullscreen
- **Ctrl+Shift+F**: Search
- **Ctrl+Shift+Q**: Quit

## AOT Compatibility

The implementation is fully AOT-compatible:
- ✅ No runtime reflection
- ✅ All configuration values are compile-time constants
- ✅ Key bindings use switch expressions (no dictionary lookups)
- ✅ Fully trimmable
- ✅ Works with PublishAot=true

## Usage

### For Users

1. Create a C# file implementing `IDottyConfig`:
```csharp
using Dotty.Abstractions.Config;

public partial class MyConfig : IDottyConfig
{
    public string? FontFamily => "Fira Code, monospace";
    public double? FontSize => 14.0;
    public IColorScheme? Colors => new MyDarkTheme();
}
```

2. Rebuild the project - the source generator will automatically pick up the config

### For Developers

Access generated values anywhere in the codebase:
```csharp
using Dotty.Generated;
using Dotty.App.Configuration;

// Raw values
string fontFamily = Config.FontFamily;
uint background = Config.Background;

// Avalonia types
FontFamily family = ConfigBridge.GetFontFamily();
IBrush brush = ConfigBridge.GetBackgroundBrush();
Color color = ConfigBridge.GetBackgroundColor();

// Key bindings
var action = Config.GetActionForKey(key, modifiers);
```

## Build Verification

Successfully built and tested:
```
Build succeeded.
    2 Warning(s)
    0 Error(s)
```

Application runs correctly with generated configuration:
- Font: JetBrainsMono Nerd Font Mono@15.0px ✓
- Background: Near-black with alpha ✓
- All key bindings functional ✓

## Architecture

```
User Config (IDottyConfig impl)
         ↓
Source Generator (ConfigGenerator.cs)
         ↓
Generated Code (Dotty.Generated namespace)
         ↓
Application Code (via ConfigBridge)
         ↓
AOT-Compiled Binary
```

## Future Enhancements

Potential improvements:
1. Multi-target support (detect and merge multiple config classes)
2. Configuration validation (emit diagnostics for invalid values)
3. Hot reload support for development
4. JSON serialization helpers for config import/export
5. Environment variable overrides at runtime

## Conclusion

The Source Generator-based configuration system is fully implemented and working. It provides:
- Type-safe configuration
- Compile-time validation
- AOT compatibility
- Excellent IntelliSense support
- Zero runtime overhead
