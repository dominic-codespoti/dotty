# State Coordination Hardening — Plan

Status: **Implemented 2026-08-12.** Decisions taken: R1 = library validator, tests + fuzz loops
only (no DEBUG hook); R2 = coalesced snapshot-fed extent apply; R3 = Option B (renderer half
deleted, epoch contract kept); R4 = regression test added. R1 additionally surfaced and fixed
three latent copy-path bugs (region-scroll/IL/DL cold-flag transfer, IL/DL maxCol transfer,
resize-narrowing dangling wide bases). Verification: full suites green; live 500k-line scrollbar
smoke passes. Each remediation item below records the options considered and their tradeoffs so
the decision is traceable.

## TL;DR

Two recent bugs — the `ret❤️rn` ghost on ASCII overwrite and the randomly-positioned scrollbar
during `yes` output — were not random failures of the buffer model. They are two instances of the
same two structural patterns, plus one instance of a third:

1. **Invariants enforced by convention, not by the library.** The emoji bug was a write path
   (`WriteGrapheme`) mutating `ColdCell.GraphemeIndex` without setting `Screen.RowColdFlags`; a
   different write path (`WriteAsciiRunBulk`) trusts that flag to decide whether to clean cold
   metadata. The invariant "cold metadata present ⇒ row flag set" was held by discipline, and the
   discipline is evidenced by the checker being copy-pasted into **9 test files**
   (`AssertBufferClean` / `AssertNoOrphanedBases`) instead of living in one place.
2. **Scroll offset has no single owner.** `_offset` is written by the `Offset` setter (user drag
   via `ScrollViewer`), `UpdateScrollState` (system follow), `ScrollToRow` (prompt jump) — and
   previously `TerminalGrid.SetBuffer`'s `ScrollViewer.ScrollToEnd()`. Worse,
   `UpdateScrollState` runs **twice per frame** with different captured state (sync from
   `HandleBufferGeometryChange`, posted from `RenderToBitmap` with a captured `sbCount`). Two
   writers with no happens-before ⇒ nondeterministic result — the "sometimes it goes all the way
   down, sometimes not" fingerprint. The `explicitScrollbackCount` parameter exists purely to
   paper over the re-read race.
3. **The incremental renderer was reverted but left in place as a dead twin.** The live path is
   full render (`canvas.Clear` + re-render everything, ~11.7 ms at 73×136). All of the incremental
   machinery — `ComputeExposedRows`, `ApplyScrollToMirror`, `MemmoveRegionRows`,
   `MemmoveWholeFrame`, `ComputeDirtyRows`, `RenderDirty`, the `PendingScroll` queue (enqueued by
   4 buffer operations and **drained-and-discarded** every frame in `RenderToBitmap`), the
   `_rowScrollEpochs` motion-epoch array, and an **empty 0-byte `RenderSnapshot.cs`** — remains.
   Two render paths coexist; future "optimizations" can target the wrong one (that is exactly how
   the band bug and the offset-starvation bug happened the first time).

This document plans four remediations (R1–R4) with options, pros/cons, recommendations, and
acceptance criteria. Nothing here changes the buffer model itself, which is sound: every bug this
session was 1–2 lines once root-caused, and the pixel-diff harness caught every render regression.

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

**A**, with a debug-guarded implementation and the cold-flag check added. This is the cheapest
fix that catches the observed bug class at its source, and it de-duplicates 9 copies of the
checker. **C** is the right long-term direction only if the hot path can afford it — revisit
after R3 decides the incremental renderer's fate (epoch/flag maintenance cost differs by path).

### Acceptance criteria

- `ValidateInvariants` fails on a deliberately corrupted row (each invariant, one test each).
- `WriteGrapheme` → ASCII overwrite sequence (the emoji repro) passes validation.
- All 9 test files call the shared validator; zero remaining copies of the inline loops.
- Full suites green; no measurable perf change (validator not called in the live path).

---

## 2. R2 — Single-owner scroll state

### Current state

- `_offset` writers: `Offset` setter (ScrollViewer user drags call it via `ILogicalScrollable`),
  `UpdateScrollState` (system follow/clamp), `ScrollToRow` (prompt navigation). Until today,
  `TerminalGrid.SetBuffer` also called `ScrollViewer.ScrollToEnd()` — removed, but the pattern of
  multiple writers remains.
- `UpdateScrollState` runs twice per frame:
  1. Synchronously from `HandleBufferGeometryChange` (reads live `ScrollbackCount`).
  2. Posted at `DispatcherPriority.Render` from `RenderToBitmap` with a captured `sbCount`
     (`explicitScrollbackCount`) — the parameter exists only because re-reading the live value
     at post time yields a newer extent than the one the follow decision was made against.
- Trace evidence of the race (pre-fix): `FOLLOW newOff=1874` → `OFFSET old=1874 new=2794`
  (the ScrollToEnd write, computed against a stale extent) → all subsequent updates report
  `atBottom=False` and output never scrolls into view.
- The Avalonia constraint is real: `UpdateScrollState` → `ScrollInvalidated` →
  `ScrollContentPresenter.InvalidateMeasure` throws if invoked inside a render pass, so the
  update must be deferred (posted) — the fix must keep the defer, not make it synchronous.

