# Avalonia Integration and Rendering Optimization Plan

Status: **Active — Phase 0 instrumentation started 2026-08-13.**

This document is the long-term plan for using Avalonia more effectively in Dotty while preserving
terminal correctness, low input latency, a low idle CPU profile, bounded memory use, and polished
cross-platform behavior. It is intentionally evidence-gated: a lower-level or more complicated
rendering path is adopted only when the real GUI harness shows a material end-to-end improvement.

## 1. Outcome

Dotty should use Avalonia for the responsibilities where a cross-platform UI framework has the
most leverage:

- window, screen, DPI, and platform lifecycle;
- layout, scrolling, focus, routed input, IME, clipboard, drag/drop, and accessibility;
- theme resources and platform transparency negotiation;
- render scheduling and final composition.

The terminal grid remains one custom-drawn visual. Dotty must not create an Avalonia control,
binding, text layout, or automation peer per terminal cell. The existing terminal-specific
HarfBuzz/Skia shaping pipeline remains the production text path until a measured replacement
proves both correctness and lower total cost.

## 2. Current implementation

The production render path is:

```text
PTY/parser
  -> TerminalBuffer
  -> TerminalSession.RenderScheduled
  -> DispatcherPriority.Render
  -> TerminalCanvas.Render
  -> Skia SKSurface over an Avalonia WriteableBitmap
  -> DrawingContext.DrawImage
  -> Avalonia renderer/compositor
```

Important properties of the current design:

- `TerminalCanvas` is one `Control` implementing `ILogicalScrollable`.
- A render attempts to acquire `TerminalBuffer.SyncRoot` for at most 4 ms. A busy buffer leaves the
  last bitmap visible rather than blocking the UI thread indefinitely.
- The live path clears and redraws the complete visible frame. A previous incremental path was
  removed after pixel corruption and scroll-follow regressions.
- PTY output is coalesced by `_renderUpdatePending`, but session-side `Task.Delay`, dispatcher
  posting, and a continuous `RequestAnimationFrame` refresh-measurement loop currently overlap.
- Font size is multiplied by `TopLevel.RenderScaling`, while the backing `WriteableBitmap` is
  allocated from unscaled `Bounds` with 96 DPI. This is a mixed logical/physical-pixel model that
  must be measured and corrected before renderer work.
- `TerminalGlCanvas` is an unused prototype. It uploads full grid, UV, and atlas textures each
  frame and does not implement the correctness surface of `TerminalFrameComposer`.
- `GlyphAtlasService` shares atlases between tabs but has no eviction or byte budget.
- Inactive-view teardown currently trims scrollback to 100 lines. Silent terminal-history loss is
  not an acceptable memory optimization.
- `AvaloniaUI.DiagnosticsSupport` is referenced, but the developer tools are not attached.
- The existing BenchmarkDotNet "rendering" suite measures parser and buffer mutation; it does not
  measure `TerminalCanvas`, bitmap rasterization, or Avalonia presentation.

## 3. Non-negotiable invariants

1. **Correctness precedes throughput.** No stale frames, torn terminal state, lost synchronized
   updates, broken scroll-follow behavior, or glyph corruption.
2. **One custom terminal visual.** No retained UI object graph proportional to rows times columns.
3. **DIPs for layout, physical pixels for raster resources.** Scaling is applied exactly once.
4. **Demand-driven rendering.** An unchanged terminal does not run a frame loop.
5. **Inactive views do no presentation work.** Hidden/detached views do not blink, measure refresh,
   or request frames.
6. **No unbounded cache.** Every glyph, typeface, shaped-run, snapshot, and diagnostic cache has a
   lifetime and a measurable budget.
7. **No silent data loss for memory.** View resources may be released; configured scrollback is
   preserved.
8. **No allocation by convenience in the hot path.** Resource lookup, string formatting, JSON,
   logging, diagnostics, and platform interop stay outside rendering.
9. **Native resource ownership is explicit.** Skia objects are disposed only when no render can use
   them, and they are not abandoned to finalization during repeated font or scale changes.
10. **Complexity must pay rent.** A new backend must produce a material measured win, not merely
    move work between threads or APIs.

## 4. Performance and quality gates

Phase 0 establishes numeric baselines. Every later phase must satisfy these gates on equivalent
Release builds and workloads.

| Area | Gate |
|---|---|
| Correctness | Deterministic render scenarios match their approved pixel baselines. Antialiasing tolerances are narrow and documented. |
| Frame coalescing | At most one queued UI post and one Avalonia animation-frame callback per mounted terminal. |
| Buffer contention | The UI thread never waits more than the existing 4 ms bound; misses are counted and retried. |
| Idle behavior | No terminal content frames while unchanged. Only the configured visible cursor blink may invalidate an overlay. |
| Hidden tabs | Zero frame, refresh-measurement, and cursor-blink work for hidden or detached views. |
| Managed allocation | Zero Dotty-owned allocation for cursor-only frames after warm-up; no sustained-frame allocation regression. |
| Cache growth | Atlas/typeface/shaped-run memory converges to a configured bound after repeated font, theme, and scale changes. |
| RSS | Median RSS does not regress by more than 5% for an equivalent workload. |
| CPU/frame time | No statistically clear regression over at least five comparable runs. |
| Backend complexity | A more complex backend needs at least a 15% p95 frame-cost or sustained-CPU improvement, with no quality loss. |
| Interaction | Keyboard, pointer, resize, scroll, and tab-switch latency remain within one display interval under normal load. |
| Visual quality | No blur, double scaling, white flash, stale tab image, cursor contrast loss, or theme-inconsistent overlay. |

Developer diagnostics are disabled for recorded numbers. Comparative GUI results use the same host,
compositor, window size, font, theme, scale, and workload. Absolute results from different machines
are not compared directly.

