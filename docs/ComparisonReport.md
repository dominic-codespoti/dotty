# Terminal Emulator Comparison: Dotty vs. Ghostty & Wezterm

This report analyzes the architectural, technological, and feature-level differences between Dotty and industry-leading modern terminal emulators, specifically **Ghostty** and **Wezterm**.

## 1. Architectural & Language Differences

| Feature | Dotty | Ghostty | Wezterm |
| :--- | :--- | :--- | :--- |
| **Core Language** | C# (.NET 10) / Avalonia | Zig | Rust |
| **Memory Management** | Managed (Garbage Collected with `Span<T>`/`ref struct` optimizations) | Manual / Arenas | Borrow Checker / Safe Manual |
| **UI Framework** | Avalonia UI | Custom / Native (AppKit/GTK) | Custom Windowing (Mux/GUI split) |
| **Configuration** | Static / TBD | Plain text configuration | Lua Scripting Engine |

**Analysis:**
Both Ghostty and Wezterm are written in low-level systems programming languages without a Garbage Collector runtime. While Dotty employs aggressive memory optimization techniques (zero-allocation parsing, `Span<T>`), it fundamentally relies on the .NET runtime. This means Dotty always carries a heavier baseline memory footprint and is subject to potential GC pauses, unlike the deterministic memory models of Zig and Rust.

## 2. Rendering Mechanisms & GPU Acceleration

*   **Dotty:** Leverages Avalonia's rendering engine (typically Skia-based) using a `TerminalCanvas` and a `GlyphAtlas` cache. It splits background synthesis from text rendering.
*   **Ghostty:** Features a custom, highly optimized GPU renderer (Metal/Vulkan/OpenGL) designed for absolute minimum latency (sub-millisecond frame dispatch) and custom font rasterization. 
*   **Wezterm:** Uses a native OpenGL/EGL hardware-accelerated rendering pipeline. It heavily supports complex text shaping (Harfbuzz), ligatures, and fallback fonts.

**Where Dotty Differs/Misses:**
*   **Complex Text Layout (CTL):** Dotty now supports programming ligatures via HarfBuzz (`TextShaper`/`ShapedRun`) for patterns like `=>`, `!=`, `::`. Arabic/Indic script shaping and color emoji fallback remain areas for improvement compared to Wezterm and Ghostty.
*   **Underline Styles:** Dotty now supports undercurl (wavy), dotted, and dashed underline styles in addition to standard underline — matching the rendering capabilities of Wezterm and Ghostty for editor diagnostics.
*   **Rounded Corners:** Dotty applies `ClipRoundRect()` for rounded terminal frame corners, a modern aesthetic feature present in Ghostty and kitty.
*   **Direct GPU Control:** Dotty is bound by Avalonia's abstractions. Ghostty and Wezterm interface much closer to the GPU, allowing for specialized shaders (e.g., CRT effects in Wezterm) and tighter VSync/latency control.

## 3. Platform & PTY Integration

Dotty uses a platform-neutral `IPty` abstraction:

- Linux and macOS use the POSIX `pty-helper` backend.
- Windows 10 build 17763+ and Windows 11 use ConPTY.
- The desktop host is Silk.NET/OpenGL.

Release artifacts currently target Linux x64, macOS x64/arm64, and Windows
x64. Linux arm64 and Windows arm64 are build targets pending runtime smoke
coverage. Helper discovery and artifact provisioning are part of the release
contract; the helper must not depend on repository-relative paths.

## 4. Advanced Features & Multiplexing

**What Dotty is Missing:**
*   **Multiplexing (tmux-like behavior):** Wezterm has a built-in client/server architecture allowing users to detach and reattach to terminal sessions locally or over SSH. Dotty is strictly a local rendering wrapper.
*   **Scriptability:** Wezterm's Lua engine allows endless user customization of keybinds, appearance, and event hooks dynamically.
*   **Split Panes and Native Tabs:** Ghostty and Wezterm have robust built-in window management (tabs, split panes). Dotty appears to rely on standard Avalonia controls (if any) or expects external multiplexers (like tmux) to handle tiling.
*   **Image Protocol Support:** Wezterm supports the Kitty image protocol and iTerm2 image protocols to display inline graphics.

## 5. Strategic Recommendations for Dotty

If Dotty aims to compete or find a specific niche against these giants, it should focus on:
## Changelog

| Date | Change |
|------|--------|
| 2026-06-17 | Updated CTL section: ligatures via HarfBuzz now implemented; added underline styles and rounded corners to comparison |
| 2026-06-15 | Updated roadmap: ligature item re-scoped to emoji/script shaping |

---

1.  **Exploiting the .NET Ecosystem:** Offer deep integrations for .NET developers (e.g., built-in structured logging parsing, intelligent C# repl integrations, MSBuild hot-links).
2.  **Windows Port:** Implement Windows `ConPty` via P/Invoke to make Dotty optionally cross-platform.
3.  **Emoji & Script Shaping:** Build on the existing HarfBuzz integration to support color emoji and complex script (Arabic/Indic) shaping, further closing the visual gap with Wezterm.

*Last updated: 2026-06-17*
