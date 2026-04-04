# Dotty Configuration: Advanced Features

> **Status**: These features are documented as future possibilities for the Dotty configuration system. They represent potential enhancements that could be enabled by the Source Generator architecture, but are **not yet implemented**.
>
> The current configuration system (v1) is production-ready and stable. These advanced features are roadmap items that may be added in future releases.

---

## Section 1: Conditional & Context-Aware Configs

### Environment Variable-Based Configs

Future versions might support environment variable interpolation and conditional configuration:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Environment variable interpolation
    public string? FontFamily => $"{Environment.GetEnvironmentVariable("DOTTY_FONT")}" ?? "JetBrains Mono";
    
    // Conditional based on environment
    public double? FontSize => Environment.GetEnvironmentVariable("DOTTY_HIGHDPI") == "1" 
        ? 18.0 
        : 14.0;
}
```

### Time-Based Theme Switching

Automatic theme switching based on time of day:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IColorScheme? Colors => TimeBasedTheme.GetCurrentTheme();
}

public static class TimeBasedTheme
{
    public static IColorScheme GetCurrentTheme()
    {
        var hour = DateTime.Now.Hour;
        return hour >= 6 && hour < 18 
            ? new DayTheme()      // Light theme during day
            : new NightTheme();   // Dark theme at night
    }
}
```

### OS-Specific Defaults

Different defaults for macOS vs Linux:

```csharp
public partial class MyConfig : IDottyConfig
{
    public string? FontFamily => OperatingSystem.IsMacOS()
        ? "SF Mono, Menlo, monospace"      // macOS system fonts
        : "JetBrains Mono, monospace";     // Linux default
    
    public double? FontSize => OperatingSystem.IsMacOS() ? 13.0 : 15.0;
    
    public IKeyBindings? KeyBindings => OperatingSystem.IsMacOS()
        ? new MacKeyBindings()    // Cmd-based shortcuts
        : new LinuxKeyBindings();  // Ctrl-based shortcuts
}
```

### Per-Host Configurations

Different settings based on hostname:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IWindowDimensions? InitialDimensions => HostBasedDimensions.Get();
}

public static class HostBasedDimensions
{
    public static IWindowDimensions Get()
    {
        var hostname = Environment.MachineName;
        return hostname switch
        {
            "laptop" => new LaptopDimensions(),     // Smaller screen
            "desktop" => new DesktopDimensions(),   // Large monitor
            "server" => new ServerDimensions(),     // SSH optimized
            _ => new DefaultDimensions()
        };
    }
}
```

---

## Section 2: Computed Keybindings

### Conditional Bindings

Different bindings based on context (e.g., when running inside tmux):

```csharp
public class ContextAwareKeyBindings : IKeyBindings
{
    public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
    {
        // Detect if running under tmux/screen
        if (IsRunningUnderTmux())
        {
            // Use alternate bindings that don't conflict with tmux
            return GetTmuxSafeBinding(key, modifiers);
        }
        
        return GetStandardBinding(key, modifiers);
    }
    
    private static bool IsRunningUnderTmux() => 
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX"));
}
```

### Chord/Key Sequences

Vim-style leader key sequences:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Enable chord-based keybindings with a leader key
    public IKeyBindings? KeyBindings => new ChordKeyBindings(
        leaderKey: Key.Space,
        leaderModifiers: KeyModifiers.Control
    );
}

public class ChordKeyBindings : IKeyBindings
{
    private readonly Key _leaderKey;
    private readonly KeyModifiers _leaderModifiers;
    private KeyChordState _state = KeyChordState.Ready;
    
    public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
    {
        if (_state == KeyChordState.WaitingForChord)
        {
            _state = KeyChordState.Ready;
            return GetChordAction(key, modifiers);
        }
        
        if (key == _leaderKey && modifiers == _leaderModifiers)
        {
            _state = KeyChordState.WaitingForChord;
            return TerminalAction.None; // Consume the leader
        }
        
        return GetStandardBinding(key, modifiers);
    }
    
    private TerminalAction? GetChordAction(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        (Key.N, KeyModifiers.None) => TerminalAction.NewTab,     // Ctrl+Space, n
        (Key.C, KeyModifiers.None) => TerminalAction.CloseTab,   // Ctrl+Space, c
        (Key.Right, KeyModifiers.None) => TerminalAction.NextTab, // Ctrl+Space, Right
        (Key.Left, KeyModifiers.None) => TerminalAction.PreviousTab,
        _ => null
    };
}
```