### Options

**A. One coalesced, snapshot-fed update per frame.**
Capture `(rows, cols, sbCount)` under `SyncRoot` at render start. A single posted
`ApplyExtent(snapshot)` per frame (guard flag, latest-snapshot-wins) replaces both call sites;
delete the `explicitScrollbackCount` parameter. Contract:
- *New extent* comes from the snapshot (one consistent value per frame).
- *User intent* comes from live `_offset`/`_lastExtent` at apply time (a wheel-up that lands
  before the post breaks `wasAtBottom` and correctly cancels the follow).

- Pros: one writer per frame; no re-read race by construction; smallest change to the live hot
  path; keeps the mandatory defer; the follow logic itself (already correct once the competing
  writer is gone) stays as-is.
- Cons: still two code paths in the sense of "sync capture + async apply" — but they are
  strictly ordered (capture before post, one per frame), which is the property that matters.

**B. Event-driven: `TerminalBuffer` raises a `ScrollbackChanged`/`Updated` event; canvas
subscribes.**
- Pros: no polling; the buffer announces growth; decouples the view from frame timing.
- Cons: new coupling direction (buffer → view events; buffer currently has none — the adapter
  owns `RenderRequested`); event storms under `yes` output need coalescing anyway (same guard
  flag); more surface for the same result as A.

**C. Drop `ScrollViewer`; draw a custom scrollbar.**
- Pros: one owner of offset (the canvas) with no framework interference; full styling control.
- Cons: re-implements drag, wheel, page-up/down, hit-testing — a large UI change for a race that
  A already eliminates; the ScrollViewer also provides the `ILogicalScrollable` contract the
  code already implements.

**D. Keep as-is** (post-fix state: only canvas writes offset, but still twice per frame with
different captured state).
- Pros: zero risk.
- Cons: the double-update remains a latent trap: any future path that reads live state in the
  posted callback re-introduces the race; the `explicitScrollbackCount` parameter is a standing
  code smell inviting "fixes".

### Recommendation

**A.** It is the minimal change that makes offset updates a pure function of
(snapshot, current offset) with a single writer per frame, and it deletes the
`explicitScrollbackCount` workaround. **B** is a viable alternative if the team prefers
event-driven; **C** is out of scope for this plan.

### Acceptance criteria

- `TerminalCanvas_FollowsBottomAsScrollbackGrows` passes (added).
- Live 500k-line `yes | head` smoke test: scrollbar reaches bottom at completion; `atBottom`
  remains true throughout streaming (trace-enabled check during development, no trace left in).
- User wheel-up mid-stream cancels follow and does not get yanked to bottom by a stale post.
- No `UpdateScrollState` invocation carries a value that could differ from the frame's snapshot.

---

## 3. R3 — Resolve the dormant incremental render machinery

### Current state

Live path: full render every frame (`canvas.Clear(bgColor)` + `RenderToBitmap`, ~11.7 ms at
73×136 — bench-verified acceptable). Everything below is **not executed** by the live path:

| Symbol | Location | Live-path contact |
|---|---|---|
| `PendingScroll` queue + enqueue sites | `TerminalBuffer` (4 ops) | Enqueued on every scroll; **drained-and-discarded** each frame in `RenderToBitmap` |
| `PendingScrollCount`, `TryDequeuePendingScroll` | `TerminalBuffer` | Tests only |
| `_rowScrollEpochs` + `RowScrollEpochs`/`GetRowEpoch` + `ScrollEpochMath` | `TerminalBuffer` | Bumped on every mutation (`MarkRowDirty` et al.); consumed by tests only |
| `ComputeExposedRows`, `ApplyScrollToMirror`, `MemmoveRegionRows`, `MemmoveWholeFrame`, `ComputeDirtyRows` | `TerminalCanvas` | Tests only |
| `RenderDirty` | `TerminalFrameComposer` | Tests only |
| `RenderSnapshot.cs` | `Dotty.Terminal/Adapter/Buffer/` | **0-byte empty file** |
| `IncrementalRenderTests`, `ScrollExposedRowsTests`, `ScrollEpochTests` | tests | Exercise the above |

The `IncrementalScrollRendering.md` doc plans a future re-attempt (Phases 0/0.5/A/B/C) that
would re-enable most of this.

### Options

**A. Delete everything dormant (queue, epochs, canvas/composer primitives, empty stub, their
tests).**
- Pros: one render path; no dead code; removes the per-frame queue enqueue/discard overhead and
  the per-mutation epoch bumps; no future maintainer can re-enable an unverified path by
  accident; git history retains all of it.
- Cons: the re-attempt (if it happens) re-writes the primitives and re-derives the band math the
  pixel-diff tests verify — the exact math that took a full session to get right;
  `ScrollExposedRowsTests`/`IncrementalRenderTests` are the verification harness for that math.

