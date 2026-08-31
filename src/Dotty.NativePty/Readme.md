pty-helper - POSIX PTY launcher used by Dotty on Linux and macOS
Overview
--------
This helper allocates a PTY, forks, attaches the slave to the child process's
stdio, and proxies the master file descriptor to stdin/stdout. It accepts an
optional Unix-domain control socket (`DOTTY_CONTROL_SOCKET`) for resize JSON:

  {"type":"resize","cols":100,"rows":30}\n
Build
-----
Requires gcc on Linux/macOS.

From the repo root:

  cd src/Dotty.NativePty
  make

The built binary will be at `bin/pty-helper`.

Usage
-----
The helper can be invoked directly. It accepts an optional first argument which is
an executable to run (with any trailing args). If no arg is provided it will exec
$DOTTY_SHELL or $SHELL with `-i`.

Environment variables:
- DOTTY_CONTROL_SOCKET - path to a unix-domain socket to accept resize/control messages
- DOTTY_SHELL - optional shell path

Example (basic):

  src/Dotty.NativePty/bin/pty-helper /bin/zsh

Example (used by GUI):

  DOTTY_CONTROL_SOCKET=/tmp/dotty-control.sock src/Dotty.NativePty/bin/pty-helper /bin/zsh

Integration
-----------
`Dotty.NativePty` selects `UnixPty` on Linux/macOS and `WindowsPty` through
ConPTY on Windows. The host resolves a packaged Unix helper beside the
application before checking development paths or `PATH`.

For diagnostics, launch the actual host project:
`dotnet run --project src/Dotty/Dotty.csproj`.