### Mode-Based Bindings

Vim-style modal keybindings (normal/insert modes):

```csharp
public class ModalKeyBindings : IKeyBindings
{
    private TerminalMode _currentMode = TerminalMode.Insert;
    
    public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
    {
        // Escape always returns to normal mode
        if (key == Key.Escape)
        {
            _currentMode = TerminalMode.Normal;
            return TerminalAction.None;
        }
        
        return _currentMode switch
        {
            TerminalMode.Insert => GetInsertModeBinding(key, modifiers),
            TerminalMode.Normal => GetNormalModeBinding(key, modifiers)
        };
    }
    
    private TerminalAction? GetNormalModeBinding(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        // Movement
        (Key.H, KeyModifiers.None) => TerminalAction.MoveLeft,
        (Key.J, KeyModifiers.None) => TerminalAction.MoveDown,
        (Key.K, KeyModifiers.None) => TerminalAction.MoveUp,
        (Key.L, KeyModifiers.None) => TerminalAction.MoveRight,
        
        // Scrolling
        (Key.G, KeyModifiers.Shift) => TerminalAction.ScrollToTop,
        (Key.G, KeyModifiers.None) => TerminalAction.ScrollToBottom,
        
        // Tab management
        (Key.T, KeyModifiers.None) => TerminalAction.NewTab,
        (Key.X, KeyModifiers.None) => TerminalAction.CloseTab,
        
        // Enter insert mode
        (Key.I, KeyModifiers.None) => TerminalAction.EnterInsertMode,
        _ => null
    };
}
```

### Application-Specific Bindings

Different bindings for different applications:

```csharp
public class ApplicationSpecificBindings : IKeyBindings
{
    public TerminalAction? GetAction(Key key, KeyModifiers modifiers)
    {
        var currentApp = GetForegroundProcessName();
        
        return currentApp switch
        {
            "vim" => GetVimCompatibleBinding(key, modifiers),
            "nvim" => GetNeovimCompatibleBinding(key, modifiers),
            "emacs" => GetEmacsCompatibleBinding(key, modifiers),
            _ => GetStandardBinding(key, modifiers)
        };
    }
}
```

---

## Section 3: Config Composition & Inheritance

### Inheriting from Preset Configs

Users might create configs that inherit from built-in presets:

```csharp
// Inherit from a preset and override specific values
public partial class MyConfig : PresetConfigs.DarkModern
{
    // Override just the font
    public override string? FontFamily => "Fira Code, monospace";
    
    // Override specific colors
    public override IColorScheme? Colors => new CustomizedDarkTheme
    {
        Background = 0xFF1E1E1E,  // Slightly lighter
        AccentColor = 0xFF007ACC   // Custom accent
    };
}

// Presets could be provided by Dotty
public static class PresetConfigs
{
    public abstract class DarkModern : IDottyConfig
    {
        public virtual string? FontFamily => "JetBrains Mono, monospace";
        public virtual double? FontSize => 15.0;
        public virtual IColorScheme? Colors => new DarkModernTheme();
        // ... other defaults
    }
    
    public abstract class LightModern : IDottyConfig { /* ... */ }
    public abstract class HighContrast : IDottyConfig { /* ... */ }
    public abstract class Minimal : IDottyConfig { /* ... */ }
}
```

### Mixing Multiple Configs Together

Combining partial configurations from multiple sources:

```csharp
// Compose multiple config "mixins"
public partial class MyConfig : IDottyConfig,
    IFontConfig,      // From Fonts.cs
    IColorConfig,     // From Colors.cs
    IKeyConfig        // From Keys.cs
{
    // The source generator could combine all interface implementations
}

// In Fonts.cs
public interface IFontConfig : IDottyConfig
{
    string? IDottyConfig.FontFamily => "Fira Code, monospace";
    double? IDottyConfig.FontSize => 14.0;
}

// In Colors.cs
public interface IColorConfig : IDottyConfig
{
    IColorScheme? IDottyConfig.Colors => new SolarizedDark();
}

// In Keys.cs
public interface IKeyConfig : IDottyConfig
{
    IKeyBindings? IDottyConfig.KeyBindings => new VimStyleBindings();
}
```

### Profile-Based Configs

