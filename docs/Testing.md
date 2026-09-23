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

`tests/Dotty.App.Tests` sets `DOTTY_SHELL` to a silent, long-lived program in a
module initializer (`TestShellEnvironment.cs`). Tabs created by tests therefore
never receive prompt, title, or terminal-mode output from the developer's shell.
Tests that need a real shell must pass one explicitly.

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
Wayland/Weston smoke. Nightly additionally validates Linux arm64 and Windows
arm64 publish outputs.

## Release verification

Release jobs must:

1. build Unix `pty-helper` and require it in Unix publish output;
2. verify `dotty`/`dotty.exe` and RID-specific Skia/Lua native libraries;
3. inspect every tar/zip archive before release creation;
4. extract and start the Linux archive under Xvfb;
5. generate `MANIFEST.txt` and `SHA256SUMS`.

See [Platform Support](PlatformSupport.md) for promotion gates and
[End-to-end Testing](E2ETesting.md) for the command-level smoke contract.
