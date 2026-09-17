# State Coordination Hardening — Plan

Status: **Implemented 2026-08-12; retained as the terminal-core coordination contract** · Last updated: 2026-09-17

The terminal core coordinates three boundaries:

1. `TerminalBuffer.SyncRoot` protects parser mutations and coherent snapshot
   capture.
2. Buffer invariants cover hot cells, cold metadata, continuation width,
   scrollback, and row generations.
3. `RenderSnapshot` copies the visible state under a bounded lock and is
   rasterized after the lock is released by the Silk.NET/OpenGL host.

The hardening work centralizes invariant validation, keeps a single source of
truth for row invalidation, and makes alternate-screen and resize transitions
full invalidations. UI-framework-specific scrolling and presentation behavior
is outside this document.

## TL;DR

The observed terminal bugs were instances of two coordination failures:

1. **Invariants enforced by convention.** Cold metadata and `RowColdFlags` must
   agree across every write, clear, scroll, and copy path. The library validator
   now owns this contract so tests and fuzz loops do not duplicate it.
2. **State transitions must invalidate derived render data.** Resizes, resets,
   alternate-screen changes, and scrollback-origin changes bump the affected
   row generations. A snapshot and its GPU scene must never outlive the state
   they describe.

The current renderer's bounded lock attempt, reader-priority handoff, and
immutable snapshot provide the presentation boundary. Incremental row reuse is
tracked separately in `IncrementalScrollRendering.md`; it is not a live
coordination path.

---

## 1. R1 — Buffer invariants owned by the library

### Current state

- `CellHot` + `ColdCell` + `RowColdFlags` + `RowMaxCol` must stay mutually consistent. The
  invariants are:
  - Continuation cells have `Rune == 0`.
  - A base cell with `Width > 1` has its continuation cells set.
  - `RowColdFlags[row]` is true iff some cell on the row has hyperlink/grapheme metadata
    (`HyperlinkId != 0 || GraphemeIndex >= 0`).
  - `RowMaxCol` is a valid upper bound for the row's content.
- Four+ write paths touch these (`WriteAsciiRunBulk` char/byte, `WriteGrapheme`,
  `WriteGraphemeAscii`, `ClearCell`, scroll/insert/delete row copies). The emoji bug was the
  `RowColdFlags ⇔ cold metadata` invariant violated by one path; no test caught it because the
  duplicated helpers check continuation/width only — none checks cold flags.
- The checker is duplicated in 9 test files: `AdvancedEdgeCaseTests`, `AsciiArtRenderTests`,
  `EdgeCaseBufferTests`, `MoreReproTests`, `NeovimCaptureReplay`, `NeovimReplayTests`,
  `PermutationScrollRenderTests`, `ReproAttemptsTests`, `StressFuzzReproTests`.

### Options

**A. Library-owned validator: `TerminalBuffer.ValidateInvariants()` (or `Screen.Validate()`).**
Returns (or throws on) a list of violations; called by tests and wired into the fuzz loops
(`PermutationScrollRenderTests`, `StressFuzzReproTests`) so corruption is caught at the moment of
the offending operation. Test helpers become one-liners delegating to it.

- Pros: single source of truth; catches *any* consumer's violation incl. future incremental
  renderer; fuzz wiring makes it fail-with-cause instead of fail-at-end; catches the exact
  emoji-bug class (cold-flag consistency) which today's helpers miss.
- Cons: new public surface on `TerminalBuffer`; must be written carefully to be O(rows×cols)
  but only ever called in tests/debug (guard with `#if DEBUG` or document as test-only).

**B. Centralize the existing helpers into one test utility class.**
Move the 9 copies into a shared `BufferInvariant` test helper; keep the same checks.

- Pros: minimal change; removes duplication without touching the library.
- Cons: still test-land; the library still cannot validate itself; the missing cold-flag check
  would still be missing (unless added manually); fuzz loops still call a test helper.

**C. Structural enforcement: make the invariant unrepresentable.**
E.g. derive `RowColdFlags` on demand (scan row when needed) instead of storing it, or funnel all
cell mutation through a single `Screen` API that maintains flags.

- Pros: bug class impossible by construction.
- Cons: `RowColdFlags` exists precisely to skip per-cell cold-reset loops on clean rows — deriving
  it costs the hot path it was built to save; funneling every mutation through one API is a large
  refactor of the SIMD/unsafe write paths (`WriteAsciiRunBulk` writes cells directly with AVX2
  stores); high risk to performance for a low-frequency bug.

### Recommendation

**Implemented as A.** `TerminalBuffer.ValidateInvariants()` delegates to the
active `Screen` validator and remains test/debug-only. Structural enforcement
is deliberately deferred because `RowColdFlags` avoids cold-cell scans on the
hot write paths.

### Acceptance criteria

- `ValidateInvariants` fails on a deliberately corrupted row (each invariant, one test each).
- `WriteGrapheme` → ASCII overwrite sequence (the emoji repro) passes validation.
- All 9 test files call the shared validator; zero remaining copies of the inline loops.
- Full suites green; no measurable perf change (validator not called in the live path).

---


## 2. R2 — Alternate-screen invalidation

### Current state (verified against source)

`TerminalBuffer.SetAlternateScreen` switches the active screen and ends with
`MarkAllRowsDirty()`. Every row generation therefore changes across both
directions of a toggle, so a scene builder cannot reuse instances from the
other screen. Resize, reset, clear, and reflow use the same full-invalidation
rule.

The current OpenGL host has no renderer-side screen mirror to preserve across
the toggle. This full invalidation is nevertheless a required contract for any
future incremental row cache.

### Recommendation

Keep the single generation arrays and invalidate every row on a screen toggle.
Splitting generations per screen is unnecessary until an incremental renderer
needs to preserve rows across the transition.

### Acceptance criteria

- `SetAlternateScreen(true)` and then `(false)` bump every row generation.
- A future row cache clears its entries before consuming the next snapshot.
- Resize, reset, clear, and reflow likewise force a complete scene.
---

## 3. Sequencing, dependencies, and acceptance

1. Keep library-owned invariant validation in the terminal test and fuzz
   coverage.
2. Keep bounded snapshot capture and reader-priority handoff around the
   current scene composition path.
3. Treat alternate-screen, resize, reset, clear, and reflow as full
   invalidations before adding any renderer-side row reuse.
4. Validate future incremental work against the complete OpenGL scene; the
   design and test requirements live in `IncrementalScrollRendering.md`.

The shipped host must continue to preserve terminal content while typing,
scrolling, switching screens, resizing, and capturing a snapshot. The
validator is not called on the live rendering path.

## 4. Risks and open questions

- Should `ValidateInvariants` also run from `#if DEBUG` public mutators, or
  remain test/fuzz-only? Keep it test/fuzz-only until a measured need appears.
- A future row cache must not reuse data across an alternate-screen toggle,
  resize, reset, clear, or reflow.
- Snapshot capture and OpenGL scene composition must remain outside the parser's
  mutation lock after the bounded copy completes.
