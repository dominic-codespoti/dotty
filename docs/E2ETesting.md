# End-to-End Testing Guide

Dotty has two layers of compatibility tests:

1. Headless terminal-core tests for parser, input, buffer, resize, and protocol behavior.
2. Native host smoke tests for PTY startup, rendering, input, resize, focus, paste,
   and shutdown.

The executable host is `src/Dotty/Dotty.csproj`. There is no separate
`Dotty.E2E.Tests` project.

## Headless contract tests

Run the full solution tests from the repository root:

```bash
dotnet test --solution Dotty.slnx -c Release --nologo
```

The following tests are platform-neutral and should run on every supported OS:

- `Dotty.App.Tests.ParserEdgeCaseTests`
- `Dotty.App.Tests.P0TerminalCompatibilityTests`
- `Dotty.App.Tests.TerminalInputEncoderTests`
- `Dotty.App.Tests.TerminalKeyboardDispatcherTests`
- `Dotty.App.Tests.TerminalBufferCursorTests`
- `Dotty.Terminal.Tests.ReflowResizeTests`

Native PTY tests must run on the host platform:

```bash
dotnet test --project tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj \
  -c Release --nologo
```

Windows CI must execute `WindowsPtyTests`; Unix CI must build `pty-helper`
before executing `UnixPtyTests`.

## Host control interface

When `DOTTY_TEST_PORT` is set, the host exposes the TCP control interface used
by the developer smoke harness. Commands are newline-delimited:

| Command | Purpose |
|---|---|
| `TYPE:<text>` | Send text input |
| `KEY:<name>` | Send a special key |
| `RESIZE:<columns>:<rows>` | Resize the active terminal |
| `DUMP` | Return visible terminal text |
| `GET_STATE` | Return dimensions, cursor, and scrollback |
| `WAIT_FOR_IDLE` | Wait for pending host work |
| `STATS` | Return tab/session statistics |
| `SHUTDOWN` | Close the host cleanly |

The protocol is transport-only. Assertions about parser modes, exact PTY bytes,
focus reports, paste wrappers, and reflow remain in deterministic tests.

## Linux smoke

Linux GUI smoke requires an X11 display server. Xvfb is the CI setup:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release --nologo
mkdir -p /tmp/dotty-empty-home
HOME=/tmp/dotty-empty-home timeout 5s \
  xvfb-run -a dotnet run --project src/Dotty/Dotty.csproj --no-build
```

Exit status `124` is expected because the host remains running. Any earlier
exit, native loader error, PTY startup error, or unhandled exception fails the
smoke test. CI runs this X11 smoke in `gui-smoke-linux`; Wayland/Weston,
macOS desktop, and Windows desktop smoke are not run in CI. X11/Xvfb startup
does not prove Wayland, macOS, or Windows GUI behavior.

## macOS smoke (manual/local)

Run this workflow on a local native macOS session for Intel or Apple Silicon;
hosted CI runners build and test the native code but do not provide a usable
desktop OpenGL session:

```bash
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release --nologo
dotnet test --solution Dotty.slnx -c Release --nologo
DOTTY_TEST_PORT=19000 dotnet run --project src/Dotty/Dotty.csproj
```

Exercise shell startup, UTF-8 text, resize, clipboard, focus changes, and clean
window shutdown. Retina scale changes must be included in GUI runs.

## Windows smoke (manual/local)

Run this workflow on Windows 10 build 17763+ or Windows 11. Hosted CI runs the
ConPTY tests and validates published assets, but does not run desktop GUI smoke:

```powershell
dotnet build Dotty.slnx -c Release --nologo
dotnet test --project tests\Dotty.NativePty.Tests\Dotty.NativePty.Tests.csproj `
  -c Release --filter "FullyQualifiedName~WindowsPtyTests"
$env:DOTTY_TEST_PORT = "19000"
dotnet run --project src\Dotty\Dotty.csproj -c Release
```

Exercise `cmd.exe`, Windows PowerShell, and `pwsh.exe`; verify ConPTY input,
output, resize, focus, clipboard, process exit, and shutdown.

## Artifact smoke

Release verification inspects every published archive and automatically
extracts and starts the Linux archive under Xvfb. Other platform archives are
run from clean directories on their native systems during manual/local
verification. Unix artifacts must contain an executable `pty-helper` beside the
host binary; Windows artifacts must contain the host binary and require no Unix
helper.

Minimum artifact checks:

1. Start the host.
2. Create a session and run a shell command.
3. Verify UTF-8 and VT output.
4. Resize the terminal.
5. Verify focus and paste paths.
6. Shut down without orphan processes.

## Failure handling

Smoke scripts must propagate build, test, and host exit codes. They must not
silently convert failures into success. Capture host logs and the extracted
artifact manifest for every failed matrix job.
