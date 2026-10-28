# AI Agents Instructions for Dotty

This `Agents.md` provides best practices and guidelines for AI agents working in
this repository.

## General objective

Dotty is a terminal emulator in .NET. The desktop host is built on Silk.NET,
GLFW, OpenGL 3.3, and SkiaSharp. Prioritize correctness, allocation-free hot
paths (parsing, buffer mutation, rendering), and cross-platform behavior on
Linux, macOS, and Windows.

## Project layout

| Path | Responsibility |
|---|---|
| `src/Dotty/` | Silk.NET/OpenGL desktop host: window lifecycle, input, rendering, scene composition |
| `src/Dotty.Rendering.Gpu/` | GPU resources, shaders, glyph atlas, quad batching |
| `src/Dotty.Runtime/` | Sessions, tabs, panes, themes, configuration, scripting |
| `src/Dotty.Terminal/` | Terminal core: ANSI parser, cell buffer, scrollback, snapshots |
| `src/Dotty.Abstractions/` | Platform-neutral contracts, defaults, built-in themes |
| `src/Dotty.NativePty/` | POSIX `pty-helper` and Windows ConPTY backends |
| `tests/` | Unit, terminal, native PTY, Skia, and performance test projects |
| `scripts/perf/` | Benchmark harnesses used by the performance skill |
| `scripts/esctest/` | esctest2 conformance runner and result parser (Python) |
| `tests/Dotty.Conformance.Host/` | Headless host executable that the esctest run drives (not a test project) |
| `tests/esctest/` | Pinned esctest2 commit and the expected-failures baseline |

## Architecture references

Read the relevant document before changing a subsystem:

- [Architecture](./docs/Architecture.md) — project layering and dependencies.
- [Rendering](./docs/Rendering.md) — glyph atlas, quad batching, frame flow.
- [Parsing](./docs/Parsing.md) — control codes, escape sequence handling.
- [Native PTY](./docs/NativePty.md) — helper protocol and process isolation.
- [Windows ConPTY](./docs/WindowsConPty.md) — ConPTY startup and handle rules.
- [Platform Support](./docs/PlatformSupport.md) — support matrix, diagnostics,
  promotion gates.
- [Testing](./docs/Testing.md) and [E2E Testing](./docs/E2ETesting.md) — test
  layout, smoke contracts, control interface.
- [Configuration](./docs/Configuration.md) — JSON schema and hot reload.
- [Conformance](./docs/Conformance.md) — esctest2 harness, expected failures,
  baseline capture, and gating.

## Working rules

1. Identify the subsystem first, then read only the documents and directories
   that subsystem touches.
2. Prefer `Span<T>`/`ref struct` for buffer work; avoid boxing and LINQ in
   parsing and rendering paths.
3. Terminal behavior changes need a regression test in `tests/Dotty.Terminal.Tests/`
   or `tests/Dotty.App.Tests/`; a test must fail before the fix and pass after.
4. GUI changes cannot be proven by unit tests alone. Verify startup with the
   X11 smoke described in [E2E Testing](./docs/E2ETesting.md), or with the
   loopback control interface (`DOTTY_TEST_PORT`).
5. Keep public contracts in `src/Dotty.Abstractions/` free of host-specific
   types; they are shared between the host projects.

## Documentation maintenance

- Update the affected `docs/*.md` file in the same change as the code.
- Add newly created documentation categories to the reference list above.
- Never describe CI coverage that does not exist: CI builds and tests every
  supported platform, verifies published native assets, smokes GUI startup
  on Ubuntu under Xvfb only, and runs esctest2 conformance on Linux only (not
  gating until a baseline is captured). Wayland, macOS, and Windows GUI startup
  are verified manually.

## Project guardrails

- `src/Dotty.Abstractions` is an internal project, not a NuGet package
  (scripting goes through Lua), so its public types may be changed or
  renamed freely as long as every in-repo caller is updated.
- Unix PTY startup requires the `pty-helper` binary beside the host; Windows
  uses ConPTY and requires build 17763 or newer.
- Do not reintroduce Avalonia: the desktop host is Silk.NET/OpenGL only.
