# Testing

Dotty separates deterministic terminal-core tests from native PTY and desktop
smoke tests. The desktop executable is `src/Dotty/Dotty.csproj`.

## Test projects

| Project | Scope |
|---|---|
| `tests/Dotty.App.Tests` | parser, input, runtime, rendering, configuration, and host seams |
| `tests/Dotty.Terminal.Tests` | terminal buffer, parser, hyperlinks, and reflow |
| `tests/Dotty.NativePty.Tests` | `IPty` contracts, platform capabilities, Unix helper, and Windows ConPTY |
| `tests/Dotty.App.SkiaTests` | Skia/OpenGL-adjacent rendering behavior |
| `tests/Dotty.Conformance.Host` | not a test project: the headless `dotty-conformance-host` executable used by the esctest conformance run; referenced by `Dotty.App.Tests` and listed in `Dotty.slnx` (see [Conformance](Conformance.md)) |
| `tests/Dotty.Performance.Tests` | BenchmarkDotNet benchmarks and the `--mode scrollback-memory` / `--mode gate-self-test` entry points; not part of the functional test run (see [Performance](Performance.md)) |

Test projects intentionally do not set a fixed `RuntimeIdentifier`. Native
assets and conditional PTY tests must resolve for the runner that executes the
tests.

### Scrollback, storage, reflow, and text-extraction tests (wave 3)

