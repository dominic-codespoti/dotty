# Dotty

A high-performance terminal emulator for .NET, built with Avalonia UI and optimized for speed and memory efficiency.

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

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

Dotty automatically creates a configuration project on first run:

- **Linux/macOS**: `~/.config/dotty/Dotty.UserConfig/`
- **Windows**: `%APPDATA%/dotty/Dotty.UserConfig/`

The generated project includes:
- A `Config.cs` file with sensible defaults (DarkPlus theme, JetBrains Mono 15pt)
- A `.csproj` with NuGet reference to `Dotty.Abstractions` for full IntelliSense
- Helpful comments explaining all options
- Open in your IDE for LSP support (VS Code, Rider, etc.)

**To customize:**
1. Open the config folder in your IDE: `code ~/.config/dotty/Dotty.UserConfig/`
2. Edit `Config.cs`
3. Rebuild dotty: `dotnet build`

**To regenerate:**
```bash
dotty --generate-config  # ⚠️ Overwrites existing config
```

The `Dotty.Abstractions` NuGet package provides full IntelliSense and LSP support for your configuration. See [Configuration Guide](docs/CONFIGURATION.md) for full details.

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

- [Architecture Overview](docs/architecture.md)
- [Rendering System](docs/rendering.md)
- [Parser Implementation](docs/parsing.md)
- [Native PTY](docs/native-pty.md)
- [Testing](docs/testing.md)
- [Performance Analysis](docs/comparison-report.md)

## License

MIT License - See [LICENSE](LICENSE) for details.

## Links

- Repository: https://github.com/dominic-codespoti/dotty
- Issues: https://github.com/dominic-codespoti/dotty/issues
