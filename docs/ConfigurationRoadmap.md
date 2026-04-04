# Dotty Configuration Roadmap

> **Note**: This roadmap documents potential future enhancements to the Dotty configuration system. Items are organized by priority and complexity. **These are possibilities, not commitments**. The current configuration system is stable and production-ready.

---

## Phase 1: Core Stabilization (Current - v1.0)

**Status**: ✅ Complete and stable

Core features that provide a solid foundation for all users:

- [x] **Basic configuration interface** (`IDottyConfig`)
  - Font family, size, padding settings
  - Color scheme with 16 ANSI colors
  - Cursor appearance and behavior
  - Window dimensions and startup options
  - Scrollback configuration

- [x] **Default values generation**
  - Sensible defaults matching previous hardcoded values
  - Zero-config startup experience
  - Fallback values for all optional properties

- [x] **Key bindings framework**
  - `IKeyBindings` interface for custom shortcuts
  - Standard action enum (`TerminalAction`)
  - Default keymap (Ctrl+Shift+T for new tab, etc.)

- [x] **Source Generator architecture**
  - Compile-time code generation
  - AOT-compatible output
  - Static configuration access
  - No runtime reflection

- [x] **ConfigBridge integration**
  - Avalonia type conversion
  - Color format helpers (ARGB → Avalonia Color)
  - Font family resolution

---

## Phase 2: Quality of Life (Next - v1.1-1.2)

**Status**: 🚧 Under consideration

Features that improve the day-to-day configuration experience with minimal complexity:

### Environment Variable Interpolation
**Priority**: High | **Complexity**: Low | **Target**: v1.1

Allow environment variables in configuration values:

```csharp
public string? FontFamily => $"{Environment.GetEnvironmentVariable("DOTTY_FONT")}" ?? "JetBrains Mono";
```

**Implementation Notes**:
- Source generator can evaluate `Environment.GetEnvironmentVariable()` calls at build time
- Support for null-coalescing with defaults
- Document that this is build-time, not runtime

---

### OS-Specific Defaults
**Priority**: High | **Complexity**: Low | **Target**: v1.1

Built-in presets for different operating systems:

```csharp
public partial class MyConfig : IDottyConfig, IOSXConfig { }  // Uses macOS defaults
public partial class MyConfig : IDottyConfig, ILinuxConfig { }  // Uses Linux defaults
```

**Implementation Notes**:
- Use `OperatingSystem.IsXXX()` in generated code
- Pre-defined interface mixins with OS-appropriate defaults
- Auto-detect if no explicit interface specified

---

### Validation Attributes
**Priority**: Medium | **Complexity**: Low | **Target**: v1.2

Compile-time validation of configuration values:

```csharp
[Range(6.0, 72.0)]
public double? FontSize => 15.0;

[PositiveInteger]
public int? ScrollbackLines => 10000;
```

**Implementation Notes**:
- Source generator validates at build time
- Build warnings or errors for invalid values
- Range checking for numeric values
- Color format validation

---

### Thickness Simplification
**Priority**: Medium | **Complexity**: Low | **Target**: v1.1

Easier padding configuration:

```csharp
// Currently requires full Thickness record
public Thickness? ContentPadding => new(10);  // Uniform
public Thickness? ContentPadding => new(10, 5);  // Horizontal, Vertical
```

**Implementation Notes**:
- Already supported, needs better documentation
- Add examples to sample configs

---

### Multiple Config Files Support
**Priority**: Medium | **Complexity**: Medium | **Target**: v1.2

Support for config composition from multiple files:

```csharp
// ~/.config/dotty/Fonts.cs
public interface IFontConfig : IDottyConfig { /* font settings */ }

// ~/.config/dotty/Colors.cs  
public interface IColorConfig : IDottyConfig { /* color settings */ }

// ~/.config/dotty/Config.cs
public partial class Config : IFontConfig, IColorConfig { }
```

**Implementation Notes**:
- Source generator already scans all files
- Document the composition pattern
- Provide examples

---

## Phase 3: Power User Features (Future - v1.3-1.5)

**Status**: 💡 Proposed

Advanced features for users who need more control:

### Conditional Keybindings
**Priority**: Medium | **Complexity**: Medium | **Target**: v1.3

Context-aware key bindings:

```csharp
public class ContextAwareBindings : IKeyBindings
{
    public TerminalAction? GetAction(Key key, KeyModifiers mods)
    {
        if (IsRunningUnderTmux())
            return GetTmuxSafeBinding(key, mods);
        return GetStandardBinding(key, mods);
    }
}
```

**Implementation Notes**:
- Requires runtime context detection
- Maintain AOT compatibility by generating switch expressions
- Detect tmux/screen via environment variables

---

### Config Inheritance
**Priority**: Medium | **Complexity**: Medium | **Target**: v1.3

Inherit from preset configurations:

```csharp
public partial class MyConfig : PresetConfigs.DarkModern
{
    public override string? FontFamily => "Fira Code";
}
```

**Implementation Notes**:
- Source generator flattens inheritance
- Base class properties become source for defaults
- Override detection via `virtual`/`override` keywords

---

### Profile-Based Configs
**Priority**: Medium | **Complexity**: Medium | **Target**: v1.4