`Dotty.App.Tests` (Terminal-internal members are visible to both `Dotty.App.Tests`
and `Dotty.Terminal.Tests` through `Dotty.Terminal`'s `InternalsVisibleTo`):

| Class | Covers |
|---|---|
| `ScrollbackPolicyTests`, `ScrollbackSessionConfigTests` | line/byte limits, normalization, `CapacityFor`, config to session wiring, resize under a policy |
| `ScrollbackCapacityReflowTests` | ring capacity following the column count under the byte cap on every reflow, spare retargeting |
| `VirtualArenaTests`, `ScreenLazyCommitTests` | arena backends and zero-fill guarantee, dirty-extent lazy commit, discard on ED 2 / CSI 3 J / RIS / alternate exit, RI with limited or zero capacity |
| `HistoryInvalidatedTests` | `HistoryInvalidated` reasons and `HistoryFloorAbsoluteRow` |
| `ReflowAnchorMappingOracleTests`, `ReflowRandomizedEquivalenceTests` | anchor-only mapping against the frozen `LegacyDenseReflowMapping` oracle over seeded random resizes; the recorded-golden layer is skipped until `Goldens/reflow-canonical.txt` is recorded on the pre-change commit with `DOTTY_RECORD_REFLOW_GOLDENS=1` |
| `RowTextTests`, `RowTextCharacterizationTests`, `SearchBufferContentTest` | single row-to-text implementation, search and scrollback search |
| `AllocationAssert`-based classes (`*AllocationTests`) | warmed-path allocation limits |

`Dotty.Terminal.Tests`: `ReflowResizeTests`, `BufferMemoryMetricsTests`,
`ColdCellTests`, `OscFastPathTests` and `OscAllocationTests` (bulk OSC scan,
bytes-native OSC 52), and `TerminalCoreAllocationTests` (resize with
column-dependent capacity allocates nothing after warm-up). Python helper tests
for the memory harness live in
`scripts/perf/tests/test_scrollback_memory.py`. None of these has been run on a
machine with the .NET SDK by the authors of this change set; GUI, Wayland,
macOS, and Windows behavior is verified manually.

### Kitty graphics tests

Written without a compiler or runtime and **never executed**; every class below
is `needs runtime verification`. `Dotty.Terminal.Tests` and `Dotty.App.Tests` both
see `Dotty.Terminal` internals (`InternalsVisibleTo`). The graphics classes share
`KittyTestSupport.cs` (`TestPng`, `FakeImageDecoder`, `FakeKittyHost`, `KittyRig`, command builders) and the
`GraphicsMemoryMetricsCollection` xUnit collection, because the
`GraphicsMemoryMetrics` counters are process-wide and tests assert that they
return to their baseline.

`tests/Dotty.Terminal.Tests`:

| Class | Covers |
|---|---|
| `KittyApcParserTests`, `KittyControlParserTests` | APC framing and abort rules, the 512-byte control cap, key parsing (`BoundedSequenceTests` gained a recording handler for the Kitty callbacks) |
| `KittyBase64DecoderTests`, `BoundedInflateTests`, `PngHeaderTests`, `PixelOpsTests`, `ImagePixelsTests` | streaming base64, bounded zlib, PNG header sanity, premultiply/downscale, reference counting |
| `KittyTransferTests`, `KittyTransferMediumTests`, `KittyFileMediumTests`, `KittyPathPolicyTests`, `KittySharedMemoryTests` | chunking, validation order, media, path deny-list and temp rules |
| `KittyControllerTests`, `KittyQueryTests`, `KittyResponseTests`, `KittyResponsesTests`, `KittyLimitsTests` | command routing, `a=q`, reply formatting and quiet modes, quota and caps |
| `KittyImageStoreTests`, `KittyPlacementTests`, `KittyRelativePlacementTests`, `KittyDeleteTests`, `PlacementGeometryTests` | ids, eviction, placement geometry and cropping, relative chains, the delete matrix |
| `KittyAnimationTests`, `KittyAnimationFlowTests`, `KittyFrameLoadTests` | frame load/edit/compose, gap and loop rules, playback clock |
| `KittyBufferHooksTests`, `KittyScreenSemanticsTests`, `KittySnapshotTests`, `IndexAndNextLineTests` | scroll/reflow/ED/RIS hooks, alternate screen, snapshot capture and leases, `ESC D`/`ESC E` |
| `KittyPlaceholderResolverTests`, `PlaceholderTableTests`, `PlaceholderWriterTests`, `DiacriticsTests`, `ImageQuadBuilderTests`, `ImageUploadPlannerTests`, `ImageRetirementQueueTests` | placeholder decoding and cells, quad building and clipping, upload/eviction planning |
| `ColorKindTests`, `SgrColonColorTests` | colon-form SGR colours that placeholders use (foreground/underline ids) |
| `KittyAllocationTests` | warmed-path allocation limits (a deliberately loose bound for a query with a payload) |
| `KittyMutationFuzzTests` | seeded mutation fuzzing of the whole path |

The fuzz test is **deterministic**: fixed seeds, 500 iterations each, no
wall-clock input. It asserts that nothing throws, stores stay consistent, replies
stay well-formed, counters return to baseline, and the parser recovers.

`tests/Dotty.App.Tests`: `KittyGraphicsConfigTests` (the `graphics` section,
normalization, catalog and Lua keys), `BlockingWorkScopeTests` (releasing and
re-acquiring `SyncRoot`), `MemoryReportTests`, `RenderSnapshotGraphicsTests`,
`TerminalSceneComposerImageTests`, `TerminalSceneComposerPlaceholderTests`,
`WindowPresentationGateTests` and `IdleWaitTests` (animation and upload wake
reasons), `TabInitialPixelSizeTests` and `TerminalSessionPixelResizeTests` (pixel
sizes through sessions), and `RenderingAllocationTests`.

`tests/Dotty.App.SkiaTests`: `SkiaImageDecoderTests`, `SilkImageTextureCacheTests`,
`SilkImageRendererTests`, `SilkTerminalRendererImageLayerTests` (layer order and
pixel assertions), and image cases in `QuadFrameBuilderTests`. The OpenGL cases
open a hidden OpenGL 3.3 window and call `Assert.Skip` when no context is
available, so on a headless runner they are reported skipped, not passed; they
are the only automated check of the real GL path. Run them on a machine with a
display.

`tests/Dotty.NativePty.Tests`: `UnixPtyPixelTests` (resize message with
`xpixel`/`ypixel`, the initial pixel size and pixel-only resizes being visible to
the child, `DOTTY_INITIAL_XPIXEL/YPIXEL`), the pixel
arguments in `IPtyContractTests`, and the passthrough flag cases in
`WindowsPtyTests` (flag mapping and fallback; the real ConPTY call only runs on
Windows).

There is no recorded-corpus replay and no golden-image regeneration step yet:
pixel assertions are computed in the tests. Manual client checks are the
[smoke checklist](KittyGraphics.md#manual-smoke-checklist).


`global.json` selects the Microsoft Testing Platform runner required by xUnit
4. Use `--solution` or `--project` explicitly; VSTest `--logger` is not
supported in this mode. Use `--report-xunit-trx` for TRX output.

## Local commands

```bash
# All deterministic and host-native tests on the current machine
dotnet test --solution Dotty.slnx -c Release --nologo

# Native PTY contract and integration coverage
make -C src/Dotty.NativePty
dotnet test --project tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj \
  -c Release --nologo

# A focused test project or class
dotnet test --project tests/Dotty.App.Tests/Dotty.App.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~GraphicsCapabilitiesTests'
```


### Troubleshooting zero tests

If a test command exits after reporting zero tests across all projects, check
which dotnet your shell resolves (`command -v dotnet`, or `type -a dotnet`) and
inspect its `--info` output. A local shell wrapper that injects raw MSBuild
switches into arbitrary `dotnet` subcommands can interfere with test-runner
argument parsing. Compare with the installed SDK executable directly, without
hard-coding a machine-specific SDK path in project scripts or documentation.

Windows builds define `WINDOWS` for both `Dotty.NativePty` and
`Dotty.NativePty.Tests`, so `WindowsPtyTests` compile and run on the Windows
runner. Unix builds compile `pty-helper` before native tests.

## Test boundaries

Use deterministic tests for parser modes, input encoding, bracketed paste,
reflow, selection, clipboard wrappers, configuration paths, graphics version
validation, and lifecycle queues. Use native tests for process creation, PTY
I/O, resize, helper discovery, ConPTY handles, and cleanup. Use desktop smoke
for display initialization, font/atlas setup, actual input, and shutdown.

Do not add a fixed Linux RID to a test project to make a local test pass; that
breaks Windows and macOS native asset resolution.

Tests in `tests/Dotty.App.Tests` that need a started session but not a child
process must not start a platform PTY: ConPTY clears the screen and resets modes
when it starts, which can land after (and erase) state a test feeds through the
parser. Create tabs with `new TerminalTabManager(SilentPty.Create)` (or the
internal `TerminalTab`/`PaneTree` overloads that take a PTY factory first, e.g.
`new TerminalTab(ptyFactory: SilentPty.Create, rows: 24, columns: 80)`), so the
session runs against the inert `SilentPty` in `SilentPty.cs`: no output, no exit,
and the bytes the session writes to the child are recorded. Real PTY behavior
belongs in `tests/Dotty.NativePty.Tests`. `TestShellEnvironment.cs` still sets
`DOTTY_SHELL` to a silent program as a fallback so a test that forgets the inert
PTY does not receive the developer's shell output.

Allocation tests measure `GC.GetAllocatedBytesForCurrentThread()` on the thread
doing the work, never process-wide counters, so concurrent test classes cannot
contaminate them.

## Clipboard, paste, hyperlink, OSC 7, and parser hardening tests

Deterministic coverage for the paste, OSC 52, OSC 8, OSC 7, environment, and
parser-string-state behavior documented in [Parsing](Parsing.md),
[Configuration](Configuration.md), and [Shell Integration](ShellIntegration.md).
These test the pure logic and host seams (builders, router, service, queue,
policy, parser, adapter); they do not draw a prompt or open a link, so the
behavior in [E2E Testing](E2ETesting.md#manual-smoke-osc-52-read-paste-confirm-and-link-confirm)
is manual verification with no CI coverage.

`tests/Dotty.Terminal.Tests`:

| Class | Covers |
|---|---|
| `ParserStringStateTests` | ESC/CAN/SUB/C0 handling in OSC, DCS, APC/PM/SOS, and CSI; no 0x9C terminator |
| `ParserRoutingTests` | DECSTR, `CSI ? Ps n`, `CSI Ps t`, DECRQCRA, modes 66/67/2027/2033 routing |
| `SaturatedParameterTests` | saturating integers; ICH/DCH/ECH/cursor clamping |
| `RepeatCharacterTests` | REP bound and equivalence with an unbounded repeat |
| `Osc52Tests` | OSC 52 selectors, write decoding, `?` read request |
| `Osc8IngestValidationTests`, `HyperlinkUriTests` | OSC 8 payload/URI validation, link closing on invalid input |
| `HyperlinkTableLimitTests`, `RenderSnapshotHyperlinkTests` | URI table caps (refuse-new) and snapshot lookup |
| `Osc7WorkingDirectoryTests`, `ShellIntegrationOsc7Tests` | strict OSC 7 parsing and adapter integration |

`tests/Dotty.App.Tests`:

| Class | Covers |
|---|---|
| `PasteEncoderTests`, `PasteSafetyTests` | paste sanitizing, bracketing, newline normalization, risk classification, preview |
| `PastePromptBuilderTests`, `ClipboardPasteRouterTests` | paste prompt contents and the confirm/skip/re-check decisions |
| `Osc52ReplyTests`, `ClipboardReadQueueTests` | OSC 52 reply encoding; queue bounds and expiry |
| `ClipboardReadServiceTests`, `ClipboardReadPromptBuilderTests` | read policy (deny/ask/allow), limits, focus/active-tab gating, prompt |
| `ClipboardUserConfigTests`, `HyperlinksUserConfigTests` | config defaults, clamping, and parsing |
| `HyperlinkPolicyTests`, `HyperlinkTextMismatchTests`, `UriCanonicalizationTests` | scheme policy, hard blocks, file rules, canonicalization, text mismatch |
| `HyperlinkActivatorTests`, `HyperlinkLauncherTests`, `HyperlinkScannerTests`, `HyperlinkIngestConfigTests`, `HyperlinkPromptBuilderTests` | activation order and Lua hook, launch start info, scanning, ingest switch, link prompts |
| `InheritableWorkingDirectoryTests`, `SessionLaunchEnvironmentTests` | use-time directory fallback; `TERM_PROGRAM*`/`DOTTY_SURFACE_ID` environment |
| `ContextMenuTests`, `TerminalMouseControllerTests`, `TerminalKeyboardDispatcherTests`, `LuaScriptingTests` | `Dismissed` and `HostPrompt` layout/sanitizing, menu replacement guards, Ctrl+click on OSC 8 links, `clipboard_read`/`open_url(url, source)` hooks and new config keys |

`tests/Dotty.NativePty.Tests`: `ShellIntegrationScriptTests` (Bash exercised for
real on Unix when present; zsh/fish/PowerShell are content checks) and
`UnixPtyTests` (the helper's protocol variables do not reach the child).

## VT, input, mouse, injection, and catalog tests

Deterministic coverage for the behavior in [Parsing](Parsing.md), [Keyboard Protocol](KeyboardProtocol.md), [Configuration](Configuration.md), [Shell Integration](ShellIntegration.md), and [Command-line usage](CommandLine.md). They test the adapter, parser, encoder, policies, and host seams; none of them opens a window, and none exercises X11, Wayland, macOS, or Windows desktop behavior.

`tests/Dotty.Terminal.Tests`:

| Class | Covers |
|---|---|
| `SoftResetTests`, `FullResetTests` | DECSTR and RIS reset sets, including what each keeps |
| `DeviceStatusTests` | DSR 5/6, DECXCPR, origin-relative CPR, silent unknown reports |
| `WindowOpsTests` | `CSI Ps t` replies, resize-request clamping and the 20/21 silence |
| `VisibilityReportTests` | mode 2033, `CSI ? 998 n`, edge triggering |
| `ChecksumReportTests` | gated DECRQCRA and the checksum definition |
| `CombiningCapTests` | the 64-mark cap and surrogate-safe truncation |
| `UnicodeWidthTests` | scalar widths against the generated golden (`UnicodeWidthGolden.g.cs`), table invariants, cluster width rules |
| `GraphemeSegmentationTests` | UAX #29 boundaries of .NET `StringInfo` that the buffer relies on (hand-written cases) |
| `BufferTextWriterClusterTests` | clusters split at every UTF-8 byte boundary or fed one byte at a time give the same screen; late VS16/VS15, cap, placeholder and right-edge cases |
| `ModeReportTests` | the complete DECRQM table and DECSET 47/1047/1048 |

`tests/Dotty.App.Tests`:

| Class | Covers |
|---|---|
| `BufferResetTests`, `GraphemeStorageTests` | buffer-side reset state; the bounded, de-duplicating grapheme store |
| `BackarrowKeyTests` | DECBKM Backspace encoding |
| `VtHostPolicyTests`, `VtUserConfigTests` | window-resize gating, visibility rule, client-size math, the resize mailbox; the `vt` config |
| `MouseOptionsTests` | `mouse` option parsing, copy-on-select, the in-process primary selection, primary paste |
| `ClipboardReadServiceTests`, `TerminalMouseControllerTests` | OSC 52 primary read policy; middle-click action and copy-on-select hook |
| `ShellIntegrationLauncherTests` | pure injection rewrite: argv and environment for every shell and every non-injection case |
| `ConfigCatalogTests` | the `+explain-config` drift guard (see [Adding an option](Configuration.md#adding-an-option)) and the action's output and exit codes |
| `DottyLaunchOptionsTests` | parsing of `+explain-config` and its conflicts with other options |
| `ConformanceHostTests` | the conformance host: option parsing, core switches, a run on a real PTY |

`tests/Dotty.NativePty.Tests`: `ShellIntegrationInjectionTests` (the bash rcfile wrapper for real on Unix when `bash` is present; zsh and fish shims are content checks only; csproj packaging of the injection directory). The Ubuntu `build-and-test` job installs `zsh` and `fish`, but no test launches either of them, so real zsh/fish/PowerShell injection is manual verification only.

The Python unit tests for the esctest scripts run with `python3 -m unittest discover -s scripts/esctest/tests` (CI: `code-quality`).

`python3 tools/gen-width-tables.py --check` (CI: `code-quality`) verifies that the committed width tables and their golden match a fresh generation from Python's `unicodedata` and that the range compression agrees with the per-code-point rules for every code point. It needs a Python whose `unicodedata` is Unicode 16.0.0 (Python 3.14) and refuses any other version; it does not need .NET. Run without `--check` to regenerate both files.

### Key-event replay harness

`tests/Dotty.App.Tests/KeyPipelineHarness.cs` drives the real `TerminalKeyboardController` and `TerminalKeyboardDispatcher` with the event sequences GLFW produces: key press, then the character, then key release (repeat, then character), each with the native `mods` bit field. The host's Ctrl/Shift/Alt/AltGr/Super and lock state comes from the controller's `KeyboardModifierState`, the same class `DottyWindowHost` uses, so no test sets modifier flags by hand. A harness is created for a layout fixture and a platform profile:

- Layout fixtures (`KeyboardLayoutFixtures.cs`): QWERTY (US), QWERTZ (German, AltGr), AZERTY (French), Dvorak, Russian (JCUKEN), and macOS ABC. Each maps the physical key to its unshifted, shifted, AltGr, and Option text; the unshifted text is the primary codepoint (`glfwGetKeyName`).
- Platform profiles `X11`, `Wayland`, `Win32`, `Cocoa` reproduce the vendored GLFW rules: C0/C1 characters are never delivered; AltGr is ISO Level 3 with no Alt bit on X11/Wayland and Ctrl+Alt (with the synthetic Left Ctrl event dropped) on Windows; Alt chords are `WM_SYSCHAR` on Windows; Option composes on macOS; Ctrl applies the X11 control transform.

`KeyPipelineHarnessTests` replays AltGr, Right Alt, Alt+Shift, non-Latin Ctrl, Option, Super, and bare-modifier sequences and asserts the PTY bytes, the search query, bindings, scroll offset and selection. The harness proves the managed routing for a given event sequence; whether a real OS delivers that sequence is checked manually (see [Native desktop and IME](NativeDesktopAndIme.md)).

`LegacyKeyEncodingTableTests` is the legacy (no Kitty flags) encoder table: more than 150 rows of key × {none, Shift, Alt, Ctrl, Ctrl+Shift, Alt+Shift, Ctrl+Alt} × {DECCKM, DECKPAM, DECBKM}, built from the xterm and kitty reference tables; it doubles as the encoding reference cited by [Keyboard Protocol](KeyboardProtocol.md#compatibility-behavior).

`KittyKeyboardSpecTests` is the Kitty keyboard conformance table: each row cites the rule of the published specification it asserts (Enter/Tab/Backspace legacy bytes and release events, modifier-key events only with flag 8, lock bits on text keys, the shifted alternate key, the functional key forms), plus golden byte sessions for `CSI > 1/3/11/31 u` replayed through the harness with Num Lock on.

## Desktop smoke

Linux X11:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release --nologo
HOME=/tmp/dotty-test-home DOTTY_CONFIG_HOME=/tmp/dotty-test-home/config \
  timeout 8s xvfb-run -a dotnet run --project src/Dotty/Dotty.csproj \
  -c Release --no-build
```

Exit status `124` is expected because the host remains open. Any earlier exit,
loader failure, OpenGL initialization error, or unhandled exception fails the
smoke. CI runs this X11 smoke under `xvfb-run`; Wayland, macOS desktop, and
Windows desktop paths are verified manually or on local native sessions, not
in CI. X11/Xvfb startup does not prove Wayland, macOS, or Windows GUI behavior.

The optional control transport is loopback-only and enabled with
`DOTTY_TEST_PORT`. The cross-platform harness is
`.opencode/skills/terminal-tester/dotty-interact.sh`; it scopes state/PID files,
uses Python sockets instead of assuming `nc`, and never kills unrelated
processes. See [GUI harness benchmarking](GuiHarnessBenchmarking.md).

The control commands `WAIT_FOR_IDLE`, `SCREENSHOT:<path>`, `GL` and the
`frameTimings` block of `STATS` are specified in
[End-to-end Testing](E2ETesting.md#host-control-interface). Their pure parts are
covered headlessly in `Dotty.App.Tests`: `GraphicsCapabilitiesTests` (driver
string classification, version parsing), `FrameTimingsTests` (percentiles, ring
wrap, present interval, input-to-swap, allocation-free recording and
summaries), `FramebufferScreenshotTests` (row flip, opaque alpha, un-premultiply
through a PNG round trip) and `IdleWaitTests` (the parked-request deadline).
Frame scheduling is covered by `FramePlannerTests` (gate, idle/hold/render
table, refresh-rate interval table), `IdleWaitTests` (absolute blink and Lua
deadlines, bell and control deadlines, safety cap) and `HostWakeCoverageTests`
(cross-thread enqueue APIs post a wake; a source scan of `src/Dotty/Host`
requires `HostWake.Request()` after every queue producer). Render recovery is
covered by `RenderRecoveryPolicyTests` (retry, rebuild, fatal, quiet-period
reset, context resets) and, in the GL lane of `Dotty.App.SkiaTests`,
`SilkTerminalRendererRebuildTests` (a disposed renderer is replaced on the same
context and draws again; also the `ReadBackBuffer` capture hook), which skips
without an OpenGL 3.3 context. None of these has been run by the author of this
change (no SDK available); `needs runtime verification`.

## Native Dotty output benchmark

Use `scripts/perf/dotty_output_bench.py` for the same unprofiled Dotty workload on native Windows and POSIX, including NativeAOT apphosts. It requires Python 3, `psutil` (`python -m pip install psutil`), a built/published apphost, and a usable desktop session. Missing `psutil` is an explicit error; .NET diagnostics tools and competitor terminals are not needed.

```bash
python scripts/perf/dotty_output_bench.py --app /path/to/dotty \
  --output-root artifacts/perf/dotty-output \
  --runs 1 --warmup-runs 0 --lines 10000 \
  --cols 80 --rows 24 --font-family Consolas --font-size 16
```

For longer comparisons, use e.g. `--runs 3 --warmup-runs 1 --lines 2000000`. On Windows use `python` and a native `dotty.exe` apphost path; on Linux use `python3` and a native `dotty` path. The default geometry/font is 80x24 and Consolas 16px; override the font if unavailable and keep it fixed across comparisons. Keep grid, font availability, display/backend/scale, and binary/workload hashes fixed; keep windows visible and unoccluded. The harness uses an isolated per-run configuration, not user config.

Every invocation creates a unique UTC directory with `summary.json`, a concise `report.md`, and retained per-run JSON, logs, source-byte acknowledgments, actual child/host geometry, binary/script hashes, endpoint observations, memory samples, child events, and final screen. Warmups are retained but excluded from summary statistics.

Raw writes can consume only a prefix: Python's unbuffered Windows console writer was observed returning 12,287 bytes for a 55,000-byte request. Ignoring the return value silently loses the suffix. All maintained output workload writers—including the benchmark, comparison, and profiler workloads—use `output_write.py` to retry remaining bytes, reject missing progress, and preserve partial final chunks. Acknowledged payload and marker bytes are recorded separately; source bytes exclude marker bytes and PTY-added bytes, so parsed totals can differ. The deterministic regression tests assert exact bytes across capped writes and partial final chunks:

```bash
python -m unittest discover -s scripts/perf/tests -p test_output_write.py
python -m unittest discover -s scripts/perf/tests -p test_dotty_output_bench.py
```

Producer timing ends after complete output and flush. The final endpoint requires the marker in the terminal buffer, parser drain (empty queue and equal PTY read/parsed counts), and a new submitted frame with a new present count whose generation matches the model. Parser and submitted-generation durations are first observer measurements after marker presence; the generation endpoint follows `SwapBuffers`. These are **not** GPU completion or physical/compositor presentation. Windows memory is sampled working set; Linux memory is sampled RSS. Root/process-tree maxima cover startup through the endpoint, exclude observer and cleanup, and are not exact peaks. Small-run statistics are descriptive observations, not population percentiles; warmups are retained but excluded from mean/median/range summaries, and profiled throughput must not be merged with these unprofiled observations. This is a full-stack producer/PTY/parser/render measurement, not an isolated parser or NativeAOT speed test. Keep producer APIs consistent; OS PTY transport costs, including Windows ConPTY, can dominate output duration.

## CI matrix

The authoritative workflow is `.github/workflows/ci.yml`:

| Job | Runner/RID | Coverage |
|---|---|---|
| `build-and-test` | Ubuntu `linux-x64` | POSIX helper, host-native tests, and native PTY smoke |
| `build-and-test` | macOS Intel `osx-x64` | POSIX helper and host-native tests |
| `build-and-test` | macOS arm64 `osx-arm64` | POSIX helper and host-native tests |
| `build-and-test` | Windows `win-x64` | Host-native tests and ConPTY smoke |
| `aot-publish` (reusable `aot-publish.yml`) | `linux-x64` in a Debian 11 (`debian:bullseye`) container on Ubuntu, Windows `win-x64`, macOS arm64 `osx-arm64`, macOS Intel `osx-x64` | NativeAOT `dotnet publish` of the shipped configuration; `scripts/dist/verify-publish.sh`/`.ps1` file asserts, `dotty --version` (central version or a `-dev+<rev>` build) and `dotty +explain-config font.size`; fails on IL2xxx/IL3xxx warnings in Dotty's own code and lists third-party ones (`scripts/dist/aot_warnings.py`); fails when the measured glibc / macOS deployment target exceeds `scripts/dist/baseline-targets.json` (`scripts/dist/check_baseline.py`) |
| `aot-publish` / `terminfo` | Ubuntu | `tic -x` compile of `terminfo/xterm-dotty.src` and an `infocmp -x` round trip; the compiled entry is packaged into the Linux and macOS publish output (`terminfo/x/` and `terminfo/78/`) |
| `gui-smoke-linux` | Ubuntu `linux-x64` | X11 desktop startup under Xvfb |
| `code-quality` | Ubuntu | `dotnet format whitespace` verification, the Python unit tests for the benchmark, esctest and distribution (`scripts/dist/tests`) scripts, and `tools/gen-width-tables.py --check` on Python 3.14 (its `unicodedata` is UCD 16.0.0) |
| `esctest-linux` | Ubuntu | esctest2 conformance against the headless host; **not gating** until `tests/esctest/expected-failures.txt` carries the `# baseline-captured:` marker (see [Conformance](Conformance.md)) |
| `performance-tests` | Ubuntu | Quick BenchmarkDotNet run on pull requests and `main` |

The hosted macOS and Windows runners build and test native code but do not run
desktop GUI smoke: they lack a usable OpenGL/GUI session. CI has no
Wayland/Weston smoke. Linux arm64 and Windows arm64 remain local/manual
candidates without an automated nightly or release-publishing lane; see the
[Release Policy](Releasing.md).

## Release verification

Release builds run the same reusable `aot-publish.yml` workflow as CI, with the
exact release version. Release jobs must:

1. build Unix `pty-helper` and require it in Unix publish output;
2. NativeAOT-publish each RID and verify it with `scripts/dist/verify-publish.sh`
   (Linux, macOS) or `scripts/dist/verify-publish.ps1` (Windows): `dotty`/`dotty.exe`,
   RID-specific GLFW/Skia/Lua native libraries, the shell-integration payload,
   `License.md`, `THIRD-PARTY-NOTICES.md` and `licenses/GLFW-LICENSE.md`,
   `dotty --version` equal to the release version, and `+explain-config`;
3. fail on IL2xxx/IL3xxx warnings in Dotty's own code, and on a measured glibc or
   macOS deployment target above `scripts/dist/baseline-targets.json`;
4. inspect every tar/zip archive before release creation (`scripts/dist/verify_archive.py`);
5. render frames from the extracted Linux archive under Xvfb with software Mesa,
   driven through the control interface by `scripts/native/desktop-smoke.py`;
6. generate `MANIFEST.txt`, `SHA256SUMS`, `baselines.json`, release notes from
   the measured baselines (`scripts/dist/release_notes.py`), and a build provenance
   attestation.

See [Platform Support](PlatformSupport.md) for promotion gates and
[End-to-end Testing](E2ETesting.md) for the command-level smoke contract.