Multiple named profiles that can be switched at runtime:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Select profile based on environment or startup parameter
    public IProfile? ActiveProfile => ProfileSelector.GetActiveProfile();
}

public interface IProfile
{
    string Name { get; }
    IDottyConfig Configuration { get; }
}

public static class ProfileSelector
{
    public static IProfile GetActiveProfile()
    {
        var profileName = Environment.GetEnvironmentVariable("DOTTY_PROFILE") ?? "default";
        
        return profileName switch
        {
            "dev" => new DevProfile(),      // Large font, lots of scrollback
            "server" => new ServerProfile(), // Minimal, SSH optimized
            "present" => new PresentProfile(), // Huge font for demos
            _ => new DefaultProfile()
        };
    }
}

public class DevProfile : IProfile
{
    public string Name => "dev";
    public IDottyConfig Configuration => new DevConfig();
}
```

### Shell-Specific Profiles

Different configs for different shells:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IDottyConfig GetConfigForShell(string shell)
    {
        return shell switch
        {
            "zsh" => new ZshOptimizedConfig(),    // Custom bindings for zsh features
            "fish" => new FishOptimizedConfig(),  // Fish-friendly settings
            "bash" => new BashOptimizedConfig(),  // Traditional bash setup
            "powershell" => new PowerShellConfig(), // Windows-style
            _ => this
        };
    }
}
```

---

## Section 4: Validation & Safety

### Compile-Time Validation Attributes

Attributes that could enable compile-time validation:

```csharp
public partial class MyConfig : IDottyConfig
{
    [FontExists]  // Source generator validates font at build time
    public string? FontFamily => "Fira Code, monospace";
    
    [Range(8.0, 72.0)]  // Must be between 8 and 72 points
    public double? FontSize => 15.0;
    
    [ValidColor]  // Must be valid ARGB format
    public uint? SelectionColor => 0xA03385DB;
    
    [PositiveInteger]
    public int? ScrollbackLines => 10000;
}
```

### Range Checking for Numeric Values

Automatic range validation:

```csharp
// The source generator could emit validation code
public static class ConfigValidation
{
    public static void ValidateFontSize(double value)
    {
        if (value < 6.0 || value > 256.0)
        {
            throw new ConfigurationException(
                $"FontSize must be between 6.0 and 256.0, got {value}");
        }
    }
    
    public static void ValidateScrollbackLines(int value)
    {
        if (value < 100 || value > 10_000_000)
        {
            throw new ConfigurationException(
                $"ScrollbackLines must be between 100 and 10,000,000, got {value}");
        }
    }
}
```

### Font Existence Validation

Build-time font availability checking:

```csharp
// Source generator could check system fonts at build time
[AttributeUsage(AttributeTargets.Property)]
public class FontExistsAttribute : Attribute
{
    public bool RequireAll { get; set; } = false;  // Require all fonts in stack
    public bool WarnOnly { get; set; } = true;   // Warning vs error
}

public partial class MyConfig : IDottyConfig
{
    // Build warning if Fira Code isn't installed
    [FontExists(WarnOnly = true)]
    public string? FontFamily => "Fira Code, monospace";
}
```

### Color Format Validation

Compile-time color format checking:

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class ValidColorAttribute : Attribute
{
    public bool AllowTransparency { get; set; } = true;
    public bool RequireOpaque { get; set; } = false;
}

public partial class MyConfig : IDottyConfig
{
    // Compiler error if not valid ARGB format
    [ValidColor(RequireOpaque = true)]
    public uint Background => 0xFF181818;  // OK
    
    [ValidColor]
    public uint Foreground => 0xFFD4D4D4;  // OK
}
```

### Custom Validation Rules

User-defined validation:

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class ValidateConfigAttribute : Attribute
{
    public Type ValidatorType { get; set; }
}

public interface IConfigValidator
{
    ValidationResult Validate(IDottyConfig config);
}

[ValidateConfig(typeof(MyConfigValidator))]
public partial class MyConfig : IDottyConfig
{
    public int? ScrollbackLines => 50000;
    public double? FontSize => 14.0;
}

public class MyConfigValidator : IConfigValidator
{
    public ValidationResult Validate(IDottyConfig config)
    {
        // Custom validation logic
        if (config.ScrollbackLines > 100000 && config.FontSize < 10)
        {
            return ValidationResult.Warning(
                "Large scrollback with small font may cause high memory usage");
        }
        
        return ValidationResult.Success();
    }
}
```

