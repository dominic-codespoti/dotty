# Dotty Terminal: Comprehensive Analysis & Findings Report

This report consolidates the architectural, UI/UX, and visual rendering analysis of the Dotty terminal application (`src/Dotty.App`, `src/Dotty.Terminal`, `src/Dotty.NativePty`).

## 1. UI & UX Analysis

### Tab Management
*   **Current State:** Tabs are managed effectively via a custom `TabStrip` and an underlying `ItemsControl`. Inline renaming, duplication, and closing functionality exists via a context menu.
*   **Performance Issue:** The `ItemsControl` renders all terminal canvas instances simultaneously and simply toggles `IsVisible`. Over time, keeping dozens of terminal sessions actively in the visual tree will consume massive memory and UI thread resources.
*   **Recommendation:** Refactor tab content rendering to swap the `DataContext` of a single `TerminalView` bound directly to `ActiveTab`, or implement strict layout virtualization so background terminals are not ticked by the Avalonia rendering loop. Add a persistent **`+`** button to the tab bar to make new tab creation discoverable.

### Styling & Theming
*   **Current State:** The application utilizes a hardcoded dark theme directly within `MainWindow.axaml` (e.g., `#181818`, `#2A2A2A`), while the terminal canvas leverages `DynamicResource`.
*   **Recommendation:** Extract all hardcoded hex values into a `ResourceDictionary` in `App.axaml`. This will significantly ease the future implementation of light/dark modes and user-customizable color palettes.

### Desktop Integration
*   **Current State:** Keyboard shortcuts for adding/closing tabs (`Ctrl+Shift+T`, `Ctrl+Shift+W`) are hardcoded in the `MainWindow` key handler. 
*   **Recommendation:** Move to an Avalonia `KeyBinding` or `RoutedCommand` architecture, making shortcuts customizable. Display tooltips or an app menu so users can discover these bindings.

---

## 2. Architectural Analysis

The solution is properly decoupled:
*   `Dotty.Abstractions`: Shared contracts.
*   `Dotty.App`: Avalonia-based GUI.
*   `Dotty.Terminal`: VT parsing backend.
*   `Dotty.NativePty`: Native C unix domain socket PTY proxy.

### PTY Offloading (`Dotty.NativePty`)
*   **Strength:** Handling `forkpty` in a standalone Unix C process (`pty-helper`) avoids runtime faults in the managed .NET process.
*   **Weakness:** It natively binds exactly to Linux/macOS toolchains. *Windows support is structurally missing.* A ConPTY integration needs to be introduced to support native Windows execution.

### Rendering Engine (`Dotty.App / TerminalCanvas`)
*   **Strength:** Uses SkiaSharp directly for low-level drawing (`TerminalFrameComposer`), avoiding slower Avalonia controls for individual grid cells. Frame delta rendering (like `BlitBackgroundRegions`) is implemented to limit draw calls.
*   **Weakness:** The Avalonia render thread might still get choked by extreme log-floods (e.g., `cat /dev/urandom`). Specialized shaders (Vulkan/WebGPU) are typically used in state-of-the-art terminals (Alacritty, WezTerm) to prevent UI locking under duress.

### Protocol Support & Interactivity (`Dotty.Terminal`)
*   **Missing Features:**
    *   **Mouse Protocol:** Lacking SGR 1006 / X10 translation, preventing robust cursor interaction in CLI apps like `htop`, `tmux`, or `vim`.
    *   **Advanced VT Extensions:** OSC 8 (Hyperlinks), Sixel (inline terminal images), and synchronized updates likely missing.
    *   **System Integration:** OSC 52 host clipboard synchronization.

---

## 3. Visual Rendering & Typography (Test Harness Results)

A suite of automated graphical tests was injected via `tmux` and `vttest` and captured into PNGs (`dotty_typography_tmux.png`, `dotty_vttest_cursor.png`). Review the screenshots against the following rendering thresholds typically problematic in Skia-backed terminal grids:

### A. Emojis and ZWJ (Zero-Width Joiners)
*   **Evaluation:** Check `dotty_typography_tmux.png`. Standard emojis should pad explicitly to take up exactly `2` standard character cells.
*   **Failure Modes:** Emojis clipped dynamically in half, or multi-rune ZWJ characters (like `👨‍💻` Man Technologist) rendering as separate distinct glyphs instead of combining correctly.

### B. CJK (Double-Width) Alignment
*   **Evaluation:** Chinese/Japanese text (`こんにちは世界`) requires true double-width allocation.
*   **Failure Modes:** If the Skia text-measuring tool overrides the backend VT state cell-width, characters will overlap their neighboring English/ASCII counterparts. 

### C. Box Drawing Coordinates
*   **Evaluation:** Check the box layout: `┌─┬─┐`.
*   **Failure Modes:** Fractional gaps appearing between structural lines. This proves the Avalonia layout system's line-height/padding rules are not stretching font glyphs fully to their absolute cell boundary edge. A custom manual drawing pipeline for specific box-characters is usually required to fix this.

### D. ANSI Color Contrast
*   **Evaluation:** Ensure the output of 16-color sequences maps correctly inside the tmux pane payload.
*   **Failure Modes:** Color bleeding or loss of 256-color multiplexing due to strict Xterm profile mismatches in `pty-helper`.

### E. VT100 Cursor Adherence (`vttest`)
*   **Evaluation:** Review `dotty_vttest_cursor.png` (Menu Option 1 executed).
*   **Failure Modes:** Misplaced cursors outside of bounded UI areas or lines improperly wrapping rather than discarding off-screen logic when Auto-Wrap Mode is toggled off.

---
**Summary:** Dotty provides a highly capable architectural foundation, but necessitates deeper integration of Unicode-wide glyph bounding strategies, a ConPTY Windows fallback, and memory virtualization for inactive background tabs.