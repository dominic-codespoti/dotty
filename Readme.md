# Dotty

A high-performance terminal emulator for .NET, built with Avalonia UI and optimized for speed and memory efficiency.

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.md)

## Overview

Dotty is a modern terminal emulator composed of:
- **Dotty.App** — Avalonia-based GUI application with hardware-accelerated rendering
- **Dotty.Terminal** — High-performance terminal core with zero-allocation parsing
- **Dotty.NativePty** — POSIX-native PTY helper for proper pseudo-terminal support
- **Dotty.Abstractions** — Clean interfaces for extensibility

### Key Features

- Hardware-accelerated rendering via SkiaSharp
- Optimized ANSI/VT parser with minimal allocations
- Native PTY support on Linux/Unix systems
- Efficient buffer management with scrollback support

## Quick Start

### Prerequisites

- .NET 10 SDK (or .NET 9)
- Linux/Unix system (for native PTY support)
- `make`, `gcc`/`clang`

### Build

```bash
# Build native PTY helper
cd src/Dotty.NativePty && make

# Build solution
cd ../..
dotnet build Dotty.sln -c Release
```

### Run

```bash
dotnet run --project src/Dotty.App
```

### Test

```bash
dotnet test tests/Dotty.App.Tests
```

## Configuration

Dotty uses a **compile-time configuration system** that delivers type-safe, high-performance settings with zero runtime overhead. Instead of parsing JSON at startup, Dotty generates optimized code from your C# configuration class during build.

### Quick Start

Dotty automatically creates a configuration project on first run:

- **Linux/macOS**: `~/.config/dotty/Dotty.UserConfig/`
- **Windows**: `%APPDATA%/dotty/Dotty.UserConfig/`

The generated project includes:
- `Config.cs` with sensible defaults (DarkPlus theme, JetBrains Mono 15pt)
- `.csproj` with NuGet reference to `Dotty.Abstractions` for full IntelliSense
- Helpful comments explaining all available options

**To customize:**
```bash
# 1. Open the config folder in your IDE
code ~/.config/dotty/Dotty.UserConfig/

# 2. Edit Config.cs (see example below)

# 3. Rebuild dotty to apply changes
dotnet build
```

**Quick Example:**
```csharp
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

namespace Dotty.UserConfig;

public partial class MyDottyConfig : IDottyConfig
{
    // Font: JetBrains Mono at 14pt
    public string? FontFamily => "JetBrains Mono, Fira Code, monospace";
    public double? FontSize => 14.0;
    
    // Theme: Dracula
    public IColorScheme? Colors => BuiltInThemes.Dracula;
    
    // Cursor: Blinking beam
    public ICursorSettings? Cursor => new CursorSettings
    {
        Shape = CursorShape.Beam,
        Blink = true
    };
}
```

### Key Features

| Feature | Benefit |
|---------|---------|
| **Type-safe** | Compile-time validation catches config errors before runtime |
| **Zero reflection** | All values resolved at compile time—no startup overhead |
| **AOT compatible** | Works with .NET Native AOT publishing |
| **Full IntelliSense** | IDE autocomplete and error checking via NuGet package |
| **11 built-in themes** | DarkPlus, Dracula, TokyoNight, Catppuccin, Gruvbox, and more |
| **Custom themes** | Create your own color schemes by extending `ColorSchemeBase` |
| **Transparency support** | Window opacity, blur, and acrylic effects |

### Default Settings

| Setting | Default Value |
|---------|-----------------|
| **Theme** | DarkPlus (VS Code: Dark+) |
| **Font** | JetBrains Mono, 15pt |
| **Cursor** | Block shape, blinking |
| **Scrollback** | 10,000 lines |
| **Window** | 80 columns × 24 rows |

### Documentation

- **[Configuration Guide](docs/guides/configuration.md)** — Complete user guide with examples and troubleshooting
- **[Architecture](docs/architecture/ConfigSourceGenerator.md)** — How the source generator works
- **[Advanced Topics](docs/ConfigurationAdvanced.md)** — Custom themes, transparency, and more

### Regenerate Config

```bash
dotty --generate-config  # ⚠️ Overwrites existing config
```

## Repository Structure

```
src/
  Dotty.App/         — Avalonia UI application
  Dotty.Terminal/    — Terminal engine (parsers, buffers, adapters)
  Dotty.NativePty/   — C-based POSIX PTY helper
  Dotty.Abstractions/ — Shared interfaces
tests/               — Unit tests
docs/                — Architecture and implementation docs
```

## Documentation

- [Architecture Overview](docs/Architecture.md)
- [Rendering System](docs/Rendering.md)
- [Parser Implementation](docs/Parsing.md)
- [Native PTY](docs/NativePty.md)
- [Testing](docs/Testing.md)
- [GUI Harness Benchmarking](docs/GuiHarnessBenchmarking.md)
- [Performance Analysis](docs/ComparisonReport.md)

## License

MIT License - See [License](License.md) for details.

## Links

- Repository: https://github.com/dominic-codespoti/dotty
- Issues: https://github.com/dominic-codespoti/dotty/issues
