# Dotty (dotnet-term)

A small terminal emulator and related libraries for .NET, using Avalonia for the UI and a tiny native pty helper for POSIX platforms.

## Overview

- Dotty is composed of a UI application (`Dotty.App`), a terminal core library (`Dotty.Terminal`) and a small native helper to allocate pseudo-terminals (`Dotty.NativePty`).
- The UI is built with Avalonia and provides a canvas-based terminal view. The terminal core handles parsing and rendering terminal sequences.

## Repository layout

- `src/Dotty.App/` — Avalonia application; main UI and entrypoint.
- `src/Dotty.Terminal/` — Terminal logic, parsers, buffer, adapter interfaces.
- `src/Dotty.NativePty/` — Native pty helper (C) plus a `Makefile` to build `pty-helper`.
- `tests/` — Unit tests (e.g. `tests/Dotty.App.Tests`).
- `scripts/` — Utility scripts for development and running the app (e.g., `scripts/run.sh`).

## Requirements

- .NET SDK (recommended: .NET 10; the project also contains outputs for `net9.0`).
- `make`, `gcc`/`clang` and typical Linux dev tools to build `Dotty.NativePty` (only required for running the native helper).
- Tested on Linux; the native pty helper is POSIX-specific.

## Building

1. Build the native pty helper (needed for proper pty support on Linux):

```bash
cd src/Dotty.NativePty
make
# result: src/Dotty.NativePty/bin/pty-helper
```

2. Build the .NET solution:

```bash
cd /home/dom/projects/dotnet-term
dotnet build Dotty.sln --configuration Debug
```

You can also build `Release`:

```bash
dotnet build Dotty.sln --configuration Release
```

## Running

- Run from source (choose target framework if needed):

```bash
dotnet run --project src/Dotty.App
```

- Or execute the built binary (example path for Debug/net10.0 on Linux):

```bash
./src/Dotty.App/bin/Debug/net10.0/linux-x64/Dotty.App
```

- Ensure the native helper is present and executable if you're running features that require a PTY:

```bash
ls -l src/Dotty.NativePty/bin/pty-helper
# if needed: chmod +x src/Dotty.NativePty/bin/pty-helper
```

## Tests

Run unit tests with:

```bash
dotnet test tests/Dotty.App.Tests
```

## Troubleshooting

- If the app fails to allocate a PTY, confirm `pty-helper` was built and is executable.
- If you see runtime/compatibility issues, ensure your installed .NET SDK version matches one of the targeted frameworks (the repo contains outputs for `net9.0` and `net10.0`).
