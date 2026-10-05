# Dotty

A cross-platform desktop terminal emulator built with .NET 10, Silk.NET,
GLFW, OpenGL 3.3, and SkiaSharp.

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](License.md)

## Overview

Dotty is a modern terminal emulator composed of:
- **Dotty** — Silk.NET/OpenGL desktop host.
- **Dotty.Rendering.Gpu** — Glyph atlases, shaders, and instanced quad rendering.
- **Dotty.Runtime** — Sessions, tabs, split panes, configuration, and Lua scripting.
- **Dotty.Terminal** — ANSI/VT parsing, cell buffers, and scrollback.
- **Dotty.NativePty** — Unix helper and Windows ConPTY backends.
- **Dotty.Abstractions** — Platform-neutral contracts.

### Key Features

- Hardware-accelerated OpenGL rendering with SkiaSharp font shaping
- Multiple tabs, split panes, per-tab search, and configurable keyboard shortcuts
- Custom JSON themes and Lua startup configuration, tab titles, and status text
- Native PTY support on Linux, macOS, and Windows
- Efficient cell-preserving buffer and scrollback reflow
- Ligature support via HarfBuzz font shaping
- Undercurl, dotted, and dashed underline rendering
- Rounded rectangle clip regions for modern terminal aesthetics
- Runtime JSON configuration hot-reload with platform-specific paths
- [Kitty keyboard negotiation](docs/KeyboardProtocol.md), native press/repeat/release, and committed Unicode scalars
- [Direct command launches](docs/CommandLine.md) with literal arguments, working directories, and child exit status
- Explicit OSC 52 clipboard-write authorization (denied by default)
- Opt-in [OSC 7/133 shell integration](docs/ShellIntegration.md): live directories, prompt navigation, and command-output copy
- [Performance regression snapshots](docs/Performance.md) and [native desktop verification lanes](docs/NativeDesktopAndIme.md)

## Install