---

## Section 5: Advanced Font Handling

### Font Chains with Fallback Ranges

Granular font fallback control:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Specify exact Unicode ranges for each fallback
    public IFontChain? FontChain => new FontChain()
        .WithPrimary("Fira Code", new[] { UnicodeRanges.BasicLatin, UnicodeRanges.Latin1Supplement })
        .WithFallback("Noto Sans CJK", UnicodeRanges.CjkUnifiedIdeographs)
        .WithFallback("Noto Color Emoji", UnicodeRanges.Emoji)
        .WithFallback("Symbols Nerd Font", UnicodeRanges.PrivateUseArea);
}

public class FontChain
{
    public FontChain WithPrimary(string fontName, UnicodeRange[] ranges)
    {
        // Configure primary font for specific ranges
        return this;
    }
    
    public FontChain WithFallback(string fontName, UnicodeRange fallbackRange)
    {
        // Add fallback for specific Unicode range
        return this;
    }
}
```

### Emoji/CJK-Specific Fallbacks

Automatic emoji and CJK font handling:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Automatically configure emoji and CJK fallbacks
    public IFontConfiguration? Fonts => new FontConfiguration
    {
        Primary = "JetBrains Mono",
        Emoji = new EmojiConfiguration
        {
            Font = "Noto Color Emoji",
            Presentation = EmojiPresentation.Color,  // vs Monochrome
            Scale = 1.1  // Slightly larger than text
        },
        Cjk = new CjkConfiguration
        {
            SimplifiedChinese = "Noto Sans CJK SC",
            TraditionalChinese = "Noto Sans CJK TC",
            Japanese = "Noto Sans CJK JP",
            Korean = "Noto Sans CJK KR",
            PreferVerticalPunctuation = false
        }
    };
}
```

### Dynamic Ligature Detection

Automatic ligature feature detection:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IFontFeatures? FontFeatures => new FontFeatures
    {
        // Auto-detect what features the font supports
        Ligatures = LigatureMode.AutoDetect,  // Detect from font metadata
        
        // Explicit feature toggles
        ProgrammingLigatures = true,   // ->, =>, !=
        ContextualAlternates = true,   // calt
        StylisticSet01 = true,         // ss01
        StylisticSet02 = false,        // ss02
        
        // Font-specific features
        CustomFeatures = new Dictionary<string, bool>
        {
            ["cv01"] = true,  // Character variant 1
            ["zero"] = true   // Slashed zero
        }
    };
}
```

### Font Feature Toggles

Fine-grained OpenType feature control:

```csharp
public partial class MyConfig : IDottyConfig
{
    // Enable specific OpenType features
    public IOpenTypeFeatures? OpenType => new OpenTypeFeatures
    {
        // Ligatures
        Liga = true,   // Standard ligatures
        Dlig = false,  // Discretionary ligatures
        Calt = true,   // Contextual alternates
        
        // Character variants
        Cv01 = true, Cv02 = true, Cv03 = false,
        
        // Stylistic sets
        Ss01 = true,   // Alternative ampersand
        Ss02 = true,   // Straight quotes
        Ss03 = false,
        Ss04 = false,
        Ss05 = false,
        Ss06 = false,
        Ss07 = false,
        Ss08 = false,
        
        // Numerals
        Onum = false,  // Old-style numerals
        Lnum = true,   // Lining numerals
        Pnum = false,  // Proportional numerals
        Tnum = true,   // Tabular numerals
        
        // Zero variant
        Zero = true    // Slashed zero
    };
}
```

---

## Section 6: Smart Defaults

### Auto-Detecting Optimal Settings

Automatic configuration based on system capabilities:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IDottyConfig GetSmartDefaults() => new SmartDefaultsConfig();
}

public class SmartDefaultsConfig : IDottyConfig
{
    // Auto-detect based on system
    public string? FontFamily => FontDetector.GetBestMonospaceFont();
    public double? FontSize => DisplayDetector.GetOptimalFontSize();
    public int? ScrollbackLines => MemoryDetector.GetRecommendedScrollback();
    
    // GPU detection for rendering preferences
    public RenderSettings? Rendering => new RenderSettings
    {
        UseGpuAcceleration = GpuDetector.IsGpuAvailable(),
        GpuBackend = GpuDetector.GetBestBackend(),  // Vulkan, OpenGL, Metal
        SoftwareFallback = true
    };
}

public static class DisplayDetector
{
    public static double GetOptimalFontSize()
    {
        // Detect DPI and screen size
        var dpi = GetSystemDpi();
        var resolution = GetScreenResolution();
        
        // Calculate optimal size
        return dpi switch
        {
            > 200 => 18.0,  // Retina/HiDPI
            > 150 => 16.0,  // High DPI
            _ => 14.0       // Standard
        };
    }
}
```