**B. Delete the dead *renderer* half, keep the buffer-side contract.**
Delete the `PendingScroll` queue (and its discard loop), `RenderSnapshot.cs`, and the canvas /
composer primitives. Keep `_rowScrollEpochs` + `ScrollEpochMath` + `ScrollEpochTests`.
- Pros: removes all live-path overhead and the dead renderer twin; keeps the cheap, tested
  buffer-side epoch contract that the design doc's Phase A explicitly depends on (rotation +
  exposed-row bump); smaller diff than A.
- Cons: `ScrollExposedRowsTests`/`IncrementalRenderTests` still go (they test the deleted canvas
  primitives); the epoch machinery remains unused-by-live-path (though cheap and tested).

**C. Keep everything; mark dormancy explicitly** (namespace/comment banner + doc cross-ref).
- Pros: zero diff risk; preserves the harness for the planned re-attempt.
- Cons: the exact state that produced the first two regressions (band math bug, starvation bug)
  — dormant code that looked live; the empty stub stays as a trap; per-frame queue overhead stays.

### Recommendation

**B**, contingent on the decision recorded in `IncrementalScrollRendering.md`:
- If Phases 0/0.5 (instrumentation + cheap wins) are the committed next step and A/B/C remain
  "maybe", deleting the renderer half now is safe — it is the half that burned us, and the doc
  already specifies how to rebuild it with the pixel-diff harness.
- Keep epochs: they are the documented buffer-side interface for the re-attempt, cheap, and
  independently tested. If the cheap wins land and A/B/C is dropped for good, a follow-up
  deletion of epochs (Option A) becomes attractive.

### Acceptance criteria

- Zero references to deleted symbols anywhere in `src/` or `tests/` (build + grep clean).
- No `_pendingScrolls` enqueue/discard in the live path.
- `ScrollExposedRowsTests` / `IncrementalRenderTests` removed or migrated; `ScrollEpochTests`
  retained.
- Full suites green; live smoke (typing, scrolling, `yes`) unchanged.

---

## 4. R4 — Alt-screen invalidation: verify and codify

### Current state (verified against source)

The design review flagged `_rowGenerations` sharing across main/alt screens as a latent bug.
Current code **already mitigates it**: `SetAlternateScreen` ends with `MarkAllRowsDirty()`, which
bumps both generations and motion epochs for every row and clears the `PendingScroll` queue; the
canvas additionally resets composer caches on alt change (`_lastBufferWasAlternate`). In the
full-render world this is complete.

The risk re-appears only if the incremental renderer returns and someone *optimizes* the toggle
to preserve main-screen rows across it — which would be correct only with per-screen arrays.

### Options

**A. Add a regression test** asserting that toggling alt screen bumps every row generation/epoch
and clears pending scrolls.
- Pros: codifies the invariant; near-zero cost; fails loudly if the toggle is ever "optimized".
- Cons: none material.

**B. Split generation/epoch arrays per screen now.**
- Pros: removes the shared-state hazard structurally before any future incremental work.
- Cons: touches `ScreenManager` + all row-indexed logic; no live-path benefit today (toggle
  already invalidates everything); churn for a latent risk.

**C. Do nothing; rely on the doc note.**
- Pros: zero diff.
- Cons: the hazard is unguarded; the first "optimization" silently reintroduces it.

### Recommendation

**A**, plus a one-line note in `IncrementalScrollRendering.md`'s blocking-preconditions section
(already present; keep it). **B** only if Phase A of the incremental work starts.

### Acceptance criteria

- New test: `SetAlternateScreen(true)` then `(false)` bumps all generations/epochs; pending
  scrolls cleared; passes with current code.

---

## 5. Sequencing, dependencies, and acceptance

1. **R1** (library validator + test de-dup) — independent; cheapest; do first.
2. **R2** (single-owner scroll state) — independent; view-layer; needs live verification with
   the `terminal-tester` harness (wheel-up mid-stream + 500k-line smoke).
3. **R3** (dormant machinery) — depends on the R3 option decision (recommended B); do after R1
   so the shared validator is in place before test files are touched/removed.
4. **R4** (alt-screen test) — trivial; anytime; note in incremental doc.

Global acceptance: `Dotty.App.Tests` + `Dotty.Terminal.Tests` full suites green; live smoke
(typing, wheel scroll, nvim session, 500k-line `yes`) behaves; no perf regression on the
73×136 full-render benchmark (~11.7 ms).

## 6. Risks and open questions

- **R2 must keep the posted defer** — making the update synchronous to "simplify" would throw
  from inside the render pass (Avalonia). The guard flag is load-bearing; test the wheel-up
  mid-stream case explicitly.
- **R3 deletion is one-way-ish**: recoverable from git, but the exposed-band math took a full
  session to verify. If the team expects Phase A/B within weeks, prefer C and re-evaluate.
- **Open question**: should `ValidateInvariants` also run in `#if DEBUG` builds at the end of
  public mutators (fail-fast in dev) or only from tests? Recommendation: tests + fuzz loops only
  for now; a DEBUG hook is a cheap follow-up if fuzzing still misses something.
- **Open question**: R2 snapshot — extend it to a small `RenderState` struct reused by
  `HandleBufferGeometryChange` and the composer (future-proofing for Phase 0 instrumentation),
  or keep it local to the extent update? Recommendation: local for now; promote when Phase 0
  actually starts.
