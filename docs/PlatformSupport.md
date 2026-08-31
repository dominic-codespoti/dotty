# Platform Support

Dotty is a native desktop terminal host backed by a platform-neutral terminal
core. The desktop host uses Silk.NET, GLFW, OpenGL, and SkiaSharp. PTY startup
is selected by `PtyFactory` and is reported by `PtyCapabilities`.

## Support matrix

| Platform | Release target | Build-only target | PTY backend | Desktop requirement |
|---|---|---|---|---|
| Linux | x64 | arm64 | POSIX `pty-helper` | X11 or Wayland desktop with OpenGL 3.3 |
| macOS | x64, arm64 | — | POSIX `pty-helper` | macOS desktop with OpenGL 3.3 |
| Windows 10 build 17763+ / Windows 11 | x64 | arm64 | ConPTY | OpenGL 3.3 driver |

Linux arm64 and Windows arm64 are built nightly as promotion candidates. They
are not release targets until native PTY and desktop smoke runs complete on
native arm64 hosts.

## Requirements

All platforms require the .NET 10 SDK for source builds and an OpenGL 3.3 core
capable driver for the desktop host. Release artifacts are self-contained.

Linux and macOS source builds also require a C compiler and `make` for the POSIX
helper. Windows uses the operating system ConPTY API and does not need a helper
compiler. Windows ConPTY requires build 17763 or newer.

## Build and run

From the repository root:

```bash
# Linux or macOS
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release
dotnet run --project src/Dotty/Dotty.csproj
```

```powershell
# Windows
dotnet build Dotty.slnx -c Release
dotnet run --project src\Dotty\Dotty.csproj
```

Run the host-native tests on the machine that will run the host:

```bash
dotnet test --project tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj -c Release
```

The CI matrix builds Linux x64, macOS Intel, macOS arm64, and Windows x64 on
native runners. Nightly builds additionally validate Linux arm64 and Windows
arm64 publish outputs.

## Configuration and user data

The configuration file is JSON and is watched for atomic changes:

- Linux: `$XDG_CONFIG_HOME/dotty/config.json`, or `~/.config/dotty/config.json`
- macOS: `~/Library/Application Support/Dotty/config.json`
- Windows: `%APPDATA%/Dotty/config.json`

Set `DOTTY_CONFIG_HOME` to override the complete configuration directory. Theme
files are in `<config-directory>/themes`; Lua startup scripts (`config.lua` or
`init.lua`) are in the configuration directory. The watcher debounces writes,
rename/write patterns, and dispatches accepted changes to the desktop UI thread.

## Diagnostics

`PtyFactory.GetCapabilities()` reports:

- derived runtime identifier and process architecture;
- selected backend (`UnixHelper` or `ConPty`);
- native dependency availability;
- an actionable diagnostic when the platform cannot start a PTY.

For a desktop startup failure, Dotty reports the required OpenGL version and the
initialization exception before closing the window so the process does not leave
an orphaned PTY. Fonts are resolved from the configured comma-separated stack;
missing families fall back to the platform default while cell metrics clamp
non-finite or non-positive values to safe dimensions.

## Troubleshooting

### `pty-helper` is missing or not executable

Build it beside the repository source and verify its mode:

```bash
make -C src/Dotty.NativePty
test -x src/Dotty.NativePty/bin/pty-helper
```

Published Unix artifacts must contain an executable `pty-helper` beside
`dotty`. The runtime first checks the published directory, then repository
build locations, then each directory in `PATH` using the host path separator.

### PTY reports an unsupported platform or architecture

Run the native test project and inspect the capability diagnostic. Supported
runtime identifiers are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`,
`win-x64`, and `win-arm64`; only the release targets in the matrix above are
promoted for distribution.

### ConPTY is unavailable

Use Windows 10 build 17763 or newer, or Windows 11. Confirm the Windows SDK is
installed and run the Windows native PTY tests without excluding
`WindowsPtyTests`.

### OpenGL startup fails

Dotty requests an OpenGL 3.3 core context. Update the graphics driver, confirm
the process has access to the desktop display, and retry under the platform's
native session. Linux CI uses Xvfb for X11 smoke and Weston for a headless
Wayland smoke; Xvfb does not validate a Wayland path.

### The configured font is not found

Provide a comma-separated fallback stack, for example
`JetBrains Mono, Cascadia Code, Liberation Mono, monospace`. Dotty keeps the
terminal grid usable when a requested family is absent and clamps invalid font
size, line-height, and scale values.

### Configuration changes are not applied

Check `DOTTY_CONFIG_HOME` and confirm that `config.json` is replaced atomically
rather than written into another directory. Invalid JSON preserves the last
valid configuration; the host reports the parse error through its diagnostics.

## Desktop smoke contract

The optional loopback control interface is enabled with `DOTTY_TEST_PORT` and
is used only by local/CI smoke harnesses. It accepts one newline-delimited
command per connection: `TYPE`, `KEY`, `RESIZE`, `DUMP`, `GET_STATE`, `STATS`,
`WAIT_FOR_IDLE`, and `SHUTDOWN`. It binds to `127.0.0.1` only.

The checked-in harness is:

```bash
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh launch
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh state
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh close
```

The harness uses Python sockets instead of assuming `nc`, propagates build and
host failures, scopes its PID/state files, and only terminates the PID it
started. X11 smoke runs under `xvfb-run`; Wayland smoke uses Weston.

## Promotion gates

Support promotion is staged:

1. **Release tier:** Linux x64, macOS x64/arm64, and Windows x64 pass native
   PTY tests, host desktop smoke, extracted artifact smoke, and checksum/
   manifest generation.
2. **Architecture candidate tier:** Linux arm64 and Windows arm64 pass native
   publish validation first. Promotion additionally requires native arm64 PTY,
   desktop, and extracted artifact smoke on two consecutive nightly runs.
3. **Regression hold:** Any failed native matrix, missing native asset, OpenGL
   startup failure, orphan process, or checksum mismatch blocks promotion until
   the failing scenario is reproduced and rerun successfully.

## Release checklist

- [ ] Restore and build `Dotty.slnx` on every release runner.
- [ ] Build and package `pty-helper` for Unix artifacts.
- [ ] Run native PTY tests without filtering the host backend.
- [ ] Verify `dotty`/`dotty.exe`, native Skia/Lua assets, and helper placement.
- [ ] Smoke X11 and Wayland Linux startup.
- [ ] Smoke macOS and Windows desktop startup on native runners.
- [ ] Extract every archive into a clean directory and run the host.
- [ ] Generate `MANIFEST.txt` and `SHA256SUMS`; verify checksums.
- [ ] Record unsupported architecture candidates separately from release assets.