### Shell Integration Profiles

Auto-configure based on detected shell:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IShellIntegration? ShellIntegration => new ShellIntegration
    {
        AutoDetect = true,
        Features = ShellFeatures.All
    };
}

public class ShellIntegration
{
    public bool AutoDetect { get; set; }
    public string? ShellPath { get; set; }
    public ShellFeatures Features { get; set; }
}

[Flags]
public enum ShellFeatures
{
    None = 0,
    WorkingDirectoryTracking = 1,
    Hyperlinks = 2,
    PromptMarking = 4,
    Suggestions = 8,
    All = WorkingDirectoryTracking | Hyperlinks | PromptMarking | Suggestions
}
```

### GPU Acceleration Conditions

Conditional GPU usage based on environment:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IRenderConfiguration? Rendering => new RenderConfiguration
    {
        // Conditions for GPU acceleration
        GpuAcceleration = GpuAcceleration.Conditional,
        
        // Only use GPU when:
        GpuConditions = new GpuConditions
        {
            BatteryOk = true,           // Not on battery
            TemperatureOk = true,       // GPU not overheating
            MemoryAvailable = 512,     // At least 512MB VRAM free
            RemoteSession = false      // Not over SSH/RDP
        },
        
        // Fallback settings
        SoftwareRender = new SoftwareRenderSettings
        {
            ThreadCount = Environment.ProcessorCount / 2,
            OptimizationLevel = OptimizationLevel.Speed
        }
    };
}
```

### Scrollback Size Based on Available RAM

Dynamic scrollback based on system memory:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IScrollbackConfiguration? Scrollback => new DynamicScrollback();
}

public class DynamicScrollback : IScrollbackConfiguration
{
    public int GetScrollbackLines()
    {
        var availableRam = GetAvailableMemoryMB();
        var estimatedCharSize = 4; // bytes per character (UTF-32)
        var avgLineLength = 100;
        var safetyFactor = 0.1; // Use only 10% of available RAM
        
        var maxLines = (int)((availableRam * 1024 * 1024 * safetyFactor) / 
                            (avgLineLength * estimatedCharSize));
        
        return Math.Clamp(maxLines, 1000, 1000000);  // Between 1K and 1M lines
    }
    
    public ScrollbackMode Mode => ScrollbackMode.Dynamic;
    public int MinLines => 1000;
    public int MaxLines => 1000000;
}
```

---

## AOT Considerations for Advanced Features

All proposed advanced features maintain AOT compatibility:

| Feature | AOT Strategy |
|---------|--------------|
| Environment variables | Evaluated at build time where possible |
| Time-based configs | Switch expression with compile-time constants |
| OS-specific defaults | `#if` preprocessor directives or static branching |
| Config inheritance | Source generator flattens inheritance |
| Validation | Compile-time validation, no runtime reflection |
| Smart defaults | Static analysis or build-time detection |

Example of AOT-compatible conditional config:

```csharp
// Source generator produces:
public static class Config
{
    // Compile-time conditional
    public static string FontFamily => OperatingSystem.IsMacOS() 
        ? "SF Mono" 
        : "JetBrains Mono";
    
    // Or using preprocessor
#if MACOS
    public static string FontFamily => "SF Mono";
#else
    public static string FontFamily => "JetBrains Mono";
#endif
}
```

---

## Summary

These advanced features demonstrate the extensibility of Dotty's Source Generator-based configuration system. While not all features may be implemented, the architecture enables:

- **Compile-time evaluation** of complex logic
- **AOT-compatible** code generation
- **Type-safe** configuration with IntelliSense support
- **Zero runtime overhead** for configuration decisions

Users can start with simple configurations and gradually adopt advanced features as needed. The simple configuration API remains stable and supported even as these advanced features are added.