For the latest merged code, [build `main` from source](#build) and
[publish a self-contained executable](#publish). A merge does not publish a new
release, and the historical `nightly` archive is no longer updated.

Versioned self-contained archives are published on
[GitHub Releases](https://github.com/dominic-codespoti/dotty/releases) only when a
release is explicitly requested. See the [release policy](docs/Releasing.md) for
versioning and publication. Release signing and a graphical installer are not
currently provided.

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

- .NET SDK 10.0.100+; [global.json](global.json) allows .NET 10 feature-band roll-forward. A runtime alone is not enough to build.
- Python 3 and CMake on `PATH`: build and publish compile the checked-in GLFW fork automatically.
- Desktop OpenGL 3.3 core support.
- **Windows:** Visual Studio 2022 C/C++ build tools and a Windows SDK, including the native import libraries. ConPTY itself is supplied by Windows; GLFW and NativeAOT still require the native toolchain.
- **Linux:** a C compiler, `make`, `pkg-config`, X11/Wayland development headers and protocols, and `libxkbcommon` development headers. NativeAOT publishing additionally requires Clang and zlib development headers.
- **macOS:** Xcode Command Line Tools (`clang` and `make`).

See [native desktop build prerequisites](docs/NativeDesktopAndIme.md#native-glfw-dependency)
for the GLFW dependency and platform packages.
The compiler requirements for publishing are also covered by Microsoft's
[NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites).

### Supported Platforms

| Platform | Release target | Build-only target | PTY backend |
|---|---|---|---|
| Linux | x64 | arm64 | POSIX `pty-helper` |
| macOS | Intel, Apple Silicon | — | POSIX `pty-helper` |
| Windows 10 build 17763+ / Windows 11 | x64 | arm64 | ConPTY |

Linux arm64 and Windows arm64 are build targets until native runtime smoke
coverage promotes them to supported release targets.

A green build or headless test run does not establish native desktop behavior: CI's Xvfb smoke is X11-only; Wayland, macOS, Windows, and physical-input checks require native interactive sessions. See [native desktop verification](docs/NativeDesktopAndIme.md).

### Build

Start from the latest merged branch rather than the historical nightly:

```bash
git clone --branch main https://github.com/dominic-codespoti/dotty.git
cd dotty
```

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
# Forward arguments after dotnet run's separator:
dotnet run --project src/Dotty/Dotty.csproj -- -d "$PWD" -- nvim .
```

For a published executable, use `dotty -d DIR -- COMMAND ARG...` or
`dotty --shell PATH`. `dotty --help` and `dotty --version` do not initialize
graphics. With no arguments, the interactive shell starts in the invocation
directory. See [command-line usage](docs/CommandLine.md) for details.

On Windows, a standalone Desktop/Start-menu launch releases its startup console
before opening the GUI. Launches from an existing console keep that console
attached, and redirected standard handles are preserved.

### Publish

Publish builds a self-contained NativeAOT executable with the portable CPU
baseline. Keep the entire output directory together; the executable still needs
the included native libraries and, on Unix, `pty-helper`.

Windows x64:

```powershell
dotnet publish src/Dotty/Dotty.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
.\publish\win-x64\dotty.exe
# Select PowerShell 7 explicitly when it is installed:
.\publish\win-x64\dotty.exe --shell "C:\Program Files\PowerShell\7\pwsh.exe" -d "$HOME"
```

Linux x64 (after building the POSIX helper above):

```bash
dotnet publish src/Dotty/Dotty.csproj -c Release -r linux-x64 --self-contained true -p:RequireUnixPtyHelper=true -o publish/linux-x64
./publish/linux-x64/dotty
```

For macOS, replace `linux-x64` with `osx-x64` (Intel) or `osx-arm64` (Apple
Silicon) in both paths. Build and publish on the target OS and architecture.

Clipboard writes requested by terminal applications through OSC 52 require
`"clipboard": { "allowOsc52Write": true }` in `config.json`. This permission
does not disable user-initiated copy/paste, and Lua hooks may veto but cannot
override a global denial. See the [clipboard policy](docs/Configuration.md#clipboard).

### Test

The repository uses Microsoft.Testing.Platform. Run the solution tests with the .NET 10 SDK:

```bash
dotnet test --solution Dotty.slnx -c Release
```

See the [testing guide](docs/Testing.md) for scope and procedures, and [Contributing](Contributing.md#tests) for setup and a note about diagnosing zero-test results. Test and benchmark results depend on workload and machine; see [Performance](docs/Performance.md) for recorded evidence and its limits.

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
[Configuration reference](docs/Configuration.md) for the complete field list and
[Platform Support](docs/PlatformSupport.md) for path and troubleshooting details.

### Controls at a glance

Common built-in chords include `ctrl+shift+t` (new tab),
`ctrl+shift+w` (close the focused pane, or the tab when it is the only pane),
`ctrl+tab`/`ctrl+shift+tab` (next/previous tab), `alt+1` through `alt+9`
(switch tab), `ctrl+shift+c`/`ctrl+shift+v` (copy/paste),
`ctrl+shift+f` (search), `ctrl+shift+d`/`ctrl+shift+s` (split),
`alt+left`/`alt+right`/`alt+up`/`alt+down` (focus a pane), `f11` (fullscreen),
`ctrl+plus`/`ctrl+minus` (zoom), `ctrl+0` (reset zoom), and `ctrl+shift+q`
(quit). `CloseTab` has no built-in chord; the full reference also lists every
other configurable action without a default binding.

Search is per tab, includes scrollback, and reveals the selected match.
Pointer input targets the pane under the pointer; terminal mouse reporting
takes precedence, while `Shift` exposes local scrolling and selection. See the
[Configuration reference](docs/Configuration.md#keybindings) for the exact
chord/action table, search editing rules, and pointer ergonomics, or the
[Configuration walkthrough](docs/guides/Configuration.md) for examples.

## Documentation

Start with the [documentation index](docs/index.md) for the current catalog. Common starting points:

- [Contributing and developer setup](Contributing.md)
- [Platform support and setup](docs/PlatformSupport.md)
- [Configuration Guide](docs/Configuration.md)
- [Command-line usage](docs/CommandLine.md)
- [Architecture Overview](docs/Architecture.md)
- [Testing](docs/Testing.md) and [performance evidence](docs/Performance.md)
- [Native desktop and IME verification](docs/NativeDesktopAndIme.md)
- [Release policy](docs/Releasing.md)

## Repository Structure

```
src/
  Dotty/             — Silk.NET/OpenGL desktop host
  Dotty.Terminal/    — Terminal engine (parser, buffer, adapter)
  Dotty.Runtime/     — Sessions, tabs, input, config, scripting
  Dotty.NativePty/   — Unix helper and Windows ConPTY backends
  Dotty.Abstractions/ — Shared platform-neutral contracts
  Dotty.Rendering.Gpu/ — Glyph atlases, shaders, and GPU quad batching
tests/               — Unit, native PTY, and rendering tests
docs/                — Architecture and platform guides
```

## License

MIT License - See [License](License.md) for details.

## Links

- Repository: https://github.com/dominic-codespoti/dotty
- Issues: https://github.com/dominic-codespoti/dotty/issues
