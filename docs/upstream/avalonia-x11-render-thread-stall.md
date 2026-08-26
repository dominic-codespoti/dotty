# [X11] Custom draw operations stop being invoked by the render thread after ~8 frames under sparse updates

## Environment
- Avalonia 12.1.0, X11 backend (XWayland on Hyprland, radeonsi/mesa)
- ICustomDrawOperation registered via `context.Custom(op)` from Control.Render

## Reproduction
1. Control.Render creates a new ICustomDrawOperation instance per frame
   (ReferenceEquals-based Equals → always "changed") and returns.
2. Drive frames at ~9/s (e.g., terminal echo updates).
3. The render thread executes op.Render for the first ~8 frames, then never
   again. The UI thread keeps running render passes (Render() called, op
   created), but the compositor never invokes op.Render. Screen freezes at
   the last drawn frame.

## Notes
- Software rendering (SoftwareRenderer) renders every frame correctly.
- llvmpipe GL renders continuously; radeonsi stalls — but the stall is in
  op.Render invocation (never called), not inside the draw.
- Render thread is alive (14% CPU, state R/S mix) but not executing ops.
- Ruled out: op exceptions (none), Equals/Bounds dirty-tracking (forcing
  Equals=false + full-damage Bounds doesn't help), GPU spin.

## Impact
Custom-draw-op-based rendering freezes under sparse update rates while
working under sustained output — terminal emulators and similar
canvas-style renderers are affected on hardware GL.

Workaround: render on the UI thread (bitmap + DrawImage) or use
OpenGlControlBase (render-loop-driven, unaffected).