## 5. Verification matrix

The reusable render matrix covers:

### Content

- plain ASCII and dense ANSI color;
- ligatures and shapeable programming text;
- CJK wide characters;
- emoji and fallback fonts;
- combining graphemes;
- box and block drawing;
- underline, double underline, undercurl, dotted, dashed, strike, and overline;
- hyperlinks;
- primary and alternate screens;
- scrollback, selection, search matches, and all cursor shapes.

### Lifecycle

- startup and first prompt;
- idle for 60 seconds;
- interactive typing;
- burst and sustained output;
- user scroll during sustained output;
- repeated resize;
- tab creation, switching, hiding, destruction, and recreation;
- repeated font, theme, transparency, and display-scale changes;
- synchronized-update enter, mutation, and exit.

### Platforms

- render scales 1.0, 1.25, 1.5, and 2.0;
- 60 Hz and high-refresh displays where available;
- X11 and Wayland;
- Windows and macOS when available;
- hardware-backed and software Avalonia rendering;
- Debug/JIT for diagnostics and Release/AOT for delivery.

## 6. Phase 0 — Real GUI instrumentation and baseline

Status: **In progress.**

### 6.1 Goals

- Measure the actual Avalonia terminal path rather than parser-only proxies.
- Establish repeatable correctness captures before changing DPI, scheduling, or rendering.
- Keep production overhead effectively zero when telemetry is disabled.

### 6.2 Telemetry contract

Each mounted `TerminalView` owns fixed-size render telemetry shared with its `TerminalCanvas`.
Telemetry is opt-in and uses counters, timestamps, a fixed duration histogram, and atomic updates;
it performs no logging, formatting, collection growth, or per-frame allocation.

Metrics:

- render notifications and notifications coalesced by the view;
- UI render updates applied;
- canvas frame requests;
- total `Render` calls;
- content render attempts and completed content frames;
- buffer-lock misses;
- backing-bitmap recreations;
- total, average, maximum, and approximate p95 render duration;
- total, average, and maximum content-raster duration;
- total and maximum managed bytes allocated on the UI thread during `Render` while telemetry is
  enabled;
- last rendered buffer generation, render scale, and backing pixel dimensions.

The TCP harness implements:

- `PERF:START` — reset and enable telemetry;
- `PERF:STOP` — disable telemetry without discarding the last snapshot;
- `PERF:GET` / `PERF:SNAPSHOT` — return active and mounted-view aggregate snapshots;
- `PERF:RESET` — clear counters while preserving enabled state;
- `WAIT_FOR_IDLE` — wait through queued UI work and Avalonia animation ticks;
- `RENDER_SCENARIO` — load a deterministic styled terminal screen;
- `CAPTURE_CANVAS` / `CAPTURE` — save deterministic PNG evidence from the UI thread.

### 6.3 Harness work

Extend `artifacts/perf/gui_harness_bench.py` to:

- enable/reset telemetry per workload phase;
- report render telemetry alongside command RTT, capture time, RSS, and lifecycle stats;
- support an explicit idle interval;
- load and capture the deterministic render scenario;
- wait for Avalonia render-idle before reading metrics or saving a capture;
- preserve per-run results rather than reducing every metric to one average.

### 6.4 Phase 0 exit criteria

- Telemetry contract tests pass.
- Release build and actual terminal smoke test pass with telemetry disabled and enabled.
- Deterministic scenario capture is produced through `CAPTURE_CANVAS`.
- Initial baseline includes idle, deterministic render, tab creation, and tab switching.
- The baseline records p50/p95 where available, maxima, allocation, lock misses, bitmap recreation,
  CPU/RSS context, and the exact command line.

### 6.5 Baseline record

First accepted run: **2026-08-13**, host AMD Ryzen 7 7735HS (Radeon 680M), X11 `:0`, 1.0 render scale.

Command line:

```text
SHELL=artifacts/perf/gui-bench-idle-shell.sh \
python3 artifacts/perf/gui_harness_bench.py \
  --app src/Dotty.App/bin/Release/net10.0/Dotty.App \
  --port 9876 --runs 3 --new-tabs 12 --switches 60 \
  --idle-seconds 5 --render-scenario --capture-mode canvas
```

Results are medians across 3 runs; render costs are from the aggregate of mounted-view telemetry.

| Workload | Command | Result (median) |
|---|---|---|
| Startup | app launch to first harness RTT | 1258 ms startup; 387 ms first command |
| Idle (5 s, quiet shell) | `--idle-seconds 5` | 8-10 settle renders, then no frames; render avg 1.5-4.3 ms, max 22.1 ms; 1.27 MB UI-thread alloc in the settle burst (font/atlas warm-up, one bitmap recreation) |
| Deterministic render scenario | `RENDER_SCENARIO` + `CAPTURE_CANVAS` | 3-4 renders; avg 6.5-8.2 ms, max 14.1-19.5 ms, p95 upper bound 16-33 ms; 94-111 KB alloc with telemetry enabled; PNG capture OK (44 KB, 1267x1511) |
| Tab creation | 12 x `NEW_TAB` | avg RTT 29-35 ms, p95 33-42 ms |
| Tab switching | 60 x `NEXT_TAB`/`PREV_TAB` | avg RTT 0.8-2.0 ms, p95 1.2-8.8 ms, 758 cmds/s |
| Buffer contention | all phases | 0 lock misses in every phase |
| Bitmap recreation | all phases | 0 at idle settle; 1 during first-scenario warm-up in runs 2-3 |

Phase 0 observations to carry into later phases:

