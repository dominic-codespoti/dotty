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

Linux GUI smoke requires an X11 display server. Xvfb is the minimum CI setup:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release --nologo
mkdir -p /tmp/dotty-empty-home
HOME=/tmp/dotty-empty-home timeout 5s \
  xvfb-run -a dotnet run --project src/Dotty/Dotty.csproj --no-build
```

Exit status `124` is expected because the host remains running. Any earlier
exit, native loader error, PTY startup error, or unhandled exception fails the
smoke test.

Wayland smoke should use a real compositor such as Weston; Xvfb does not prove
Wayland behavior.

## macOS smoke

Run on native macOS runners for both Intel and Apple Silicon:

```bash
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release --nologo
dotnet test --solution Dotty.slnx -c Release --nologo
DOTTY_TEST_PORT=19000 dotnet run --project src/Dotty/Dotty.csproj
```

Exercise shell startup, UTF-8 text, resize, clipboard, focus changes, and clean
window shutdown. Retina scale changes must be included in GUI runs.

## Windows smoke

Run on Windows 10 build 17763+ and Windows 11:

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

Every published artifact must be extracted into a clean directory and tested
from there, not from the repository. Unix artifacts must contain an executable
`pty-helper` beside the host binary. Windows artifacts must contain the host
binary and require no Unix helper.

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
