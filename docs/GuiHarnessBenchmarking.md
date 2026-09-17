# GUI Harness Benchmarking

The desktop host is `src/Dotty/Dotty.csproj`. The optional loopback control
interface is enabled with `DOTTY_TEST_PORT`; it binds to `127.0.0.1` and is
intended for local or CI smoke runs, not production IPC.

## Control protocol

Each TCP connection sends one newline-delimited command and receives one
newline-terminated response. Supported commands are:

- `TYPE:<utf8 text>`
- `KEY:<key name>`
- `RESIZE:<columns>:<rows>`
- `DUMP`
- `GET_STATE`
- `STATS`
- `WAIT_FOR_IDLE`
- `SHUTDOWN`

The transport is implemented by `src/Dotty/Host/DesktopControlServer.cs`; UI
state changes are queued and executed on the window thread by
`DottyWindowHost`.

## Standard smoke harness

```bash
DOTTY_TEST_STATE_DIR=/tmp/dotty-harness \
  .opencode/skills/terminal-tester/dotty-interact.sh launch
DOTTY_TEST_STATE_DIR=/tmp/dotty-harness \
  .opencode/skills/terminal-tester/dotty-interact.sh type 'printf "hello\\n"'
DOTTY_TEST_STATE_DIR=/tmp/dotty-harness \
  .opencode/skills/terminal-tester/dotty-interact.sh key Enter
DOTTY_TEST_STATE_DIR=/tmp/dotty-harness \
  .opencode/skills/terminal-tester/dotty-interact.sh dump
DOTTY_TEST_STATE_DIR=/tmp/dotty-harness \
  .opencode/skills/terminal-tester/dotty-interact.sh close
```

The harness builds the current host, creates a private state directory, uses
Python sockets instead of assuming `nc`, waits for the control port, propagates
build/transport failures, and only terminates the PID recorded for that run.
Use a unique `DOTTY_TEST_STATE_DIR` for concurrent runs.

## Benchmark boundaries

Benchmark terminal-core operations directly in the relevant test or performance
project. Do not use a GUI process to infer parser or PTY throughput. GUI runs
measure startup, control round-trip latency, frame cadence, resize behavior,
and shutdown/orphan-process behavior only.

For display coverage, CI runs only Linux X11 under `xvfb-run`. Wayland/Weston,
macOS desktop, and Windows desktop runs require a local native session; they
are not part of CI because the hosted environments do not provide usable GUI
OpenGL contexts. X11/Xvfb startup does not prove Wayland, macOS, or Windows GUI
behavior.

A timeout while the host remains alive is expected. A crash, early exit, failed
control response, missing native asset, or process that survives `close` is a
failed benchmark/smoke result.
