# VT conformance (esctest2)

Dotty is checked against the community conformance suite [esctest2](https://github.com/ThomasDickey/esctest2) on **Linux only**: esctest drives a program through a Unix tty and termios, so there is no Windows lane, and the macOS run of the baseline workflow is a comparison that is never gated. Everything here is automated CI coverage of the terminal core (parser, adapter, buffer, reply path); it says nothing about GUI, rendering, or platform behavior, which are verified manually (see [Testing](Testing.md) and [Platform Support](PlatformSupport.md)).

> **Status.** The harness was merged without a .NET SDK and without an esctest2 checkout, so it has not been run end to end yet. The runner's docstring lists the assumptions that the first real run must confirm (esctest2 layout and log format, the apphost path, the pinned commit). The gate is **off** until a baseline has been captured (see [Gating](#gating)).

## Components

| Path | Role |
|---|---|
| `tests/Dotty.Conformance.Host/` | `dotty-conformance-host`: runs a command on a real PTY whose terminal side is a headless Dotty `TerminalSession` (the production parser, adapter, and buffer; no window, no GL). It knows nothing about esctest. |
| `scripts/esctest/run_esctest.py` | The runner: fetches the pinned esctest2, starts one host process per test class, collects the logs, compares with the expectations, writes reports, and sets the exit code. |
| `scripts/esctest/esctest_results.py` | Pure parsing, normalization, comparison, and report rendering (standard library only). |
| `scripts/esctest/tests/` | Python unit tests of the two scripts (they need no .NET and no esctest2). |
| `tests/esctest/esctest.json` | Pinned esctest2 repository and commit, grid size (25 x 80), host and esctest arguments, per-class timeout, and `minPassed`. |
| `tests/esctest/expected-failures.txt` | The expected-failure and quarantine list, and the `# baseline-captured:` marker. |
| `.github/workflows/ci.yml` (`esctest-linux`) | The CI job. |
| `.github/workflows/esctest-baseline.yml` | Manual capture of the expectations file. |
| `tests/Dotty.App.Tests/ConformanceHostTests.cs` | .NET tests of the host (option parsing, core switches, a run against a real PTY). `Dotty.App.Tests` references the host project, and `Dotty.slnx` lists it. |

## How a run works

1. The runner reads `esctest.json` and makes sure `.esctest/esctest2` holds the pinned commit, fetching it with `git fetch --depth 1` on the first run (this needs network access; CI caches the directory by the hash of `esctest.json`). A checkout at a different commit is an error: delete the directory to refetch.
2. It discovers the test classes from `esctest/tests/__init__.py`.
3. For every class it starts one host process (`--jobs` at a time), so a hang or crash cannot take other classes down: `dotty-conformance-host --rows 25 --columns 80 --timeout-seconds 300 --checksum-reports --window-resize [--transcript ...] -- python esctest.py --logfile <log> --include '^Class\.' <esctestArgs>`. The esctest arguments select an xterm-compatible expectation set (`--expected-terminal=xterm --xterm-checksum=334 --max-vt-level=5`, `xtermWinopsEnabled`).
4. esctest has no machine-readable output and always exits 0, so the runner parses esctest's log file. Each test is `pass`, `fail`, `known-bug`, `skipped` (esctest decided the terminal lacks a capability), `not-run` (no outcome: hang or crash), or `harness-fault`. A class is *complete* only when its log has the summary line and the host did not exit with 124 (timeout), 125 (host error), or 130 (interrupted).
5. Failures are normalized: a failure with an `in reset` frame is a `harness-fault` (the host did not answer esctest's startup queries), and a `Should have failed` error on an xterm known-bug test counts as a pass.
6. Results are compared with the expectations (below), reports are written to `--out` (default `artifacts/esctest`): `results.json`, `summary.md` (also appended to the GitHub step summary), `merged.log`, per-class `logs/`, and `transcripts/` only for classes that failed, hung, or had faults (`--keep-transcripts` keeps all).

## The host and its knobs

The host exits with the child's exit code; `2` is a usage error, `124` a timeout (`--timeout-seconds`, 1 to 86400, default 900), `125` a host error (PTY unsupported, `pty-helper` missing, session start failure), and `130` an interrupt.

| Option | Environment variable | Effect |
|---|---|---|
| `--rows N`, `--columns N` | | Initial grid (default 25 x 80; esctest's `reset()` also forces it). |
| `--checksum-reports` | `DOTTY_CONFORMANCE_CHECKSUM=1` (`1`, `true`, `yes`, `on`) | Sets `TerminalAdapter.ChecksumReportEnabled`, the same switch as `vt.checksumReportEnabled`. Without it DECRQCRA gets no reply, as in the product default. |
| `--window-resize` | `DOTTY_CONFORMANCE_RESIZE=1` | Honours `CSI 8 ; rows ; cols t` and `CSI Ps t` (Ps >= 24) by resizing the session grid and the PTY window size synchronously on the parser thread, so a following `CSI 18 t` in the same stream sees the new grid. Without it requests are ignored, like a product window with `vt.windowResizeAllowed` off. This differs deliberately from the GUI host, which applies resizes asynchronously and only for a single-pane, single-tab, normal window. |
| `--transcript FILE` | | Byte transcript, one line per chunk: elapsed milliseconds, `C` (child output) or `R` (terminal reply), the text with ESC shown as `<ESC>`. |
| `--working-directory DIR`, `--env KEY=VALUE` | | Child working directory and extra environment. |

The cell size is pinned to 8 x 16 pixels, and the text-area size is derived from the live grid, so `CSI 14 t` and `CSI 18 t` stay consistent. The conformance run exercises exactly the adapter behavior described in [Parsing](Parsing.md); it cannot see GUI-only code such as `VtHostPolicy` gating.

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

1. Dispatch the **esctest Baseline** workflow (`workflow_dispatch`) on the branch. Inputs: `commit` (default `false`), `quarantine_not_run` (add `skip` entries for tests that hung or crashed), `os` (`ubuntu-latest`, or `macos-15` for comparison only; a macOS capture is never committed back).
2. The workflow builds `pty-helper` and the host, runs `run_esctest.py --update-expectations --jobs 2`, and always uploads `tests/esctest/expected-failures.txt` as the `esctest-expectations` artifact (and the full results as `esctest-baseline-results`). With `commit: true` on Ubuntu it also commits the file back to the branch.
3. The intended flow is to download the artifact and commit it in a reviewed pull request, because expectations should shrink through review. Also set `minPassed` in `esctest.json` to the value the run prints (`suggested minPassed`).
4. `--update-expectations` needs a full run (no `--only-class` or `--include`), refuses to write from a run with harness faults, and refuses when tests hung or crashed unless `--quarantine-not-run` is given. Every `xfail` it writes gets the note `unclassified`; replace it with a reason as entries are triaged. The `AREAS` table in `esctest_results.py` maps classes to the owning area in the reports.

## Running locally

```bash
make -C src/Dotty.NativePty
dotnet build tests/Dotty.Conformance.Host/Dotty.Conformance.Host.csproj -c Release
python3 scripts/esctest/run_esctest.py --list-classes
python3 scripts/esctest/run_esctest.py --only-class CUUTests --gate off --out artifacts/esctest
python3 -m unittest discover -s scripts/esctest/tests
```

The host path defaults to `tests/Dotty.Conformance.Host/bin/Release/net11.0/dotty-conformance-host` (or the `.dll` through `dotnet`); `--host CMD...` overrides it. `--include REGEX` narrows test names inside each class for debugging.

## CI

`esctest-linux` in `.github/workflows/ci.yml` (Ubuntu, 20-minute limit) builds `pty-helper` and the host, runs the runner with `--jobs 2`, and uploads `artifacts/esctest/` as `esctest-results` (14 days). The Python unit tests of the scripts run in the `code-quality` job. The job is part of `ci-summary`'s `needs`. Nothing here runs on macOS or Windows.