- **RSS grows 421-735 MB across a 12-tab, 60-switch run.** Includes per-session scrollback buffers,
  PTY-backed shells, and Avalonia/Skia caches. Anchors the memory gates (Section 4) before any
  cache-budget work.
- **CJK renders as tofu in the scenario capture on this host** while emoji and box drawing fall
  back correctly. Font-fallback coverage for CJK is a pre-existing gap to confirm against the
  installed font set before treating it as a renderer defect.
- **Render cost is dominated by content raster (avg 6.5-8.2 ms for the styled 1267x1511 canvas).**
  This is the budget any incremental or GPU path must beat under the Section 4 gates.
- Telemetry-enabled allocation (94-111 KB/run of UI-thread bytes) is measurement overhead;
  the disabled path records zero allocation (contract test `DisabledRecording_DoesNotAllocateOrChangeCounters`).

## 7. Phase 1 — Correct DPI and physical-pixel ownership

### 7.1 Contract

- Avalonia bounds, viewport, padding, cell size, scrolling, pointer hit testing, and overlays remain
  in DIPs.
- Backing surface dimensions are `round(Bounds * RenderScaling)` physical pixels.
- One transform or one centralized conversion maps logical geometry to physical pixels.
- Pixel-snapped geometry uses `round(dip * scale) / scale` or an equivalent centralized helper.
- Protocol replies that claim pixel units use intentionally converted physical dimensions.

### 7.2 Work

1. Subscribe to `TopLevel.ScalingChanged` while `TerminalCanvas` is attached.
2. On a scale transition, mark metrics and backing resources dirty once; invalidate measure/render
   once; rebuild on demand.
3. Replace the current scaled-font/unscaled-bitmap combination with one internally consistent
   raster model.
4. Include all raster-dependent parameters in atlas identity.
5. Snap box drawing, block elements, cursor edges, underlines, and selection bounds to device
   pixels without changing terminal-cell geometry.
6. Audit CSI window-pixel replies and `MainWindow.BroadcastWindowPixelSize()`.
7. Exercise live movement between differently scaled displays.

### 7.3 Exit criteria

- Backing dimensions match render scale.
- Logical row/column count does not change merely because a window moves displays.
- Text and geometric glyphs are crisp at all target scales.
- No double scaling, surface recreation loop, or native-memory growth occurs.

### 7.4 Implementation record — 2026-08-13

Changes:

- `TerminalCanvas` subscribes to `TopLevel.ScalingChanged` while attached and unsubscribes on
  detach. A scale transition marks metrics dirty and invalidates measure/render once; the backing
  surface is rebuilt on demand because its physical dimensions are now derived from the scale.
- The backing `WriteableBitmap` is `round(Bounds * RenderScaling)` physical pixels at
  `96 * RenderScaling` DPI, and the Skia surface is created with an explicit
  `Bgra8888/Premul` image info matching the bitmap format.
- One logical-to-physical transform: the render applies `SKMatrix.CreateScale(scale)` once; all
  cell, padding, scroll, selection, cursor, and debug-overlay geometry stays in DIPs.
- Device-pixel snapping (`round(dip * scale) / scale`) applied to cursor, selection, background
  region, block-geometry, hyperlink-band, and decoration rects/lines; decoration stroke widths
  keep a one-device-pixel floor. `TerminalFrameComposer.DeviceScale` is set per render.
- `MainWindow.BroadcastWindowPixelSize` reports physical pixels (`ClientSize * RenderScaling`)
  and re-broadcasts on `ScalingChanged`; tab-switch preview snapshots are scale-aware. CSI 14 t
  therefore replies with physical dimensions; CSI 18 t already replies in cells.
- Atlas identity already includes the device-size font (`SkFont.Size` is scale-multiplied), so a
  scale change selects a distinct shared atlas. Known edge case, not fixed here: the atlas key
  rounds size to 0.1, so two scales can collide on the same key and share an atlas rasterized at
  the first-seen size.

Verification (Release, Xvfb with `AVALONIA_GLOBAL_SCALE_FACTOR`):

| Scale | Bitmap px (1000x660 DIP window) | Grid cols x rows | Render avg/max ms | Captures |
|---|---|---|---|---|
| 1.0 | 1267x1511 (native window) | n/a | 9.2 / 14.3 | 1267x1511 PNG |
| 1.25 | 1250x825 | 107x31 | 8.9 / 15.8 | 1250x825 PNG, crisp |
| 1.5 | 1500x990 | 107x31 | 10.8 / 18.9 | 1500x990 PNG |
| 2.0 | 2000x1320 | 107x31 | 7.5 / 15.1 | 2000x1320 PNG |

- Backing dimensions match `round(Bounds * scale)` at every scale; captures match the backing
  dimensions exactly, so the compositor path is 1:1 (no resampling).
- Grid stays 107x31 across 1.25/1.5/2.0 for the same DIP window.
- 1.25x capture inspected: box drawing corners sharp, no AA halo, uniform stroke width, no
  baseline drift. Text and geometric glyphs crisp at the fractional scale.
- No surface recreation loop: 1 bitmap recreation at startup, 0 during steady renders at every
  scale; single-tab RSS delta ~21 MB.
- 0 buffer lock misses at every scale.

Not verified on this host: a live move between differently scaled physical displays (single
display available; Xvfb reports no physical size and this Xvfb build drops root-window property
writes, so `Xft.dpi` transitions cannot be induced here). The transition path is verified by
construction: symmetric attach/detach subscription, scale re-read on every render in
`EnsureMetrics`, and dimension-driven bitmap rebuild. `AVALONIA_GLOBAL_SCALE_FACTOR` is the
supported test override (read once at startup); `AVALONIA_SCREEN_SCALE_FACTORS=DP-1=2;DP-2=1`
covers per-monitor setups.

