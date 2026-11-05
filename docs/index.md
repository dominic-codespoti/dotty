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
| [Parsing Engine](Parsing.md) | ANSI/VT parser state machine, string/control rules, escape sequences, handler dispatch, OSC 7/8/52 behavior and limits, DECSTR/RIS, status and window reports, DECRQCRA, grapheme limits, and the DECRQM table |
| [Kitty Graphics Protocol](KittyGraphics.md) | Inline images: supported/unsupported matrix, media and path policy, limits, memory and threading model, tmux and Windows notes, divergences from kitty, default-off flag, Linux smoke-test scope, and the remaining manual checklist |
| [Keyboard Protocol](KeyboardProtocol.md) | Kitty negotiation, native key phases, alternate identities, committed Unicode, and DECBKM |

## Configuration

| Document | Description |
|----------|-------------|
| [Configuration Guide](Configuration.md) | User-facing config: fonts, colors, themes, key bindings, hot-reload, clipboard (OSC 52 write/read, paste confirmation) and hyperlink policy, launch environment |
| [Command-Line Usage](CommandLine.md) | Literal command arguments, launch directories, shell selection, exit status, and the `+explain-config` action |
| [Shell Integration](ShellIntegration.md) | Opt-in shell hooks and automatic injection (`shellIntegration`), live working directories (strict OSC 7), prompt navigation, and output copy |
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
| [Testing on a laptop](LaptopTesting.md) | Build, `scripts/dev/check.sh`, run with a throwaway config, and the manual checklist for HiDPI, fonts, input, IME and graphics |
| [E2E Testing](E2ETesting.md) | End-to-end testing via TCP command interface; manual OSC 52 read, paste, and link confirmation smoke |
| [Performance Guide](Performance.md) | Benchmark workloads, recorded machine-specific measurements, and limitations; results are not universal performance guarantees |
| [GUI Harness Benchmarking](GuiHarnessBenchmarking.md) | Visual benchmark harness for render quality verification |
| [VT Conformance](Conformance.md) | esctest2 harness (Linux only): headless host, runner, expected failures, baseline capture, and gating |

## Releases

| Document | Description |
|----------|-------------|
| [Release Policy](Releasing.md) | Central version, explicit stable/prerelease release triggers, readiness gates, and bump advice |
| [Install](Install.md) | Release archives, user-local `install.sh` (desktop entry, icons, terminfo, man page, completions, uninstall), packages, source installs |
| [Packaging](Packaging.md) | Publish layout, nfpm `.deb`/`.rpm`, generated man page and completions, notices/NuGet inventory check, size and start-time measurement and budgets |
| [Troubleshooting](Troubleshooting.md) | `+diagnose`, GPU requirements (OpenGL 3.3 core), Mesa llvmpipe workaround, helper/terminfo/completion problems |

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

*Last updated: 2026-11-05*
