# Dotty

A high-performance terminal emulator for .NET, built with Silk.NET, OpenGL,
and a cell-preserving terminal core.

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.md)

## Overview

*Last updated: 2026-08-31*

Dotty is a modern terminal emulator composed of:
- **Dotty** — Silk.NET/OpenGL desktop host.
- **Dotty.Terminal** — High-performance terminal core with zero-allocation parsing.
- **Dotty.NativePty** — Unix helper and Windows ConPTY backends.
- **Dotty.Abstractions** — Platform-neutral contracts.

### Key Features

- Hardware-accelerated OpenGL rendering with SkiaSharp font shaping
- Native PTY support on Linux, macOS, and Windows
- Efficient cell-preserving buffer and scrollback reflow
- Ligature support via HarfBuzz font shaping
- Undercurl, dotted, and dashed underline rendering
- Rounded rectangle clip regions for modern terminal aesthetics
- Runtime JSON configuration hot-reload with platform-specific paths
- PromptMark (OSC 1337) shell integration for prompt tracking

## Install

Prebuilt self-contained archives are available from the
[nightly prerelease](https://github.com/dominic-codespoti/dotty/releases/tag/nightly).
Nightly builds are unsigned prereleases, so Windows SmartScreen may warn on
first launch. There is no stable release or installer yet.

Linux x64:

```bash
tar -xzf dotty-<version>-linux-x64.tar.gz
./dotty
```

macOS x64:

```bash
tar -xzf dotty-<version>-osx-x64.tar.gz
./dotty
```

macOS arm64:

```bash
tar -xzf dotty-<version>-osx-arm64.tar.gz
./dotty
```

Windows x64: extract `dotty-<version>-win-x64.zip` and run `dotty.exe`,
keeping it beside the DLLs included in the archive. The archives are
self-contained; no .NET installation is needed. Windows requires build 17763+
(or Windows 11) and an OpenGL 3.3 driver.

## Quick Start

### Prerequisites

- .NET 10 SDK or runtime
- Desktop OpenGL 3.3 core support
- Linux/macOS source builds additionally require `make` and `gcc` or `clang`

### Supported Platforms

| Platform | Release target | Build-only target | PTY backend |
|---|---|---|---|
| Linux | x64 | arm64 | POSIX `pty-helper` |
| macOS | Intel, Apple Silicon | — | POSIX `pty-helper` |
| Windows 10 build 17763+ / Windows 11 | x64 | arm64 | ConPTY |

Linux arm64 and Windows arm64 are build targets until native runtime smoke
coverage promotes them to supported release targets.

### Build

On Linux or macOS, build the POSIX helper first:

```bash
make -C src/Dotty.NativePty
```

On Windows, no separate native helper build is required; ConPTY is provided by
the OS. All platforms then build the solution:

```bash
dotnet build Dotty.slnx -c Release
```

### Run

```bash
dotnet run --project src/Dotty/Dotty.csproj
```

### Test

```bash
dotnet test --solution Dotty.slnx -c Release
```

## Configuration

Dotty loads JSON configuration at startup and watches the file for atomic
updates. The active path is:

- Linux: `$XDG_CONFIG_HOME/dotty/config.json`, or `~/.config/dotty/config.json`
- macOS: `~/Library/Application Support/Dotty/config.json`
- Windows: `%APPDATA%/Dotty/config.json`

Set `DOTTY_CONFIG_HOME` to override the platform directory. The file is
created with defaults on first run.

```json
{
  "font": {
    "family": "JetBrains Mono, Cascadia Code, monospace",
    "size": 14,
    "lineHeight": 1.25
  },
  "window": {
    "padding": { "left": 14, "top": 8, "right": 14, "bottom": 8 },
    "opacity": 1
  },
  "theme": "DarkPlus",
  "cursor": { "shape": "Block", "blink": true, "blinkIntervalMs": 500 },
  "keybindings": {
    "ctrl+shift+t": "NewTab",
    "ctrl+shift+w": "ClosePane"
  }
}
```

Changes are debounced and applied on the desktop UI thread. Invalid JSON keeps
the last valid configuration and is reported through the host diagnostics.
Themes belong in the platform themes directory; Lua startup scripts (`config.lua`
or `init.lua`) belong in the platform configuration directory. See
[Configuration Guide](docs/Configuration.md) for the complete field list and
[Platform Support](docs/PlatformSupport.md) for path and troubleshooting details.

## Documentation

- [Architecture Overview](docs/Architecture.md)
- [Rendering System](docs/Rendering.md)
- [Parser Implementation](docs/Parsing.md)
- [Platform support and setup](docs/PlatformSupport.md)
- [Configuration Guide](docs/Configuration.md)
- [Advanced Configuration](docs/ConfigurationAdvanced.md)
- [Custom Themes](docs/CustomThemeArchitecture.md)
- [Themes](docs/Themes.md)
- [Native PTY](docs/NativePty.md)
- [Windows ConPTY guide](docs/WindowsConPty.md)
- [Testing](docs/Testing.md)
- [End-to-end smoke testing](docs/E2ETesting.md)
- [GUI Harness Benchmarking](docs/GuiHarnessBenchmarking.md)
- [Performance Guide](docs/Performance.md)
- [Comparison Report](docs/ComparisonReport.md)

## Repository Structure

```
src/
  Dotty/             — Silk.NET/OpenGL desktop host
  Dotty.Terminal/    — Terminal engine (parser, buffer, adapter)
  Dotty.Runtime/     — Sessions, tabs, input, config, scripting
  Dotty.NativePty/   — Unix helper and Windows ConPTY backends
  Dotty.Abstractions/ — Shared platform-neutral contracts
tests/               — Unit, native PTY, and rendering tests
docs/                — Architecture and platform guides
```

## License

MIT License - See [License](License.md) for details.

## Links

- Repository: https://github.com/dominic-codespoti/dotty
- Issues: https://github.com/dominic-codespoti/dotty/issues