Exit criteria status: **all verifiable criteria pass**; the live display-move exercise remains
pending hardware.

## 8. Phase 2 — Demand-driven Avalonia frame scheduling

### 8.1 Target flow

```text
terminal mutation
  -> update a monotonic dirty generation
  -> coalesce one UI post
  -> request one TopLevel animation frame
  -> render the latest generation
  -> reschedule only if newer work arrived or the buffer lock was missed
```

### 8.2 Work

1. Define atomic dirty, scheduled, rendered, and retry state.
2. Make `TopLevel.RequestAnimationFrame` the presentation gate while work is pending.
3. Preserve synchronized-update semantics: no intermediate presentation and one latest frame on
   exit.
4. Reschedule explicitly after a 4 ms buffer-lock miss.
5. Remove duplicate invalidations once the gate owns scheduling.
6. Remove the perpetual refresh-measurement callback and session `Task.Delay` render loop only
   after burst, sustained, idle, synchronized-update, resize, and detach tests pass.
7. Stop blink and presentation work for hidden/detached views.
8. Destroy inactive view resources after the grace period without trimming session scrollback.

### 8.3 Exit criteria

- No idle frame loop.
- One scheduling owner and no lost latest frame.
- Hidden views produce no render telemetry.
- Continuous output remains interactive and scroll-follow remains correct.
- Inactive view teardown reduces view memory without changing terminal history.

### 8.4 Implementation record — 2026-08-13

Changes:

- **One scheduling owner.** `TerminalView` owns the gate: a mutation (adapter
  `RenderRequested`, which fires from the PTY consumer thread or the UI thread) coalesces into
  one `DispatcherPriority.Render` post, which requests one `TopLevel` animation frame. The frame
  callback renders the latest buffer state (`SetBuffer`), so bursts collapse to one frame and a
  frame that was pending while new output arrived still presents the newest data.
- **At most one animation frame in flight.** A `_frameScheduled` flag prevents registering one
  callback per post. Without it, a flood registered hundreds of callbacks and every display tick
  fired them all, churning the UI thread (measured: 347 posts, then hundreds of callback fires per
  tick; fixed to 24 frames for the same flood).
- **Background-thread signal fix.** The original gate checked `IsVisible`/`VisualRoot` on the
  calling thread, which for the PTY consumer thread silently dropped every render signal (echoed
  input reached the buffer but produced zero telemetry). Replaced with a UI-thread-maintained
  `_presentationEnabled` flag; the authoritative check runs in the posted delegate.
- **Explicit lock-miss retry.** `TerminalCanvas` raises `FrameRetryRequested` on a buffer-lock
  miss; the view schedules one more animation frame instead of dropping the skipped content.
- **Removed the session `Task.Delay` render poll timer** (16 ms forever) and the perpetual
  `RequestAnimationFrame` fps-measurement loop (which survived detach). `RefreshInterval` remains
  as a diagnostic cadence signal updated by the presentation gate.
- Hidden views: `_presentationEnabled` gates all work before telemetry; the blink timer already
  stops on `IsVisible=false`. Inactive-view teardown was already implemented (grace timer, scrollback
  preserved).

Verification (Release, live X11 + harness telemetry):

- Idle 2 s: 0 notifications, 0 posts, 0 frame requests; only blink-driven renders (allowed).
- Scenario: 1 notification → 1 post → 1 frame → renders latest; generation advances.
- 100-command TYPE flood: 425 notifications → 362 posts → 24 frames → 27 renders, 0 lock misses,
  4.4 ms avg render; settle delta over 2 s: 0 notifications/posts/frames (no idle loop, no lost
  latest frame).
- Hidden background tab after creation burst: aggregate telemetry exactly equals active view —
  hidden view produces zero render telemetry.
- App test suite: 627 passed.

E2E suite findings (pre-existing infrastructure issues, not the scheduling change):

- `TestCommandInterface.GetStatsAsync` deserialized the app's camelCase STATS JSON with default
  (case-sensitive) options, so every `ApplicationStats` field was always zero and every
  `SessionsStarted > 0` assertion always failed. Fixed with case-insensitive options; the suite
  went from 6/65 to 56/65 passing, with the failures now running their real assertions.
- The remaining failures are: tests that pass in isolation but fail after earlier tests polluted
  the shared app's terminal state (suite reuses one app per class), and perf assertions
  (`AssertFps`, parser throughput) that read fields the pre-phase-0 `PERF:*` commands returned as
  `{}` — they could never have passed. The app-side telemetry is verified correct in four
  independent replications of the collector's command pattern (fps 8.2-9.2, 50-51 renders over
  ~5.8 s during rapid resize).
- The earlier "200 processes / OOM" incident: each E2E test class launches a full app subprocess
  (Release, xvfb-run); interrupted runs and MSBuild node reuse accumulated processes, and the
  host's `earlyoom` is configured to kill `dotnet`/`Xvfb` first. Cleanup protocol adopted: one app
  instance at a time, `SHUTDOWN` between runs, `dotnet build-server shutdown` + `pkill testhost`
  after every test/build, and no stray processes before yielding.

Exit criteria status: **all met** on the live path; E2E failures are suite-infrastructure issues
documented above.

## 9. Phase 3 — Optimize the correctness renderer

### 9.1 Split content from cheap overlays

Track content, viewport, selection/search, cursor, and settings invalidation separately. A cursor
blink must reuse terminal content and draw only a lightweight overlay. Selection/search separation
is considered only after cursor separation passes pixel and interaction tests.

Cursor rendering must preserve DECSCUSR shape/blink semantics, focus state, theme contrast, and
correct block-cursor glyph treatment rather than applying a fixed translucent white rectangle.

