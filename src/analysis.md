# Architectural and Feature Analysis: Dotty Terminal Application

## Executive Summary

The Dotty terminal application is a modern terminal emulator built with Avalonia UI (.NET) and a C-based native PTY helper. It features a separation of concerns across multiple projects (`Dotty.Abstractions`, `Dotty.App`, `Dotty.NativePty`, `Dotty.Terminal`). The architecture leans on modern cross-platform .NET but splits out the PTY allocation to a native executable to avoid complex managed-to-native marshaling for low-level TTY setup.

## 1. Architectural Overview

The application is structured into clearly delineated projects:
- **Dotty.Abstractions**: Interfaces and shared models.
- **Dotty.App**: The Avalonia-based GUI frontend, responsible for rendering and user input.
- **Dotty.NativePty**: A C-based helper (`pty-helper`) that cleanly handles `forkpty`, PTY proxying, and resizing via UNIX sockets.
- **Dotty.Terminal**: Core VT parsing, escape sequence handling, and terminal state machine implementation.

This modularity allows the terminal core to be tested independently of the UI and OS-specific PTY mechanics.

## 2. Feature Analysis

### PTY Handling (Dotty.NativePty)
**Current State**: 
- Uses a standalone native helper (`pty-helper`) rather than P/Invoking `forkpty` directly from C#.
- Proxies the master PTY file descriptor to stdin/stdout.
- Supports resizing via a UNIX domain control socket (`DOTTY_CONTROL_SOCKET`), receiving JSON messages (e.g., `{"type":"resize","cols":100,"rows":30}`).

**Pros & Cons**:
- *Pro*: Highly stable, avoids .NET runtime issues with `fork()`.
- *Con*: Requires a separate binary compilation step (via `Makefile`), meaning cross-platform distribution (e.g., Windows/macOS/Linux) requires native toolchains per target. Windows support is likely missing here since it relies on `forkpty` and UNIX sockets.

### Rendering Pipeline (Dotty.App)
**Current State**:
- Uses Avalonia UI for cross-platform rendering.
- Implements a custom canvas/drawing routine (`TerminalCanvas`, `TerminalFrameComposer`).
- Supports SKia-based drawing (likely using `SKColor` inside Avalonia's SKia backend).

**Areas for Improvement**:
- Need to ensure ligatures, fallback fonts, and complex Unicode (emojis, ZWJ sequences) are supported correctly.
- Performance: Rendering full terminal grids efficiently requires dirty region tracking (`BlitBackgroundRegions` is present, indicating optimized drawing). 

### VT Sequence Parsing (Dotty.Terminal)
**Current State**:
- Houses the parser and terminal emulator state.
- Expected to handle ANSI/VT100/Xterm sequences (colors, cursor movement, modes).

**Areas for Improvement**:
- Comprehensive testing against standard test suites (e.g., `vttest`) is required.
- Advanced features like Sixel graphics, true-color (24-bit), and Kitty keyboard protocols might be incomplete or missing.

### Input Handling
**Current State**:
- Key presses in Avalonia are translated into terminal escape sequences and sent to the PTY helper's standard input.

**Areas for Improvement**:
- Handling modifier keys (Alt/Meta, Ctrl) correctly across different OS keyboard layouts.
- Mouse reporting (X10, SGR, URXVT) needs to be mapped from Avalonia pointer events to terminal sequences.

## 3. Missing Features & Standard Terminal Comparisons

Compared to modern standard terminals (e.g., Alacritty, Windows Terminal, Kitty):
1. **Windows Support**: The `pty-helper` is UNIX-centric (`forkpty`). Windows requires the ConPTY API.
2. **Hardware Acceleration**: While Avalonia uses Skia/OpenGL, specialized terminals use custom GPU shaders (WebGL/Vulkan/Metal) for ultra-low latency. 
3. **Advanced VT Extensions**: Support for OSC 8 (hyperlinks), Sixel/iTerm image protocols, and synchronized updates (DCS = 1 s).
4. **Configuration & Theming**: Lack of a clear user-configurable `settings.json` hot-reload system for colors, fonts, and keybindings.

## 4. Recommendations
- Implement ConPTY for native Windows support without WSL.
- Expand `Dotty.Terminal` tests to cover edge cases in UTF-8 parsing and CJK double-width characters.
- Implement mouse tracking mode (SGR 1006) for interactive terminal applications like `htop` or `vim`.