# Dotty Terminal Emulator — Documentation

## Architecture & Design

| Document | Description |
|----------|-------------|
| [Architecture Overview](Architecture.md) | Layered architecture, component diagram, data flow, platform abstraction |
| [Rendering Pipeline](Rendering.md) | GPU rendering via SkiaSharp, frame lifecycle, glyph atlas, performance optimizations |
| [Avalonia Optimization Plan](architecture/AvaloniaOptimizationPlan.md) | Active long-term roadmap for DPI correctness, demand-driven scheduling, bounded memory, renderer measurement, and native Avalonia UX |
| [GPU Rendering Migration Plan](architecture/GPURenderingPlan.md) | Active: A8 glyph atlas + quad-batched renderer to replace the CPU raster path (branch `feat/gpu-rendering`) |
| [Incremental Scroll Rendering](architecture/IncrementalScrollRendering.md) | Scroll-aware dirty tracking + region-memmove rendering: reverted from the live path, primitives tested for a future re-attempt |
| [State Coordination Hardening](architecture/StateCoordinationPlan.md) | Executed: library-owned buffer invariants, single-owner scroll state, dormant incremental machinery removed, alt-screen invalidation locked in |
| [Parsing Engine](Parsing.md) | ANSI/VT parser state machine, escape sequences, handler dispatch |
| [Source Generator Architecture](architecture/ConfigSourceGenerator.md) | Build-time C# code generation for zero-overhead configuration |

## Configuration

| Document | Description |
|----------|-------------|
| [Configuration Guide](Configuration.md) | User-facing config: fonts, colors, themes, key bindings, hot-reload |
| [Advanced Configuration](ConfigurationAdvanced.md) | Roadmap items and future config enhancements |
| [Implementation Summary](ConfigurationImplementationSummary.md) | Technical summary of the config system implementation |
| [Configuration Roadmap](ConfigurationRoadmap.md) | Phased feature roadmap for the config system |
| [Custom Theme Architecture](CustomThemeArchitecture.md) | Theme system internals and custom theme creation |

## Testing & Performance

| Document | Description |
|----------|-------------|
| [Testing Guide](Testing.md) | Test architecture, unit/integration/render tests |
| [E2E Testing](E2ETesting.md) | End-to-end testing via TCP command interface |
| [Performance Guide](Performance.md) | Benchmarks, allocation profiles, cold-start optimization |
| [GUI Harness Benchmarking](GuiHarnessBenchmarking.md) | Visual benchmark harness for render quality verification |

## Platform

| Document | Description |
|----------|-------------|
| [Native PTY](NativePty.md) | Unix PTY implementation (posix_openpt, forkpty) |
| [Windows ConPTY](WindowsConPty.md) | Windows pseudo-console API integration |

## Comparisons & Reports

| Document | Description |
|----------|-------------|
| [Comparison Report](ComparisonReport.md) | Dotty vs Ghostty vs Wezterm feature comparison |

---

*Last updated: 2026-08-13*