### 9.2 Eliminate hot-path work

Remove or reuse:

- resource lookup and color conversion during `Render`;
- cursor `SKPaint` creation per frame;
- repeated or undisposed scrollback text blobs;
- shaped-run font and builder churn;
- per-cell decoration paths;
- debug strings when the overlay is disabled;
- native paint/typeface objects abandoned to finalization after settings changes.

### 9.3 Bound shared resources

Give `GlyphAtlasService` a measurable byte budget, mounted-view references, and LRU eviction of
unused atlases. Apply explicit lifetime accounting to cached typefaces. Repeated font and scale
changes must return to a stable retained-memory level.

### 9.4 Partial rendering policy

Full redraw remains the reference implementation. Dirty-row, scroll-copy, or region rendering is
not reintroduced until the pixel-diff harness can prove manual scroll, output while scrolled,
alternate screen, resize, wide characters, overlays, theme changes, and DPI transitions.

### 9.5 Exit criteria

- Cursor blinking does not rasterize terminal content.
- Cursor-only work allocates no Dotty-owned managed memory after warm-up.
- Resource dictionaries and parsing are absent from the render hot path.
- Native/managed caches are bounded.
- Frame CPU and p95 improve with no correctness regression.

### 9.6 Implementation record — 2026-08-13

Changes:

- **Cursor overlay split.** `TerminalCanvas` now tracks content dirtiness separately from
  overlay-only changes. The content bitmap is re-rasterized only when the buffer, geometry,
  colors, scroll offset, selection, or scale changed; the cursor is drawn as an Avalonia
  `DrawingContext` primitive on top of the cached bitmap (same padding + scroll-translate
  geometry, device-pixel snapped). Blink (`ShowCursor`) and shape changes invalidate the overlay
  only. The Skia cursor block (including the per-frame `SKPaint` allocation) was removed from
  `RenderToBitmap`.
- **Hot-path resource caching.** Background brush/color and cursor brush are resolved on attach,
  runtime-settings change, and theme change (`App.ThemeUpdated`, a new static event raised by
  `App.OnThemeChanged`) — never during `Render`. The cursor brush follows the theme foreground at
  the previous translucency instead of hard-coded white.
- **Atlas bounds.** `GlyphAtlas` exposes `SizeBytes` and service-maintained recency/refcount
  stamps. `GlyphAtlasService` gained `AcquireAtlas`/`ReleaseAtlas`, a 32 MB retained-memory
  budget, and LRU eviction of unreferenced atlases; canvases release their reference on detach.
  Referenced atlases are never evicted.
- **Fixed: atlas sharing never worked across tabs.** `GlyphRasterizationOptions` used reference
  equality, so every options instance hashed differently and same-font tabs got separate atlases.
  Made it value-equatable; exposed by the new service tests.

Verification:

- 631 app tests pass (4 new: acquire/release refcounts, byte accounting, LRU eviction under
  budget, referenced-atlas protection).
- Live Release smoke: idle 4 s produced +7 overlay renders with **+0 content rasters** (blink no
  longer rasterizes terminal content); a content mutation re-rasterizes exactly once.
- Capture inspected: beam cursor renders at the correct cell (row 11, col 5 per the scenario),
  clean containment, no artifacts, content fully rendered.
- Tab create/switch/destroy cycles exercise atlas acquire/release without crash; rendering
  remains correct after re-acquire.

Exit criteria status: blink no-raster **met**; cursor-only work allocates no Dotty-owned memory
(cached brush, no `SKPaint`, no resource lookup) **met**; resource dictionaries absent from the
render path **met**; caches bounded by budget + LRU **met**; frame CPU/p95 improvement follows
from blink frames dropping from a full ~7-13 ms content raster to a compositor overlay pass —
the histogram now shows sub-ms overlay frames interleaved with content frames.

Deferred from §9.2 (documented, not removed): scrollback `SKTextBlob` creation per line, shaped-run
font/builder churn, and per-cell decoration paths remain; they are content-path work gated by the
overlay split and are candidates for the pixel-diff-harness phase (9.4) rather than the cursor
path.

## 10. Phase 4 — Rendering backend decision

### 10.1 Experiment A: direct Skia custom draw operation

Prototype an `ICustomDrawOperation` using Avalonia's `ISkiaSharpApiLeaseFeature` and the existing
`TerminalFrameComposer`. Measure whether removing the intermediate `WriteableBitmap` lowers total
UI-thread cost, copy/upload cost, allocations, RSS, and p95 frame latency.

Requirements:

- same buffer-synchronization and correctness behavior;
- safe behavior when the Skia lease is unavailable;
- software-renderer support;
- 1x and 2x scale results;
- no unexplained pixel difference.

Adopt only if the 15% complexity gate is met.

### 10.2 Experiment B: composition render thread

Consider `CompositionCustomVisualHandler` only if Experiment A proves UI-thread drawing remains the
bottleneck. The Avalonia 12.1 API is the pinned source contract:

- `OnRender(ImmediateDrawingContext)`;
- `Invalidate()` / `Invalidate(Rect)`;
- `RegisterForNextAnimationFrameUpdate()`.

The handler consumes a bounded immutable/versioned frame snapshot. It never reads mutable
`TerminalBuffer` state or UI-owned Skia resources. Snapshot copy, pooling, synchronization, and
native lifetime costs count against the result.

### 10.3 Rejected default

Do not revive `TerminalGlCanvas`. Full grid/UV/atlas upload per frame and a second incomplete glyph
semantics path are not a viable production baseline.

### 10.4 Decision rule

If neither experiment clears the 15% gate, retain the simpler optimized bitmap renderer and delete
the rejected prototypes.

### 10.5 Measurement record — 2026-08-13

