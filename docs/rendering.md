# Rendering Development Guide

Welcome to the rendering layer documentation! This deals strictly with updating what the user sees in the Avalonia `Dotty.App`.

## Architecture & Components
- **`TerminalCanvas` / `TerminalVisualHandler`**: Core rendering engines that interface with the `TerminalBuffer`.
- **`GlyphAtlas`**: Pre-bakes text glyphs onto an atlas texture. When rendering the screen, we only incur the cost of fetching from the atlas, saving CPU cycles on typography/rasterization layout overhead per frame.
- **`BackgroundSynth`**: Renders non-text visual features like selection ranges, backgrounds, inverted colors, and cursor blocks effectively on the GPU/Canvas.

## Core Rules 🚫

1. **NO LINQ in Render Loop:** Never use `IEnumerable`, `.Select()`, or `.ToList()` in `RequestFrame` or inside the `TerminalVisualHandler.RenderTo` methods.
2. **NO Boxed Types:** Avoid allocations on the heap that will trigger Garbage Collection spikes during rapid scrolling. Use `ref struct`, `Memory<T>`, `Span<T>` over standard classes or arrays where feasible.
3. **Scroll Synchronization:** `ScrollInvalidated` events are crucial. Always ensure that the view matrix respects the current PTY output buffer's offset when scrolling back.
4. **Invalidation:** Do not redraw components that haven't changed. Rely on dirty tracking where possible to only redraw affected screen rectangles.
5. **Double Buffering:** Keep the backend parser and the UI renderer loosely coupled to prevent deadlocks and tearing. The UI should snapshot the backend's state for the current frame.