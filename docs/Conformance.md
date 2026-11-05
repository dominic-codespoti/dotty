# VT conformance (esctest2)

Dotty is checked against the community conformance suite [esctest2](https://github.com/ThomasDickey/esctest2) on **Linux only**: esctest drives a program through a Unix tty and termios, and four explicitly selected cases also require a real X11 GUI. The run covers terminal-core behavior plus those four native window/clipboard paths; it is not a general rendering or platform-compatibility test (see [Testing](Testing.md) and [Platform Support](PlatformSupport.md)).

> **Status.** The 2026-11-05 G13/G14 run against pinned esctest2 `2798f12149a19c3295e9b4853ab2da4b2eff1b2b` accounted for all 567 names: **527 passed, 6 failed, and 34 retained their upstream expected-failure annotations**. **257 of the original 263 failures are resolved**, including **38 of this batch's 40 targets**; all 489 previous passes remain passing. G01-G11 and G14 are complete. By explicit user decision, four G12 DA/DA2 identity mismatches and two G13 identity-dependent report-arity mismatches remain visibly failing: no spoofing, exclusions, xfails or alternate identity profile. The four host-dependent originals now run in an explicitly authorized real-GUI fixture; product permissions remain safe by default. Suite commit, arguments, checksum/reverse-wrap profiles, VT level, assertions and annotation names are unchanged. There were no harness faults, skips or not-run outcomes. Reporting-mode exit `0` is not a full conformance pass; regression gating remains **off** (see [Gating](#gating)).

The [conformance completion plan](ConformancePlan.md) accounts for every failing test, its required behavior, implementation dependencies, specification/profile reviews and verification criteria. It also lists the separate upstream expected-failure audit.

## Components

| Path | Role |
|---|---|
| `tests/Dotty.Conformance.Host/` | `dotty-conformance-host`: runs a command on a real PTY whose terminal side is a headless Dotty `TerminalSession` (the production parser, adapter, and buffer; no window, no GL). It knows nothing about esctest. |
| `scripts/esctest/run_esctest.py` | The runner: fetches pinned esctest2, runs 563 cases in the headless PTY host and four selected cases in the real Dotty GUI under Xvfb/Openbox, merges their outcomes, compares expectations, writes reports, and sets the exit code. |
| `scripts/esctest/esctest_results.py` | Pure parsing, normalization, comparison, and report rendering (standard library only). |
| `scripts/esctest/tests/` | Python unit tests of the two scripts (they need no .NET and no esctest2). |
| `tests/esctest/esctest.json` | Pinned esctest2 repository and commit, grid size (25 x 80), host and esctest arguments, per-class timeout, expected full-run count, Dotty GUI executable, and `minPassed`. |
| `tests/esctest/expected-failures.txt` | The expected-failure and quarantine list, and the `# baseline-captured:` marker. |
| `.github/workflows/ci.yml` (`esctest-linux`) | The CI job. |
| `.github/workflows/esctest-baseline.yml` | Manual, Linux-only capture of expectations using the same headless and GUI lanes as CI. |
| `tests/Dotty.App.Tests/ConformanceHostTests.cs` | .NET tests of the host (option parsing, core switches, a run against a real PTY). `Dotty.App.Tests` references the host project, and `Dotty.slnx` lists it. |

## How a run works

1. The runner reads `esctest.json` and makes sure `.esctest/esctest2` holds the pinned commit, fetching it with `git fetch --depth 1` on the first run (this needs network access; CI caches the directory by the hash of `esctest.json`). A checkout at a different commit is an error: delete the directory to refetch.
2. It discovers the test classes from `esctest/tests/__init__.py`.
3. For each class, the headless lane starts one host process (`--jobs` at a time), so a hang or crash cannot take other classes down: `dotty-conformance-host --working-directory <checkout>/esctest --rows 25 --columns 80 --timeout-seconds 300 --checksum-reports --window-resize --title-reports [--transcript ...] -- python esctest.py --logfile <log> --include '^Class\.' <esctestArgs>`. Its include expression excludes the four named GUI cases; the runner then starts those four, one at a time, in the real Dotty GUI. The suite directory is explicitly passed to the PTY child; the runner also resolves output paths before changing directories. The esctest arguments select an xterm-compatible expectation set (`--expected-terminal=xterm --xterm-checksum=334 --xterm-reverse-wrap=383 --max-vt-level=5`, `xtermWinopsEnabled`). Checksum and reverse-wrap versions are independent: the explicit reverse-wrap selection distinguishes mode 45 (soft-wrapped lines) from mode 1045 (extended wrapping), avoiding contradictory legacy/new assertions without removing tests. Title reports use their own explicitly approved fixture permission, not the window-resize flag.
4. esctest has no machine-readable output and always exits 0, so the runner parses each esctest logfile. Each test is `pass`, `fail`, `known-bug`, `skipped` (esctest decided the terminal lacks a capability), `not-run` (no outcome: hang or crash), or `harness-fault`. A headless class is complete only when its log has the summary line and host did not exit with 124 (timeout), 125 (host error), or 130 (interrupted); each GUI invocation must also produce exactly its one expected result and a completed real-GUI lifecycle.
5. Failures are normalized: a failure with an `in reset` frame is a `harness-fault` (the host did not answer esctest's startup queries), and a `Should have failed` error on an xterm known-bug test counts as a pass.
6. Results from both lanes are merged and compared with expectations (below); reports go to `--out` (default `artifacts/esctest`): `results.json`, `summary.md` (also appended to the GitHub step summary), `merged.log`, per-class `logs/`, per-case GUI logs/metadata under `logs/gui/`, and `transcripts/` only for classes that failed, hung, or had faults (`--keep-transcripts` keeps all).

## Real GUI conformance cases

Exactly these upstream cases run sequentially in the actual Dotty GUI; the other 563 remain in the headless conformance-host lane:

- `ManipulateSelectionDataTests.test_ManipulateSelectionData_default`
- `XtermWinopsTests.test_XtermWinops_IconifyDeiconfiy`
- `XtermWinopsTests.test_XtermWinops_MoveToXY`
- `XtermWinopsTests.test_XtermWinops_MoveToXY_Defaults`

The pinned upstream test names, assertions, and known-bug annotations are not modified or reclassified by the runner.

The runner launches the product apphost with the original esctest process as its command inside Dotty's real PTY. It uses an isolated Xvfb display and Openbox window manager, sets `WAYLAND_DISPLAY` empty and `XDG_SESSION_TYPE=x11`, and keeps the original esctest stdin/stdout/stderr attached to that PTY. Before releasing the child, it waits for Dotty's control port and configured 80 x 25 grid, then verifies the native window is visible and focused through the isolated X11 `DISPLAY` and `XAUTHORITY`. It reads outcomes from esctest's original logfile and child status; each case's host diagnostics, result log, Openbox log, and JSON metadata are under `artifacts/esctest/logs/gui/`.

Each GUI case has a temporary `HOME`, `DOTNET_CLI_HOME`, `XDG_CONFIG_HOME`, and `DOTTY_CONFIG_HOME`; no user configuration is reused. The fixture explicitly enables only the required test permissions and reports (`windowControlAllowed`, `windowResizeAllowed`, OSC 52 read/write, checksum/title reports); selection behavior remains native X11. Missing X11 tools or the Dotty apphost is a setup error, not a fallback to the headless lane.

## The host and its knobs

The host exits with the child's exit code; `2` is a usage error, `124` a timeout (`--timeout-seconds`, 1 to 86400, default 900), `125` a host error (PTY unsupported, `pty-helper` missing, session start failure), and `130` an interrupt.

| Option | Environment variable | Effect |
|---|---|---|
| `--rows N`, `--columns N` | | Initial grid (default 25 x 80; esctest's `reset()` also forces it). |
| `--checksum-reports` | `DOTTY_CONFORMANCE_CHECKSUM=1` (`1`, `true`, `yes`, `on`) | Sets `TerminalAdapter.ChecksumReportEnabled`, the same switch as `vt.checksumReportEnabled`. Without it DECRQCRA gets no reply, as in the product default. |
| `--window-resize` | `DOTTY_CONFORMANCE_RESIZE=1` | Honours `CSI 8 ; rows ; cols t`, `CSI Ps t` (Ps >= 24), and DECCOLM requests permitted by mode 40, by resizing the session grid and PTY synchronously on the parser thread. A following `CSI 18 t` in the same stream sees the accepted grid. The flag does not itself enable mode 40. Without it geometry requests are ignored, like a product window with `vt.windowResizeAllowed` off. The GUI applies resizes asynchronously and only for a single-pane, single-tab, normal window. |
| `--title-reports` | `DOTTY_CONFORMANCE_TITLE_REPORTS=1` | Sets `TerminalAdapter.TitleReportEnabled`, matching `vt.titleReportEnabled`. Permits byte-accurate icon/window title reports (`CSI 20/21 t`) independently of resizing and checksums. Without it both queries are silent; title updates still work. |
| `--transcript FILE` | | Byte transcript, one line per chunk: elapsed milliseconds, `C` (child output) or `R` (terminal reply), the text with ESC shown as `<ESC>`. |
| `--working-directory DIR`, `--env KEY=VALUE` | | Child working directory and extra environment. |

The cell size is pinned to 8 x 16 pixels, and the text-area size is derived from the live grid, so `CSI 14 t` and `CSI 18 t` stay consistent. The conformance run exercises exactly the adapter behavior described in [Parsing](Parsing.md); it cannot see GUI-only code such as `VtHostPolicy` gating.

The headless theme is explicitly black on white (`#000000` / `#FFFFFF`). This fixture-only baseline was approved during G11: upstream [`reset()`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/esctest.py#L87-L89) installs black/white dynamic overrides, and `ResetSpecialColorTests.test_ResetSpecialColor_Dynamic` assumes OSC 110 returns to that same foreground. OSC 110/111 correctly clear overrides to the configured theme, not to a previous override. The suite assertions are unchanged, and real Dotty windows continue using their configured themes.

## Expectations and the baseline marker

`tests/esctest/expected-failures.txt` has one directive per line, with exact `Class.test_name` (no wildcards) and an optional `# note`:

```text
# baseline-captured: <40-hex commit of esctest2>
xfail DECSTRTests.test_DECSTR_Foo  # why it fails
skip  SomeTests.test_hangs         # quarantine: not executed
```

- `xfail`: the test must fail. Passing it is an error (an *unexpected pass*): delete the line when a fix lands.
- `skip`: the test is excluded from the run (hang or crash quarantine).
- `# baseline-captured: <commit>`: written only by `--update-expectations`; its presence arms the gate. The file as committed has no marker and no entries.

The comparison buckets results as regression (fails, not listed), expected failure, unexpected pass, stale entry (an `xfail` name not seen in a full run, or a `skip` for an unknown class), and not-run.

## Gating

| State | Behavior |
|---|---|
| No marker (today) | The run executes and reports everything, but a gate failure exits `0` and the output says `NOT GATING`. In CI the build, run, and setup steps are `continue-on-error`, so the job stays green; read the step summary and the `esctest-results` artifact. A harness fault (exit `3`) is still reported as such. |
| Marker present | Exit `1` on a regression, an unexpected pass, a stale entry, or a not-run test; exit `3` on a harness fault (an incomplete class, failed `reset()`, a canary class `CUUTests` with no passing test, or fewer passes than `minPassed`); steps no longer `continue-on-error`. `ci-summary` fails when the `esctest-linux` job fails. |
| `--gate on\|off` | Overrides the marker for local runs. `--allow-unexpected-pass` (alias `--no-strict`) stops unexpected passes from failing the run. |

Exit codes of the runner: `0` ok, `1` gate failure, `2` usage or setup error, `3` harness fault.

## Capturing the baseline

1. Dispatch the **esctest Baseline** workflow (`workflow_dispatch`) on the branch. It runs on `ubuntu-latest` only; inputs are `commit` (default `false`) and `quarantine_not_run` (add `skip` entries for tests that hung or crashed).
2. The workflow installs the Xvfb/Openbox and native build prerequisites, builds `pty-helper`, the conformance host, and the Dotty GUI apphost, then runs `run_esctest.py --update-expectations --jobs 2` across the complete 567-case suite. It always uploads `tests/esctest/expected-failures.txt` as the `esctest-expectations` artifact and the full results as `esctest-baseline-results`; with `commit: true`, it also commits the file back to the branch.
3. The intended flow is to download the artifact and commit it in a reviewed pull request, because expectations should shrink through review. Also set `minPassed` in `esctest.json` to the value the run prints (`suggested minPassed`).
4. `--update-expectations` needs a full run (no `--only-class` or `--include`), refuses to write from a run with harness faults, and refuses when tests hung or crashed unless `--quarantine-not-run` is given. Every `xfail` it writes gets the note `unclassified`; replace it with a reason as entries are triaged. The `AREAS` table in `esctest_results.py` maps classes to the owning area in the reports.

## Running locally

```bash
sudo apt-get install -y xvfb openbox xauth x11-utils xdotool cmake pkg-config xorg-dev libwayland-dev wayland-protocols libxkbcommon-dev
make -C src/Dotty.NativePty
dotnet restore tests/Dotty.Conformance.Host/Dotty.Conformance.Host.csproj
dotnet build tests/Dotty.Conformance.Host/Dotty.Conformance.Host.csproj -c Release --no-restore
dotnet restore src/Dotty/Dotty.csproj
dotnet build src/Dotty/Dotty.csproj -c Release --no-restore
python3 scripts/esctest/run_esctest.py --jobs 2 --out artifacts/esctest
python3 scripts/esctest/run_esctest.py --only-class CUUTests --gate off --out artifacts/esctest
python3 -m unittest discover -s scripts/esctest/tests
```

The default invocation executes all 567 pinned cases, merging 563 headless outcomes with the four isolated GUI outcomes. `--only-class` and `--include REGEX` restrict both lanes consistently; the host path defaults to `tests/Dotty.Conformance.Host/bin/Release/net11.0/dotty-conformance-host` (or the `.dll` through `dotnet`), and `--host CMD...` overrides only the headless host.

## CI

`esctest-linux` in `.github/workflows/ci.yml` (Ubuntu, 30-minute job limit; 20-minute runner step) installs the GUI/native prerequisites, builds `pty-helper`, the conformance host, and Dotty apphost, runs all cases with `--jobs 2`, and uploads `artifacts/esctest/` as `esctest-results` (14 days). The manual `esctest Baseline` workflow is also Linux-only and uses the same Xvfb/Openbox and both executable builds, so it can capture the complete suite without generating skip annotations for unsupported GUI cases. The Python unit tests of the scripts run in the `code-quality` job. The job is part of `ci-summary`'s `needs`. No esctest or baseline capture lane runs on macOS or Windows.
