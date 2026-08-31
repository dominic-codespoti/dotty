# Testing

Dotty separates deterministic terminal-core tests from native PTY and desktop
smoke tests. The desktop executable is `src/Dotty/Dotty.csproj`; there is no
separate `Dotty.App` host.

## Test projects

| Project | Scope |
|---|---|
| `tests/Dotty.App.Tests` | parser, input, runtime, rendering, configuration, and host seams |
| `tests/Dotty.Terminal.Tests` | terminal buffer, parser, hyperlinks, and reflow |
| `tests/Dotty.NativePty.Tests` | `IPty` contracts, platform capabilities, Unix helper, and Windows ConPTY |
| `tests/Dotty.App.SkiaTests` | Skia/OpenGL-adjacent rendering behavior |
| `tests/Dotty.Config.SourceGenerator.Tests` | platform-neutral source-generator compatibility |

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
smoke. Linux Wayland coverage uses a real compositor such as Weston; Xvfb only
proves X11 startup.

The optional control transport is loopback-only and enabled with
`DOTTY_TEST_PORT`. The cross-platform harness is
`.opencode/skills/terminal-tester/dotty-interact.sh`; it scopes state/PID files,
uses Python sockets instead of assuming `nc`, and never kills unrelated
processes. See [GUI harness benchmarking](GuiHarnessBenchmarking.md).

## CI matrix

The authoritative workflow is `.github/workflows/ci.yml`:

| Runner | RID | Native work | Desktop work |
|---|---|---|---|
| Ubuntu | `linux-x64` | POSIX helper and native PTY smoke | X11/Xvfb and Weston smoke |
| macOS Intel | `osx-x64` | POSIX helper and native PTY smoke | native desktop smoke |
| macOS arm64 | `osx-arm64` | POSIX helper and native PTY smoke | native desktop smoke |
| Windows | `win-x64` | ConPTY native PTY smoke | native desktop smoke |

Each matrix entry restores and publishes the host RID, verifies executable and
native asset names, builds/tests the host backend, and uploads TRX results.
Nightly additionally validates Linux arm64 and Windows arm64 publish outputs.

## Release verification

Release jobs must:

1. build Unix `pty-helper` and require it in Unix publish output;
2. verify `dotty`/`dotty.exe` and RID-specific Skia/Lua native libraries;
3. inspect every tar/zip archive before release creation;
4. extract and start the Linux archive under Xvfb;
5. generate `MANIFEST.txt` and `SHA256SUMS`.

See [Platform Support](PlatformSupport.md) for promotion gates and
[End-to-end Testing](E2ETesting.md) for the command-level smoke contract.
