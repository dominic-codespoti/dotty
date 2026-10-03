# Dotty documentation

Start here: [README and quick start](../Readme.md) for the application, [Contributing](../Contributing.md) for developer setup/build/tests, and this index for reference docs. Documentation describes implemented behavior unless a page is explicitly marked as a historical design/plan.

## Architecture & Design

| Document | Description |
|----------|-------------|
| [Architecture Overview](Architecture.md) | Layered architecture, component diagram, data flow, platform abstraction |
| [Rendering Pipeline](Rendering.md) | GPU rendering via SkiaSharp, frame lifecycle, glyph atlas, performance optimizations |
| [GPU Rendering Migration Plan](architecture/GPURenderingPlan.md) | Historical proposal for the GPU renderer migration; consult [Rendering Pipeline](Rendering.md) for the current implementation |
| [Incremental Scroll Rendering](architecture/IncrementalScrollRendering.md) | Scroll-aware dirty tracking + region-memmove rendering: reverted from the live path, primitives tested for a future re-attempt |
| [State Coordination Hardening](architecture/StateCoordinationPlan.md) | Executed: library-owned buffer invariants, single-owner scroll state, dormant incremental machinery removed, alt-screen invalidation locked in |
| [Lock-Free Snapshot Design](architecture/LockFreeSnapshotDesign.md) | Snapshot ownership and lock-free rendering considerations |
| [Parsing Engine](Parsing.md) | ANSI/VT parser state machine, escape sequences, handler dispatch |
| [Keyboard Protocol](KeyboardProtocol.md) | Kitty negotiation, native key phases, alternate identities, and committed Unicode |

## Configuration

| Document | Description |
|----------|-------------|
| [Configuration Guide](Configuration.md) | User-facing config: fonts, colors, themes, key bindings, hot-reload, clipboard authorization |
| [Command-Line Usage](CommandLine.md) | Literal command arguments, launch directories, shell selection, and exit status |
| [Shell Integration](ShellIntegration.md) | Opt-in shell hooks, live working directories, prompt navigation, and output copy |
| [Advanced Configuration](ConfigurationAdvanced.md) | Platform-independent JSON details, themes, and safe reload |
| [Implementation Summary](ConfigurationImplementationSummary.md) | Technical summary of the config system implementation |
| [Configuration Roadmap](ConfigurationRoadmap.md) | Phased feature roadmap for the config system |
| [Themes](Themes.md) | Built-in theme palettes and JSON theme selection |
| [Configuration Walkthrough](guides/Configuration.md) | JSON configuration examples and troubleshooting |
| [Custom Theme Architecture](CustomThemeArchitecture.md) | Theme system internals and custom theme creation |

## Testing & Performance

| Document | Description |
|----------|-------------|
| [Testing Guide](Testing.md) | Test architecture, unit/integration/render tests |
| [E2E Testing](E2ETesting.md) | End-to-end testing via TCP command interface |
| [Performance Guide](Performance.md) | Benchmark workloads, recorded machine-specific measurements, and limitations; results are not universal performance guarantees |
| [GUI Harness Benchmarking](GuiHarnessBenchmarking.md) | Visual benchmark harness for render quality verification |

## Releases

| Document | Description |
|----------|-------------|
| [Release Policy](Releasing.md) | Central version, explicit stable/prerelease release triggers, readiness gates, and bump advice |

## Platform

| Document | Description |
|----------|-------------|
| [Native PTY](NativePty.md) | Unix PTY implementation (posix_openpt, forkpty) |
| [Native Desktop and IME Assessment](NativeDesktopAndIme.md) | Interactive native GUI verification, physical-input coverage boundaries, committed Unicode, and the separate composition boundary |
| [Platform Support](PlatformSupport.md) | OS requirements, native assets, diagnostics, and promotion gates |
| [Windows ConPTY](WindowsConPty.md) | Windows pseudo-console API integration |

## Comparisons & Reports

| Document | Description |
|----------|-------------|
| [Comparison Report](ComparisonReport.md) | Dotty vs Ghostty vs Wezterm feature comparison |

---

*Last updated: 2026-10-03*
