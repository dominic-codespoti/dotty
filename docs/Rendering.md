# Rendering Loop & Avalonia Integration

Located in `src/Dotty.App/Controls/Canvas/TerminalCanvas.cs`:

* **`TerminalCanvas` & `TerminalVisualHandler`**: These connect logical array coordinates from the headless buffer directly into physical GPU vectors for rendering.
* **Strict Performance Constraints**: The main render loop heavily restricts LINQ overhead, allocations, and boxed types. `ref struct` and `Span<T>` are mandated to maintain performance.
* **Glyph Caching**: Text glyphs are pre-baked onto an atlas texture, drastically reducing typography and rasterization layout times during rapid 60+ FPS updates.
* **`BackgroundSynth`**: Non-text visual features (highlights, terminal bells, inverted colors) are delegated to this component for faster direct GPU composition. Double buffering logic cleanly splits GUI parsing from background updates.
