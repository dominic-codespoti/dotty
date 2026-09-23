# Terminal Emulator Comparison: Dotty vs. Ghostty & Wezterm

This report records a comparison made on 2026-06-17. The original comparison
included the then-current Avalonia host; that architecture is historical. The
current host is documented as Silk.NET/GLFW/OpenGL with SkiaSharp.

## 1. Architectural & Language Differences

| Feature | Dotty | Ghostty | Wezterm |
| :--- | :--- | :--- | :--- |
| **Core Language** | C# (.NET 10) | Zig | Rust |
| **Memory Management** | Managed (Garbage Collected with `Span<T>`/`ref struct` optimizations) | Manual / Arenas | Borrow Checker / Safe Manual |
| **UI Framework** | Silk.NET/GLFW windowing with an OpenGL 3.3 renderer | Custom / Native (AppKit/GTK) | Custom Windowing (Mux/GUI split) |
| **Configuration** | JSON with atomic hot reload plus a Lua startup script API for configuration, keybindings, tabs/panes, and event hooks | Plain text configuration | Lua scripting engine |

**Analysis:**
Both Ghostty and Wezterm are written in low-level systems programming languages without a Garbage Collector runtime. While Dotty employs aggressive memory optimization techniques (zero-allocation parsing, `Span<T>`), it fundamentally relies on the .NET runtime. This means Dotty always carries a heavier baseline memory footprint and is subject to potential GC pauses, unlike the deterministic memory models of Zig and Rust.

## 2. Rendering Mechanisms & GPU Acceleration

*   **Dotty:** Uses a Silk.NET/GLFW host with an OpenGL 3.3 core renderer. SkiaSharp and the shared glyph-atlas/shaping pipeline prepare glyph coverage; `TerminalSceneComposer` emits instanced cell and chrome quads.
*   **Ghostty:** Features a custom, highly optimized GPU renderer (Metal/Vulkan/OpenGL) designed for absolute minimum latency (sub-millisecond frame dispatch) and custom font rasterization. 
*   **Wezterm:** Uses a native OpenGL/EGL hardware-accelerated rendering pipeline. It heavily supports complex text shaping (Harfbuzz), ligatures, and fallback fonts.

**Where Dotty Differs/Misses:**
*   **Complex Text Layout (CTL):** Dotty now supports programming ligatures via HarfBuzz (`TextShaper`/`ShapedRun`) for patterns like `=>`, `!=`, `::`. Arabic/Indic script shaping and color emoji fallback remain areas for improvement compared to Wezterm and Ghostty.
*   **Underline Styles:** Dotty now supports undercurl (wavy), dotted, and dashed underline styles in addition to standard underline — matching the rendering capabilities of Wezterm and Ghostty for editor diagnostics.
*   **Rounded Corners:** Dotty's OpenGL chrome shader computes rounded terminal-frame geometry directly; this is a modern terminal-window feature also present in Ghostty and kitty.
*   **Direct GPU Control:** Dotty's current host submits directly to an OpenGL 3.3 context through Silk.NET. Ghostty and Wezterm interface even closer to their platform GPU APIs, allowing specialized shaders and tighter platform-specific latency control.

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

**Comparison notes:**
*   **Multiplexing (tmux-like behavior):** Wezterm has a built-in client/server architecture allowing users to detach and reattach to terminal sessions locally or over SSH. Dotty provides local terminal sessions, tabs, and panes but no client/server multiplexer.
*   **Scriptability:** Dotty exposes Lua configuration, keybinding callbacks, tab/pane operations, timers, actions, and event/value hooks; Wezterm's Lua engine exposes a broader client/server and event-hook surface.
*   **Split Panes and Native Tabs:** Dotty provides tabs and split panes in its runtime. Ghostty and Wezterm also provide built-in window management.
*   **Image Protocol Support:** Wezterm supports the Kitty image protocol and iTerm2 image protocols to display inline graphics; Dotty does not currently document those protocols as supported.

## 5. Strategic Recommendations for Dotty

If Dotty aims to compete or find a specific niche against these giants, it should focus on:
## Changelog

| Date | Change |
|------|--------|
| 2026-06-17 | Updated CTL section: ligatures via HarfBuzz now implemented; added underline styles and rounded corners to comparison |
| 2026-06-15 | Updated roadmap: ligature item re-scoped to emoji/script shaping |

---

1.  **Exploiting the .NET Ecosystem:** Offer deep integrations for .NET developers (e.g., built-in structured logging parsing, intelligent C# repl integrations, MSBuild hot-links).
2.  **Native host polish:** Continue improving platform-specific graphics, input, and accessibility behavior while preserving the shared terminal core.
3.  **Emoji & Script Shaping:** Build on the existing HarfBuzz integration to support color emoji and complex script (Arabic/Indic) shaping, further closing the visual gap with Wezterm.

*Last updated: 2026-06-17*
