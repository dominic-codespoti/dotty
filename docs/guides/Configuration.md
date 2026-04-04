# Dotty Configuration Guide

Welcome to the Dotty Configuration Guide! This guide will help you customize your terminal experience, from simple theme changes to advanced custom color schemes.

## Table of Contents

1. [Quick Start](#quick-start)
2. [Configuration Location](#configuration-location)
3. [Available Options](#available-options)
4. [Theme Reference](#theme-reference)
5. [Advanced Examples](#advanced-examples)
6. [Troubleshooting](#troubleshooting)
7. [Rebuild Instructions](#rebuild-instructions)

---

## Quick Start

### Creating Your First Custom Config

Dotty automatically creates a default configuration on first run. To customize it:

1. **Locate your config file:**
   - **Linux/macOS**: `~/.config/dotty/Dotty.UserConfig/Config.cs`
   - **Windows**: `%APPDATA%/dotty/Dotty.UserConfig/Config.cs`

2. **Open in your IDE** (recommended for IntelliSense):
   ```bash
   # VS Code:
   code ~/.config/dotty/Dotty.UserConfig/
   
   # JetBrains Rider:
   rider ~/.config/dotty/Dotty.UserConfig/
   ```

3. **Make a simple change** - uncomment and modify a property:
   ```csharp
   // Change the theme
   public IColorScheme? Colors => BuiltInThemes.Dracula;
   
   // Increase font size
   public double? FontSize => 16.0;
   ```

4. **Rebuild Dotty** to apply changes (see [Rebuild Instructions](#rebuild-instructions))

That's it! Your terminal will now use Dracula theme with a larger font size.

---

## Configuration Location

### Where to Put the Config.cs File

Dotty uses the **XDG Base Directory Specification** on Linux/macOS and standard Windows paths:

| Platform | Location |
|----------|----------|
| **Linux/macOS** | `~/.config/dotty/Dotty.UserConfig/Config.cs` |
| **Windows** | `%APPDATA%/dotty/Dotty.UserConfig/Config.cs` |

### Project Structure

```
~/.config/dotty/Dotty.UserConfig/
├── Dotty.UserConfig.csproj    # Project file with NuGet reference
├── Config.cs                  # Your configuration (edit this!)
└── obj/                       # Build artifacts (auto-generated)
```

### What the Config.cs Contains

The generated file includes:
- Sensible defaults (DarkPlus theme, JetBrains Mono 15pt)
- Helpful comments explaining all options
- Pre-written examples you can uncomment
- Full IntelliSense support via the NuGet package

> **Tip:** The Dotty.Abstractions NuGet package is already referenced in your project, giving you full IntelliSense and error checking in your IDE!

---

## Available Options

Here's a complete reference of all configurable properties with examples:

### Font Settings

Control how text appears in your terminal.

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    // Font family stack - comma-separated with fallbacks
    // First available font is used
    public string? FontFamily => 
        "Fira Code, JetBrains Mono, Cascadia Code, monospace";
    
    // Font size in points (default: 15.0)
    public double? FontSize => 14.0;
    
    // Cell padding in pixels (default: 1.5)
    // Adds spacing around each character cell
    public double? CellPadding => 2.0;
    
    // Content padding around terminal area
    // Format: new Thickness(Left, Top, Right, Bottom)
    public Thickness? ContentPadding => new Thickness(8.0, 8.0, 8.0, 8.0);
    
    // Or use uniform padding for all sides:
    // public Thickness? ContentPadding => new Thickness(8.0);
    
    // Or specify horizontal and vertical separately:
    // public Thickness? ContentPadding => new Thickness(8.0, 4.0);
}
```

**Recommended Font Stacks:**
- **Nerd Fonts** (with icons): `"JetBrainsMono Nerd Font, FiraCode Nerd Font, monospace"`
- **Standard coding fonts**: `"Fira Code, JetBrains Mono, Cascadia Code, Consolas, monospace"`
- **Fallback**: `"monospace"` (system default)

### Theme Selection

Choose from 11 built-in themes or create your own.

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    // Use a built-in theme (easiest option)
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    
    // Alternative syntax with comments:
    // public IColorScheme? Colors => BuiltInThemes.Dracula;
    // public IColorScheme? Colors => BuiltInThemes.TokyoNight;
    // public IColorScheme? Colors => BuiltInThemes.GruvboxDark;
}
```

**Available Built-in Themes:**

#### Dark Themes
| Theme | Description | Best For |
|-------|-------------|----------|
| `DarkPlus` | VS Code: Dark+ theme (default) | Balanced, great readability |
| `Dracula` | Vibrant purple with bright colors | Eye-catching, popular |
| `OneDark` | Atom-inspired, muted colors | Professional, subtle |
| `GruvboxDark` | Warm, earthy tones | Long coding sessions |
| `CatppuccinMocha` | Soothing pastels | Easy on the eyes |
| `TokyoNight` | Deep blues and purples | Modern aesthetic |

#### Light Themes
| Theme | Description | Best For |
|-------|-------------|----------|
| `LightPlus` | VS Code: Light+ | Clean, bright environments |
| `OneLight` | One Dark counterpart | Balanced light theme |
| `GruvboxLight` | Warm light variant | Light gruvbox fans |
| `CatppuccinLatte` | Pastel light theme | Easy viewing |
| `SolarizedLight` | Low contrast, reduced eye strain | Sensitive eyes |

### Cursor Settings

Customize your cursor appearance and behavior.

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    public ICursorSettings? Cursor => new CursorSettings
    {
        // Shape options: Block, Beam, or Underline
        Shape = CursorShape.Beam,
        
        // Enable blinking (true/false)
        Blink = true,
        
        // Blink interval in milliseconds
        BlinkIntervalMs = 600,
        
        // Cursor color (ARGB format) - null uses foreground color
        Color = 0xFF00FF00,  // Bright green
        
        // Show cursor when window is not focused
        ShowUnfocused = false
    };
}
```

**Cursor Shapes:**
- `Block` - Fills the entire cell (traditional terminal style)
- `Beam` - Vertical line (modern text editor style)
- `Underline` - Horizontal line at bottom

**Color Format:**
- `0xFF00FF00` - ARGB format (Alpha, Red, Green, Blue)
- `0xFF` prefix = fully opaque
- `null` = uses the terminal's foreground color

### Window Settings

Control initial window size and appearance.

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    public IWindowDimensions? InitialDimensions => new WindowDimensions
    {
        // Size in terminal columns and rows
        Columns = 120,
        Rows = 40,
        
        // Or specify exact pixel dimensions (optional)
        // WidthPixels = 1920,
        // HeightPixels = 1080,
        
        // Start in fullscreen mode
        StartFullscreen = false,
        
        // Window title
        Title = "My Terminal"
    };
    
    // Window transparency effects (platform-dependent)
    // Options: None, Transparent, Blur, Acrylic
    public TransparencyLevel? Transparency => TransparencyLevel.None;
    
    // Window opacity: 0-100 (100 = fully opaque, 0 = fully transparent)
    public byte? WindowOpacity => 100;
}
```

**Transparency Levels:**
- `None` - Solid background (default)
- `Transparent` - Simple transparency (see-through without blur)
- `Blur` - Background blur effect
- `Acrylic` - Full acrylic/glass effect with noise texture

> **Note:** True acrylic/blur effects depend on your platform and compositor support.

### Terminal Settings

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    // Number of scrollback lines to keep in memory
    // Higher values use more RAM but allow more history
    public int? ScrollbackLines => 50000;
    
    // Delay before destroying inactive tab visuals (milliseconds)
    // Lower values save memory but may cause flickering when switching tabs
    public int? InactiveTabDestroyDelayMs => 10000;
}
```

**Scrollback Recommendations:**
- `10000` - Default, good for most users
- `50000` - Heavy terminal users
- `100000` - Power users with plenty of RAM
- `5000` - Minimal memory usage

### UI Colors

```csharp
public partial class MyDottyConfig : IDottyConfig
{
    // Selection highlight color (ARGB format)
    // Default: 0xA03385DB (semi-transparent blue)
    public uint? SelectionColor => 0xA03385DB;
    
    // Tab bar background color (ARGB format)
    // Default: 0xFF1A1A1A (dark gray)
    public uint? TabBarBackgroundColor => 0xFF1A1A1A;
}
```

**ARGB Format Guide:**
- First 2 characters: Alpha (transparency) - `FF` = opaque, `00` = transparent
- Next 2: Red intensity
- Next 2: Green intensity
- Last 2: Blue intensity
- Example: `0xFF00FF00` = opaque bright green

---

## Theme Reference

### Built-in Theme Colors

#### Dark Themes

##### DarkPlus (Default)
```
Background: #1E1E1E    Foreground: #D4D4D4
Black:      #000000    Red:        #CD3131
Green:      #0DBC79    Yellow:     #E5E510
Blue:       #2472C8    Magenta:    #BC3FBC
Cyan:       #11A8CD    White:      #E5E5E5
Bright Black:  #666666    Bright Red:    #F14C4C
Bright Green:  #23D18B    Bright Yellow: #F5F543
Bright Blue:   #3B8EEA    Bright Magenta:#D670D6
Bright Cyan:   #29B8DB    Bright White:  #FFFFFF
```

##### Dracula
```
Background: #282A36    Foreground: #F8F8F2
Black:      #21222C    Red:        #FF5555
Green:      #50FA7B    Yellow:     #F1FA8C
Blue:       #BD93F9    Magenta:    #FF79C6
Cyan:       #8BE9FD    White:      #F8F8F2
Bright Black:  #6272A4    Bright Red:    #FF6E6E
Bright Green:  #69FF94    Bright Yellow: #FFFFA5
Bright Blue:   #D6ACFF    Bright Magenta:#FF92DF
Bright Cyan:   #A4FFFF    Bright White:  #FFFFFF
```

##### OneDark
```
Background: #282C34    Foreground: #ABB2BF
Black:      #282C34    Red:        #E06C75
Green:      #98C379    Yellow:     #E5C07B
Blue:       #61AFEF    Magenta:    #C678DD
Cyan:       #56B6C2    White:      #ABB2BF
Bright Black:  #5C6370    Bright Red:    #FF7A85
Bright Green:  #B5E090    Bright Yellow: #FFD58F
Bright Blue:   #8CCBFF    Bright Magenta:#E599FF
Bright Cyan:   #89DDFF    Bright White:  #FFFFFF
```

##### GruvboxDark
```
Background: #282828    Foreground: #EBDBB2
Black:      #282828    Red:        #CC241D
Green:      #98971A    Yellow:     #D79921
Blue:       #458588    Magenta:    #B16286
Cyan:       #689D6A    White:      #A89984
Bright Black:  #928374    Bright Red:    #FB4934
Bright Green:  #B8BB26    Bright Yellow: #FABD2F
Bright Blue:   #83A598    Bright Magenta:#D3869B
Bright Cyan:   #8EC07C    Bright White:  #FBF1C7
```

##### CatppuccinMocha
```
Background: #1E1E2E    Foreground: #CDD6F4
Black:      #45475A    Red:        #F38BA8
Green:      #A6E3A1    Yellow:     #F9E2AF
Blue:       #89B4FA    Magenta:    #F5C2E7
Cyan:       #94E2D5    White:      #BAC2DE
Bright Black:  #585B70    Bright Red:    #FFA1C1
Bright Green:  #B9F0B4    Bright Yellow: #FFEFA1
Bright Blue:   #A3C9FF    Bright Magenta:#FFDBF7
Bright Cyan:   #AAEFDE    Bright White:  #FFFFFF
```

##### TokyoNight
```
Background: #1A1B26    Foreground: #A9B1D6
Black:      #414868    Red:        #F7768E
Green:      #73DACA    Yellow:     #E0AF68
Blue:       #7AA2F7    Magenta:    #BB9AF7
Cyan:       #7DCFFF    White:      #787C99
Bright Black:  #565F89    Bright Red:    #FF8EA0
Bright Green:  #8BECC8    Bright Yellow: #FFD88A
Bright Blue:   #9AB8FF    Bright Magenta:#D4BFFF
Bright Cyan:   #9ED7FF    Bright White:  #C0CAF5
```

#### Light Themes

##### LightPlus
```
Background: #FFFFFF    Foreground: #000000
Black:      #000000    Red:        #CD3131
Green:      #00BC00    Yellow:     #949800
Blue:       #0451A5    Magenta:    #BC05BC
Cyan:       #0598BC    White:      #555555
Bright Black:  #666666    Bright Red:    #F14C4C
Bright Green:  #16C60C    Bright Yellow: #B5BA00
Bright Blue:   #0A6BC8    Bright Magenta:#BC05BC
Bright Cyan:   #0598BC    Bright White:  #A5A5A5
```

##### OneLight
```
Background: #FAFAFA    Foreground: #383A42
Black:      #383A42    Red:        #E45649
Green:      #50A14F    Yellow:     #C18401
Blue:       #4078F2    Magenta:    #A626A4
Cyan:       #0184BC    White:      #A0A1A7
Bright Black:  #4F525D    Bright Red:    #FF6E66
Bright Green:  #6BC468    Bright Yellow: #D9940F
Bright Blue:   #6394FF    Bright Magenta:#C053BE
Bright Cyan:   #38B7F0    Bright White:  #FFFFFF
```

##### GruvboxLight
```
Background: #FBF1C7    Foreground: #3C3836
Black:      #3C3836    Red:        #CC241D
Green:      #98971A    Yellow:     #D79921
Blue:       #458588    Magenta:    #B16286
Cyan:       #689D6A    White:      #7C6F64
Bright Black:  #928374    Bright Red:    #9D0006
Bright Green:  #79740E    Bright Yellow: #B57614
Bright Blue:   #076678    Bright Magenta:#8F3F71
Bright Cyan:   #427B58    Bright White:  #282828
```

##### CatppuccinLatte
```
Background: #EFF1F5    Foreground: #4C4F69
Black:      #5C5F77    Red:        #D20F39
Green:      #40A02B    Yellow:     #DF8E1D
Blue:       #1E66F5    Magenta:    #EA76CB
Cyan:       #179299    White:      #ACB0BE
Bright Black:  #6C6F85    Bright Red:    #EE324C
Bright Green:  #56C150    Bright Yellow: #F0AB39
Bright Blue:   #4C89FF    Bright Magenta:#F495DA
Bright Cyan:   #2AB6B2    Bright White:  #CCD0DA
```

##### SolarizedLight
```
Background: #FDF6E3    Foreground: #657B83
Black:      #073642    Red:        #DC322F
Green:      #859900    Yellow:     #B58900
Blue:       #268BD2    Magenta:    #D33682
Cyan:       #2AA198    White:      #EEE8D5
Bright Black:  #002B36    Bright Red:    #CB4B16
Bright Green:  #586E75    Bright Yellow: #657B83
Bright Blue:   #839496    Bright Magenta:#6C71C4
Bright Cyan:   #93A1A1    Bright White:  #FDF6E3
```

---

## Advanced Examples

### Custom Color Schemes

Create your own theme by extending `ColorSchemeBase`:

```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

public partial class MyDottyConfig : IDottyConfig
{
    // Use your custom theme
    public IColorScheme? Colors => new MyOceanTheme();
}

/// <summary>
/// Custom ocean-inspired theme
/// </summary>
public class MyOceanTheme : ColorSchemeBase
{
    public MyOceanTheme() : base(
        background: 0xFF001A33,      // Deep ocean blue
        foreground: 0xFFCCDDFF,      // Soft light blue-white
        ansiBlack: 0xFF001122,       // Dark navy
        ansiRed: 0xFFFF6B6B,         // Coral red
        ansiGreen: 0xFF4ECDC4,       // Seafoam green
        ansiYellow: 0xFFFFE66D,      // Sunlight yellow
        ansiBlue: 0xFF4A90E2,        // Ocean blue
        ansiMagenta: 0xFFFF9FF3,     // Pink coral
        ansiCyan: 0xFF7FDBDA,        // Turquoise
        ansiWhite: 0xFFCCDDFF,       // Light blue-white
        ansiBrightBlack: 0xFF334455, // Medium navy
        ansiBrightRed: 0xFFFF8E8E,   // Light coral
        ansiBrightGreen: 0xFF7FDBDA, // Light seafoam
        ansiBrightYellow: 0xFFFFF5A5,// Bright sunlight
        ansiBrightBlue: 0xFF7ABAF7,  // Light ocean blue
        ansiBrightMagenta: 0xFFFFC9F9,// Light pink
        ansiBrightCyan: 0xFFB4F0EF,  // Light turquoise
        ansiBrightWhite: 0xFFFFFFFF  // Pure white
    )
    {
    }
}
```

### Theme Override (Customizing a Built-in Theme)

Override specific colors from a built-in theme:

```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

public partial class MyDottyConfig : IDottyConfig
{
    // DarkPlus theme with custom background and foreground
    public IColorScheme? Colors => new ThemeOverride(
        baseTheme: BuiltInThemes.DarkPlus,
        background: 0xFF000000,  // Pure black background
        foreground: 0xFFFFFFFF   // Pure white foreground
    );
}

/// <summary>
/// Theme override helper - allows customizing specific colors from a base theme
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
            ansiBlue ?? baseTheme.AnsiBlue,
            ansiBrightMagenta ?? baseTheme.AnsiBrightMagenta,
            ansiBrightCyan ?? baseTheme.AnsiBrightCyan,
            ansiBrightWhite ?? baseTheme.AnsiBrightWhite
        )
    {
    }
}
```

### Transparency Configuration

Make your terminal window partially transparent:

```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

public partial class MyDottyConfig : IDottyConfig
{
    // Use a theme with transparency support
    public IColorScheme? Colors => new TranslucentDracula();
    
    // Or set window-wide opacity
    public byte? WindowOpacity => 85;  // 85% opaque = 15% transparent
    
    // Add blur effect (platform-dependent)
    public TransparencyLevel? Transparency => TransparencyLevel.Blur;
}

/// <summary>
/// Dracula theme with 85% opacity (15% transparent)
/// </summary>
public class TranslucentDracula : DraculaTheme
{
    public override byte Opacity => 85;
}

/// <summary>
/// Theme that changes opacity based on time of day
/// More transparent at night (90%), fully opaque during day
/// </summary>
public class TimeBasedOpacityTheme : DarkPlusTheme
{
    public override byte Opacity => 
        DateTime.Now.Hour is >= 20 or < 6 ? 90 : 100;
}
```

**Recommended Opacity Values:**
- `100` - Fully opaque (default)
- `95` - Subtle transparency (5%)
- `90` - Light transparency (10%)
- `85` - Moderate transparency (15%, good balance)
- `70` - Heavy transparency (30%, advanced users only)

> **Warning:** Low opacity values (< 80) may hurt readability. Use with strong background colors only.

### Font Stack Configuration

Set up a comprehensive font stack with fallbacks:

```csharp
using Dotty.Abstractions.Config;

namespace Dotty.UserConfig;

public partial class MyDottyConfig : IDottyConfig
{
    // Comprehensive font stack with programming ligatures and icon support
    public string? FontFamily => 
        "JetBrainsMono Nerd Font Mono, " +     // Primary: Nerd Font with icons
        "FiraCode Nerd Font, " +                // Fallback 1: Fira Code
        "Cascadia Code, " +                     // Fallback 2: Cascadia
        "SF Mono, " +                           // Fallback 3: macOS system
        "Ubuntu Mono, " +                       // Fallback 4: Ubuntu
        "Consolas, " +                          // Fallback 5: Windows
        "Liberation Mono, " +                   // Fallback 6: Linux
        "monospace";                            // Ultimate fallback
    
    // Simpler stack for standard coding fonts
    // public string? FontFamily => 
    //     "Fira Code, JetBrains Mono, Cascadia Code, Consolas, monospace";
    
    // System default only (fastest)
    // public string? FontFamily => "monospace";
}
```

### Complete Configuration Example

Here's a complete, production-ready configuration:

```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

/// <summary>
/// Complete custom Dotty configuration
/// </summary>
public partial class MyDottyConfig : IDottyConfig
{
    // ============================================
    // FONT SETTINGS
    // ============================================
    
    // Nerd Font stack for icons and ligatures
    public string? FontFamily => 
        "JetBrainsMono Nerd Font Mono, JetBrainsMono Nerd Font, " +
        "FiraCode Nerd Font, Cascadia Code, monospace";
    
    // Comfortable size for 1080p displays
    public double? FontSize => 14.0;
    
    // Slight cell padding for readability
    public double? CellPadding => 1.5;
    
    // 8px padding on all sides
    public Thickness? ContentPadding => new Thickness(8.0);
    
    // ============================================
    // THEME
    // ============================================
    
    // Tokyo Night with subtle transparency
    public IColorScheme? Colors => new TranslucentTokyoNight();
    
    // ============================================
    // WINDOW
    // ============================================
    
    // Start with 120 columns, 40 rows
    public IWindowDimensions? InitialDimensions => new WindowDimensions
    {
        Columns = 120,
        Rows = 40,
        Title = "Terminal"
    };
    
    // Enable blur effect
    public TransparencyLevel? Transparency => TransparencyLevel.Blur;
    
    // ============================================
    // CURSOR
    // ============================================
    
    public ICursorSettings? Cursor => new CursorSettings
    {
        Shape = CursorShape.Beam,
        Blink = true,
        BlinkIntervalMs = 550,
        Color = 0xFF7AA2F7,  // Tokyo Night blue
        ShowUnfocused = true
    };
    
    // ============================================
    // TERMINAL
    // ============================================
    
    // 50,000 lines of scrollback
    public int? ScrollbackLines => 50000;
    
    // 10 second delay for tab cleanup
    public int? InactiveTabDestroyDelayMs => 10000;
}

/// <summary>
/// Tokyo Night theme with 90% opacity
/// </summary>
public class TranslucentTokyoNight : TokyoNightTheme
{
    public override byte Opacity => 90;
}
```

---

## Troubleshooting

### Common Issues and Solutions

#### Issue: Changes Not Applied After Editing Config.cs

**Symptom:** You edited `Config.cs` but the terminal looks the same.

**Solution:** Remember to rebuild Dotty after config changes:
```bash
# Navigate to the Dotty source directory
cd /path/to/dotty

# Rebuild
dotnet build

# Or rebuild completely
dotnet clean && dotnet build
```

#### Issue: IntelliSense Not Working

**Symptom:** No autocomplete or error detection in your IDE.

**Solutions:**
1. Make sure you opened the entire folder, not just the file:
   ```bash
   code ~/.config/dotty/Dotty.UserConfig/  # Correct
   code ~/.config/dotty/Dotty.UserConfig/Config.cs  # Wrong
   ```

2. Check that the project restored successfully:
   ```bash
   cd ~/.config/dotty/Dotty.UserConfig/
   dotnet restore
   ```

3. Verify the NuGet package is referenced in `.csproj`:
   ```xml
   <PackageReference Include="Dotty.Abstractions" Version="0.1.0" />
   ```

#### Issue: Config Not Found

**Symptom:** Dotty doesn't recognize your configuration.

**Solutions:**
1. Check the config location:
   ```bash
   # Linux/macOS
   ls -la ~/.config/dotty/Dotty.UserConfig/Config.cs
   
   # Windows
   dir %APPDATA%/dotty/Dotty.UserConfig/Config.cs
   ```

2. Verify the namespace is correct:
   ```csharp
   namespace Dotty.UserConfig;  // Must match exactly
   ```

3. Ensure your class implements `IDottyConfig`:
   ```csharp
   public partial class MyDottyConfig : IDottyConfig
   ```

#### Issue: Build Errors After Config Changes

**Symptom:** `dotnet build` fails with configuration errors.

**Common Causes:**
1. **Missing semicolons or braces** - Check for syntax errors
2. **Wrong namespace** - Must be `Dotty.UserConfig`
3. **Missing `partial` keyword** - Class must be declared `partial`
4. **Type mismatches** - Ensure property types match (e.g., `double?` for `FontSize`)

**Solution:** Check the detailed error message:
```bash
dotnet build --verbosity normal
```

#### Issue: Transparency Not Working

**Symptom:** Window opacity settings have no effect.

**Solutions:**
1. **Platform limitations:** True acrylic/blur requires:
   - Windows: Windows 10/11 with DWM
   - macOS: 10.14+ with NSVisualEffectView support
   - Linux: Compositor with blur support (e.g., picom, Mutter)

2. **Use `WindowOpacity` as fallback:**
   ```csharp
   public byte? WindowOpacity => 85;  // Works everywhere
   ```

3. **Check compositor on Linux:**
   ```bash
   # For picom (compton), ensure blur is enabled
   picom --blur-background
   ```

#### Issue: Font Not Loading

**Symptom:** Font family change has no effect.

**Solutions:**
1. **Verify font is installed:**
   ```bash
   # Linux
   fc-list | grep "Fira Code"
   
   # macOS
   system_profiler SPFontsDataType | grep "Fira Code"
   
   # Windows
   # Check Control Panel > Fonts
   ```

2. **Check font name spelling** - Must match exactly, including spaces
3. **Use fallbacks** - Always include `monospace` at the end
4. **Clear font cache** (Linux):
   ```bash
   fc-cache -fv
   ```

#### Issue: Cursor Blinking Too Fast/Slow

**Symptom:** Cursor blink speed isn't right.

**Solution:** Adjust the interval:
```csharp
public ICursorSettings? Cursor => new CursorSettings
{
    Blink = true,
    BlinkIntervalMs = 600  // Default is 500ms
};
```

**Recommended intervals:**
- `400` - Fast blink
- `500` - Default blink
- `600` - Slower blink
- `800` - Very slow blink

---

## Rebuild Instructions

### How to Rebuild After Config Changes

After editing your `Config.cs`, you **must** rebuild Dotty to apply the changes.

#### Quick Rebuild (Development Build)

```bash
# Navigate to the Dotty source directory
cd /path/to/dotty

# Build only (faster)
dotnet build

# Or build the specific project
dotnet build src/Dotty.App/Dotty.App.csproj
```

#### Full Rebuild (Clean Build)

If you encounter issues, try a clean rebuild:

```bash
cd /path/to/dotty

# Clean all build artifacts
dotnet clean

# Rebuild everything
dotnet build
```

#### Release Build

For the best performance, build in Release mode:

```bash
cd /path/to/dotty

# Release build (optimized)
dotnet build --configuration Release

# Or with self-contained publish
dotnet publish src/Dotty.App/Dotty.App.csproj \
    --configuration Release \
    --self-contained \
    --runtime linux-x64 \
    --output ./publish
```

#### Running After Build

```bash
# Run from build output
./src/Dotty.App/bin/Debug/net8.0/Dotty.App

# Or if published
./publish/Dotty.App
```

### Build Verification

Check if your configuration was applied:

1. **Verify build succeeded:**
   ```bash
   dotnet build
   # Should show: Build succeeded with 0 warnings
   ```

2. **Run with verbose logging:**
   ```bash
   DOTTY_LOG_LEVEL=Debug ./src/Dotty.App/bin/Debug/net8.0/Dotty.App
   ```

3. **Check generated files** (optional):
   ```bash
   # Find generated Config class
   find ~/.config/dotty -name "*.g.cs" 2>/dev/null
   ```

### Automating Rebuilds

Add an alias to your shell for quick rebuilding:

```bash
# Add to ~/.bashrc or ~/.zshrc
alias dotty-rebuild='cd /path/to/dotty && dotnet build && ./src/Dotty.App/bin/Debug/net8.0/Dotty.App'
```

Then simply run:
```bash
dotty-rebuild
```

### Tips for Faster Development

1. **Use watch mode** for automatic rebuilds during development:
   ```bash
   dotnet watch build
   ```

2. **Build only the app project** (skips tests):
   ```bash
   dotnet build src/Dotty.App/Dotty.App.csproj
   ```

3. **Skip validation** (not recommended for production):
   ```bash
   dotnet build -p:SkipValidation=true
   ```

---

## Additional Resources

- **Sample Configuration**: See `samples/Config.cs` in the Dotty repository
- **Advanced Configuration**: See `docs/CONFIGURATION_ADVANCED.md` for source generator details
- **Theme Architecture**: See `docs/CustomThemeArchitecture.md` for creating complex themes
- **API Reference**: The `Dotty.Abstractions` NuGet package includes XML documentation

---

## Getting Help

If you encounter issues not covered here:

1. Check the [troubleshooting section](#troubleshooting) above
2. Review the sample configuration in `~/.config/dotty/Dotty.UserConfig/Config.cs`
3. Look at working examples in the `samples/` directory
4. Enable debug logging: `DOTTY_LOG_LEVEL=Debug dotnet run`

Happy customizing! 🎨