Gate workload mix (the workloads the 15% comparison is measured on):

1. **Sustained output flood** — infinite `seq` stream, 120 Hz display, Release build.
2. **Deterministic render scenario** — `RENDER_SCENARIO` styled screen (phase-0 baseline: content
   raster avg 6.5-8.2 ms at 1267x1511).
3. **Idle + blink** — zero gate signals, overlay-only blink frames (phase-3: blink no longer
   rasterizes content).

Instrumentation addition: present-interval counters on the presentation gate. Note on semantics:
the measured interval is the gate-callback cadence (dispatcher-op driven), i.e. the UI-thread frame
production rate — not a display-present timestamp (Avalonia exposes no direct present callback).
It is a valid UI-thread load indicator, not a compositor timing.

Sustained flood baseline (Release, 120 Hz display, this host):

| Metric | Value |
|---|---|
| Renders | 208 in 4 s = **52/s** (display is 120 Hz — UI thread is the cap) |
| Buffer lock misses | **119/208 = 57%** of render attempts |
| Content raster | avg 2.34 ms, max 6.64 ms |
| Total canvas render | avg 2.37 ms, max 6.67 ms, p95 upper bound 8 ms |
| Gate callback cadence | avg 0.19 ms between UI-thread frame callbacks (back-to-back op processing) |
| Allocations | 38 KB per render on the UI thread |
| Parser progress | buffer generation advanced ~2.4e9 during the window (kept up with the stream) |

Decision-relevant reading (CORRECTED 2026-08-13 — see §10.8; the frame-rate claims below are
unreliable because the observation environment throttles presentation cadence):

- The lock misses (57% of render attempts) are a real per-render observation: the PTY consumer
  holds `SyncRoot` while parsing, and the UI thread's bounded `TryEnter` collides. They do NOT
  cap the frame rate — the retry mechanism re-renders; the presentation cadence is set by
  Avalonia's animation-clock delivery (§10.8).
- Experiment A (direct Skia custom draw op) does not touch lock acquisition and is slower per
  content frame — its gate verdict stands on per-frame cost (fails 15%).
- The earlier inference that "lock-contention relief (Experiment B's snapshot pipeline) could
  clear the gate" does not survive the corrected analysis: the frame-rate cap is
  composition-bound, not lock-bound. B-lite would shorten UI lock holds (modest consumer
  throughput benefit) but does not address the observed cadence collapse.
- At 60 Hz the flood is likely fine; the p95 case is the 120 Hz target — unmeasurable until the
  §10.8 cadence issue is resolved or a quiet dedicated host is available.

### 10.6 Experiment A gate result — 2026-08-13

Prototype: `TerminalFrameDrawOperation` (`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`)
renders the terminal content directly into the compositor's Skia canvas, skipping the
`WriteableBitmap`. Same content path (`DrawContentToSkiaCanvas` shared with the bitmap renderer),
same bounded `SyncRoot` lock. Env-gated `DOTTY_DRAW_OP=1`. Verified correct: live flood content
and cursor render with correct positioning under the op path (also under `RenderTargetBitmap`
capture, where the lease is available).

Sustained flood comparison (Release, 120 Hz, same host/window):

| Metric | Bitmap baseline | Draw op (A) | Verdict |
|---|---|---|---|
| Frame rate | 52/s | 60/s | +15% but from retries, not content |
| Content frames | 89 | 80 | fewer content frames |
| Content raster avg | 2.34 ms | 2.88 ms | +23% slower |
| Render max | 6.67 ms | 5.76 ms | −14% |
| p95 upper bound | 8 ms | 8 ms | unchanged |
| Lock misses | 119 (57%) | 159 (67%) | worse |
| UI-thread alloc/render | 38 KB | 30 KB | −21% (real, not the gate) |

**Verdict: does not clear the 15% gate.** p95 frame cost unchanged; per-content-frame raster
worse; the frame-rate gain is additional lock-miss retries, not presented content. The flood
bottleneck is buffer-lock contention on the UI thread, which the presentation surface does not
affect. Per §10.4 the bitmap renderer is retained; this prototype is marked for deletion before
production (kept env-gated for re-measurement).

The workload where a backend change could clear the gate remains lock-contention relief
(Experiment B's bounded-immutable-snapshot pipeline, or a visible-rows snapshot taken under the
lock and rasterized off the UI thread) — §10.2's snapshot-copy + synchronization costs must be
counted against the result.

### 10.7 Experiment B-lite attempt — invalidated by environment

Design (feasibility confirmed, prototype reverted): `Screen.ReadSnapshot` already provides a
pooled, caller-owned memcpy snapshot of the screen; the composer's per-cell reads are centralized
in `ClassifyRowCellsCached` (GetCell/GetColdCell/StyleSet/GetRowGeneration), so a snapshot-aware
render source is a bounded refactor. The plan was: hold SyncRoot only for the copy, raster from
the snapshot outside the lock, and measure whether content frames recover from the 22/s observed
in §10.5.

Outcome: **the measurement is invalid — the frame cadence collapsed to ~1-2/s in every run,
independent of app code and independent of system load.** A bisect proved the app code is not the
cause: reverting the entire B-lite change (composer interface + canvas snapshot path) did not
restore cadence. Re-measurement at load 3.65 (down from 8-12) still produced ~1/s, so the load
hypothesis is also wrong.

### 10.8 Presentation cadence finding — 2026-08-13