Multiple named profiles:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IProfile? ActiveProfile => ProfileSelector.FromEnvironment();
}
```

**Implementation Notes**:
- Switch via `DOTTY_PROFILE` environment variable
- Build-time profile selection
- Each profile generates separate config class

---

### Smart Defaults
**Priority**: Low | **Complexity**: High | **Target**: v1.5

Auto-detect optimal settings:

```csharp
public partial class MyConfig : IDottyConfig
{
    public IDottyConfig UseSmartDefaults() => new SmartDefaultsConfig();
}
```

**Implementation Notes**:
- Build-time detection where possible (DPI, available fonts)
- Runtime detection for dynamic values (GPU availability)
- AOT-friendly static analysis

---

### Font Feature Toggles
**Priority**: Low | **Complexity**: Medium | **Target**: v1.4

OpenType feature control:

```csharp
public IFontFeatures? FontFeatures => new FontFeatures
{
    Liga = true,
    Calt = true,
    Ss01 = true
};
```

**Implementation Notes**:
- Requires Skia/Avalonia font feature support
- Generate feature bitmask at compile time
- Document which features work with which fonts

---

## Phase 4: Advanced Scenarios (Long-term - v2.0+)

**Status**: 🔮 Exploratory

Complex features for specialized use cases:

### Custom Panels
**Priority**: Low | **Complexity**: High | **Target**: v2.0

Split panels and custom layouts:

```csharp
public ILayout? Layout => new GridLayout(2, 2)
    .WithPanel(0, 0, new TerminalPanel { Shell = "zsh" })
    .WithPanel(0, 1, new TerminalPanel { Shell = "fish" })
    .WithPanel(1, 0, new InfoPanel { Type = PanelType.SystemInfo })
    .WithPanel(1, 1, new TerminalPanel { Shell = "bash" });
```

**Implementation Notes**:
- Major UI architecture changes
- Panel management system
- Session persistence

---

### Protocol Extensions
**Priority**: Low | **Complexity**: High | **Target**: v2.0

Custom escape sequence handlers:

```csharp
public IProtocolExtensions? Extensions => new ProtocolExtensions
{
    ["dotty-set-font"] = (args) => SetFontAsync(args),
    ["dotty-notify"] = (args) => ShowNotificationAsync(args)
};
```

**Implementation Notes**:
- VT protocol extension framework
- Async handlers
- Security considerations for escape sequences

---

### Output Transformers
**Priority**: Low | **Complexity**: High | **Target**: v2.0

Real-time output processing:

```csharp
public IOutputTransformers? Transforms => new OutputTransformers
{
    // Make URLs clickable
    new RegexTransformer(@"https?://\S+", match => 
        new Hyperlink(match.Value, match.Value)),
    
    // Highlight errors
    new PatternTransformer("ERROR:", new Style { Foreground = Colors.Red })
};
```

**Implementation Notes**:
- Performance-critical path
- GPU-accelerated where possible
- Configurable per-tab

---

### Plugin System
**Priority**: Low | **Complexity**: Very High | **Target**: v2.0+

External plugin support:

```csharp
public IPluginConfiguration? Plugins => new PluginConfiguration
{
    LoadPaths = new[] { "~/.config/dotty/plugins" },
    Plugins = new[]
    {
        new Plugin("dotty-plugin-git", Version = "1.0.0"),
        new Plugin("dotty-plugin-kubectl", Version = "2.1.0")
    }
};
```

**Implementation Notes**:
- Requires plugin API design
- Security sandboxing
- AOT compatibility challenges
- Version management

---

## Implementation Status Legend

| Symbol | Meaning |
|--------|---------|
| ✅ | Implemented and stable |
| 🚧 | Under active development |
| 💡 | Proposed, awaiting prioritization |
| 🔮 | Exploratory, significant research needed |

---

## Complexity Assessment

| Complexity | Criteria |
|------------|----------|
| **Low** | Source generator only changes, no runtime impact |
| **Medium** | Requires runtime support, but within existing architecture |
| **High** | Architecture changes or significant new subsystems |
| **Very High** | Cross-cutting concerns, security implications, major design |

---

## Version Planning

### v1.1 (Near-term)
- Environment variable interpolation
- OS-specific defaults
- Better documentation and examples

### v1.2 (Short-term)
- Validation attributes
- Thickness improvements
- Multiple config file support
- More preset themes

### v1.3-1.5 (Medium-term)
- Conditional keybindings
- Config inheritance
- Profile system
- Font features

### v2.0 (Long-term)
- Custom panels
- Protocol extensions
- Output transformers
- Plugin architecture

---

## AOT Compatibility Notes

All roadmap items must maintain AOT compatibility:

1. **Compile-time evaluation**: Source generator evaluates expressions where possible
2. **Static branching**: Use `OperatingSystem.IsXXX()` and similar static methods
3. **Generated switch expressions**: No runtime reflection or dynamic code
4. **Trim-safe**: All types used in configuration must be trimmable

Example transformation:

```csharp
// User writes:
public string? FontFamily => Environment.GetEnvironmentVariable("DOTTY_FONT") ?? "default";

// Source generator produces (at build time):
public static string FontFamily => "JetBrains Mono";  // Evaluated value
```

---

## Contributing to the Roadmap

This roadmap is a living document. To propose new features:

1. Consider AOT compatibility requirements
2. Assess complexity impact
3. Provide use case examples
4. Note any breaking changes

The focus remains on maintaining the simplicity of the current system while enabling power users to do more.
