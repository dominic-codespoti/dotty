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

Test projects intentionally do not set a fixed `RuntimeIdentifier`. Native
assets and conditional PTY tests must resolve for the runner that executes the
tests.

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
| `gui-smoke-linux` | Ubuntu `linux-x64` | X11 desktop startup under Xvfb |
| `code-quality` | Ubuntu | `dotnet format whitespace` verification |
| `performance-tests` | Ubuntu | Quick BenchmarkDotNet run on pull requests and `main` |

The hosted macOS and Windows runners build and test native code but do not run
desktop GUI smoke: they lack a usable OpenGL/GUI session. CI has no
Wayland/Weston smoke. Linux arm64 and Windows arm64 remain local/manual
candidates without an automated nightly or release-publishing lane; see the
[Release Policy](Releasing.md).

## Release verification

Release jobs must:

1. build Unix `pty-helper` and require it in Unix publish output;
2. verify `dotty`/`dotty.exe` and RID-specific Skia/Lua native libraries;
3. inspect every tar/zip archive before release creation;
4. extract and start the Linux archive under Xvfb;
5. generate `MANIFEST.txt` and `SHA256SUMS`.

See [Platform Support](PlatformSupport.md) for promotion gates and
[End-to-end Testing](E2ETesting.md) for the command-level smoke contract.