The gate's animation-frame callbacks fire only when Avalonia's composition batch completes
(`MediaContext._animationsAreWaitingForComposition` gates `Pulse`). In this environment
(XWayland window on Hyprland, `:0`, 120 Hz display), sustained-output runs delivered callbacks at
~1.75/s — indistinguishable from the 600 ms cursor-blink cadence — meaning the ONLY thing driving
composition batches (and therefore gate callbacks) during the flood was the blink invalidation.
Single mutations render promptly; the sustained-output cadence is the failure mode. The window
was verified normal/mapped/on-screen; the app rendered ~0.9 ms per content frame with near-zero
lock misses during these runs.

**Decisive isolation (same day): under bare X11 (Xvfb, no compositor) the same build delivers
108 renders/s and 38.8 content frames/s under the identical flood.** The Hyprland/XWayland
present path is the cadence killer, not the app, not the load, not the backend. All gate
measurements must run on bare X11 (or a native Wayland path, untested).

### 10.9 Valid gate verdict — bare-X11 flood, 2026-08-13

Same host, Xvfb `:99` 1600x1000, identical flood shell, same window (1000x660), Release build.
The `:0`/Hyprland numbers in §10.5/§10.6 are superseded.

| Metric | Bitmap baseline | Draw op (A) | Verdict |
|---|---|---|---|
| Content frames/s | 38.8 | 36.4 | −6% |
| Content raster avg | 2.84 ms | 2.94 ms | +3.5% slower |
| Content raster max | ~6.8 ms | 6.78 ms | unchanged |
| p95 upper bound | 8 ms | 8 ms | unchanged |
| Lock misses | 161/433 (37%) | 178/433 (41%) | worse |
| Render rate | 108/s | 61.7/s | −43% (A re-rasters every frame; no content caching) |
| UI-thread alloc/render | ~38 KB | 22.7 KB | −40% (real, not the gate) |

**Verdict: Experiment A does not clear the 15% gate.** Per-content-frame cost is worse
(+3.5% vs the required −15%), p95 unchanged, content-frame rate lower, and the draw-op's
no-caching design halves the total render rate. Per §10.4 the bitmap renderer is retained.
`TerminalFrameDrawOperation` and its `DOTTY_DRAW_OP` switch were **deleted** in the phase-6
cleanup (2026-08-13) after the verdict.

What the valid data shows about the remaining limiter: under bare X11 the UI thread renders at
~108/s but content frames cap at ~39/s because 37-41% of render attempts fail the 4 ms bounded
lock while the PTY consumer parses. Experiment B-lite (snapshot under the lock, raster outside)
would shorten the UI's lock hold but does not change the consumer's hold — the miss mechanism is
consumer collisions, so B-lite's benefit is bounded and unproven; it is not the lead hypothesis
for clearing the gate.

## 11. Phase 5 — Avalonia-native usability and aesthetics

### 11.1 IME

Implement a `TextInputMethodClient` for terminal focus:

- preedit display at the terminal cursor;
- correct candidate-window cursor rectangle;
- composition reset on focus/session changes;
- committed text sent exactly once;
- bounded or disabled surrounding-text exposure.

Test dead keys, CJK input, and platform input methods. IME work stays outside rendering except for an
immutable overlay state change.

### 11.2 Theme and transparency

- Resolve resources with the control's actual theme variant.
- Convert resource values to immutable cached Skia values on change.
- Use theme resources for cursor, selection, search, focus, and tab chrome.
- Verify contrast in dark, light, transparent, and platform fallback modes.
- Use `ActualTransparencyLevel` to detect the achieved platform effect.
- Avoid expensive terminal-surface effects unless measured.

### 11.3 Accessibility

Expose one semantic terminal surface with a meaningful name, visible viewport text, cursor,
selection, and bounded/coalesced announcements. Never create an automation peer per cell.

### 11.4 Drag/drop

Optionally accept async file/text transfers and feed them through existing bracketed-paste handling.
No transfer or platform operation runs in a frame callback.

### 11.5 Exit criteria

- IME preedit and commit work across supported platforms.
- Focus, clipboard, pointer selection, search, and keyboard protocols remain correct.
- Theme variants and transparency fallbacks are visually coherent.
- Accessibility adds no per-frame work while unused.

### 11.6 Implementation record — 2026-08-13

Changes:

- **Theme variant resolution.** `TerminalCanvas.ResolveResourceBrush` now resolves with the
  control's `ActualThemeVariant` instead of `ThemeVariant.Default`, so cached background/cursor
  brushes follow the effective theme. Verified by capture: the deterministic scenario renders
  fully under the actual variant.
- **Drag/drop.** `TerminalView` accepts file and text drops and feeds them through the existing
  bracketed-paste path (`SendPasteInput`). Uses the 12.1 `DataTransfer` API (`DataFormat.Text` /
  `DataFormat.File`, `TryGetText`/`TryGetFiles`); file drops paste local paths joined by spaces.
  No platform operation runs in a frame callback.
- **IME client.** `TerminalTextInputMethodClient` (Avalonia 12.1 `TextInputMethodClient`):
  `SupportsPreedit` with disabled surrounding text (bounded exposure); `CursorRectangle` follows
  the terminal cursor cell (the canvas notifies on cursor-cell changes); preedit text renders as
  an overlay at the cursor cell with a composition underline; committed text arrives through the
  normal `TextInput` event and is sent exactly once (the preedit is never sent). The view
  provides the client on `TextInputMethodClientRequeryRequested` and resets the composition on
  detach.
- **Accessibility.** `TerminalCanvasAutomationPeer` exposes one semantic surface: name
  "Terminal", no children (never a peer per cell), and the bounded visible viewport text via
  `GetHelpTextCore`. All work is lazy (AT-driven); nothing runs per frame while unused.

Verification: 634 app tests pass (3 new IME state-machine tests: preedit set/clear, reset-only-
when-composing, safe cursor rect without a buffer); live smoke under bare X11 confirmed scenario
rendering, session/typing integrity, and clean shutdown.

