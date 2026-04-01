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

Dotty automatically creates a configuration file on first run:

- **Linux/macOS**: `~/.config/dotty/Config.cs`
- **Windows**: `%APPDATA%/dotty/Config.cs`

The generated config includes:
- Sensible defaults (DarkPlus theme, JetBrains Mono 15pt)
- Helpful comments explaining all options
- Uncomment examples for easy customization

**To customize:**
1. Edit `~/.config/dotty/Config.cs`
2. Rebuild: `dotnet build`

**To regenerate:**
```bash
dotty --generate-config  # ⚠️ Overwrites existing config
```

See [Configuration Guide](docs/CONFIGURATION.md) for full details.

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