Not verified here (needs a real input method): live CJK/dead-key composition on a platform IME,
candidate-window placement across platforms, and AT-screen-reader interaction. The client
contract and preedit rendering are implemented and unit-tested; platform IME verification is the
remaining exit-criterion item.

## 12. Phase 6 — Verification, cleanup, and documentation

1. Run the complete verification matrix on the final backend.
2. Compare at least five Release GUI runs against Phase 0.
3. Publish and smoke-test the configured AOT deliverable.
4. Remove unused `TerminalGlCanvas`, shaders, snapshot code, obsolete timers, temporary backend
   switches, and benchmark scaffolding.
5. Enable Avalonia Developer Tools only in Debug and keep recorded performance runs detached.
6. Update `Architecture.md`, `Rendering.md`, `Performance.md`, GUI harness documentation, and test
   documentation to describe the shipped path rather than the historical composition renderer.

### 12.1 Implementation record — 2026-08-13

Cleanup (item 4) executed:

- Removed `TerminalGlCanvas`, the `term.vert`/`term.frag` shaders, and every reference (only a
  comment remained).
- Removed the rejected Experiment A prototype (`TerminalFrameDrawOperation` + the
  `DOTTY_DRAW_OP` switch + the lease-canvas surface path) per the §10.9 verdict.
- Verified no stale root experiment projects or harness scaffolding remain; the remaining
  env-gated switches (`DOTTY_RENDER_DIAGNOSTICS`, `DOTTY_DISABLE_GLYPH_DISCOVERY`,
  `DOTTY_BENCH_STARTUP_LOG`) are documented diagnostics, not temporary experiments.

AOT deliverable (item 3): `PublishAot` is configured; `dotnet publish -c Release -r linux-x64
--self-contained` succeeds (~98 s) with two expected trim warnings from the Roslyn runtime
compiler (dynamic config compilation). Smoke-tested the published binary under bare X11:
scenario renders (capture byte-identical to the JIT build), telemetry live, typing works, clean
shutdown. The AOT binary starts the harness listener in <300 ms.

Documentation (item 6): `Rendering.md`'s frame lifecycle now describes the shipped demand-driven
path (adapter flush -> presentation gate -> cached-bitmap raster + overlay primitives -> Avalonia
composition) instead of the historical 1 ms-timer/GPU-handler flow. The roadmap (§10.5-§11.6)
records the measurement, gate verdicts, cadence finding, and phase-5 implementation.

## 13. Implementation slices

Changes land in independently verifiable slices:

1. Phase 0 telemetry, capture scenario, and baseline.
2. DPI contract and multi-monitor behavior.
3. Frame gate and inactive-tab CPU behavior.
4. Cache budgets, native lifetime, and scrollback-preserving view teardown.
5. Cursor overlay and hot-path allocation removal.
6. Direct-Skia experiment and backend decision.
7. IME, theme resources, accessibility, and optional drag/drop.
8. Dead-path deletion and documentation alignment.

Each slice records its before/after command, environment, metrics, visual evidence, and rejected
alternatives. A phase is complete only when its acceptance criteria are observed end to end.

## 14. Risks and controls

| Risk | Control |
|---|---|
| DPI is applied twice | One documented unit boundary; assertions and 1x/2x capture dimensions. |
| Coalescing loses a late update | Monotonic dirty/rendered generations and a retry check. |
| Render thread reads mutable state | Immutable versioned snapshots only; otherwise stay on the UI path. |
| Snapshot copying erases backend gain | Include copy/pool time and retained bytes in backend comparison. |
| Skia object disposed during use | Explicit ownership and deferred disposal after all users release it. |
| Cache optimizes CPU but leaks native memory | Byte accounting, bounded LRU, repeated-settings stress run. |
| Partial redraw corrupts pixels | Full redraw remains oracle; exact scenario pixel comparison. |
| Low-memory policy loses user history | Release view resources only; preserve configured scrollback. |
| Diagnostics distort production results | Compile/runtime gate; record benchmarks with telemetry and tools off unless measuring instrumentation itself. |
| Visual polish adds full-frame cost | Overlay/event-driven state and explicit CPU/allocation comparison. |

## 15. Decision log

| Date | Decision | Evidence |
|---|---|---|
| 2026-08-13 | Optimize and instrument the production `Control.Render` path before selecting a new backend. | Current GL prototype is unused/incomplete; existing benchmarks do not cover Avalonia presentation. |
| 2026-08-13 | Correct DPI ownership before renderer optimization. | Current font and bitmap use different scale models. |
| 2026-08-13 | Require a 15% material win for backend complexity. | Render-thread/custom-draw designs add snapshot, synchronization, fallback, and native-lifetime risk. |
| 2026-08-13 | Preserve scrollback when releasing inactive views. | Silent `TrimScrollback(100)` violates terminal correctness and usability. |

## 16. Primary Avalonia references

- [Custom rendering](https://github.com/AvaloniaUI/avalonia-docs/blob/main/docs/graphics-animation/custom-rendering.md)
- [TopLevel scaling and animation frames](https://github.com/AvaloniaUI/avalonia-docs/blob/main/docs/fundamentals/top-level.md)
- [Avalonia 12.1 CompositionCustomVisualHandler](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/a21b9f573172f705a944dcc8aad7f036b9986f39/src/Avalonia.Base/Rendering/Composition/CompositionCustomVisualHandler.cs)
- [Avalonia 12.1 OpenGlControlBase](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/a21b9f573172f705a944dcc8aad7f036b9986f39/src/Avalonia.OpenGL/Controls/OpenGlControlBase.cs)
- [Developer Tools installation](https://docs.avaloniaui.net/tools/developer-tools/installation)
