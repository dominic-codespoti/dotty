pty-helper - minimal native PTY launcher

Overview
--------
This small native helper allocates a PTY, forks, attaches the slave to the child
process's stdio (0/1/2), and proxies the master file descriptor to the helper's
stdin/stdout. It also accepts an optional unix-domain control socket (DOTTY_CONTROL_SOCKET)
that can be used to send resize JSON messages from the GUI, for example:

  {"type":"resize","cols":100,"rows":30}\n
Build
-----
Requires gcc on Linux/macOS.

From the repo root:

  cd src/Dotty.NativePty
  make

The built binary will be at `src/Dotty.NativePty/bin/pty-helper`.

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
Update `Dotty.App` to prefer launching this helper binary (in repo `bin` path) instead
of trying to call forkpty from managed code. The GUI should continue to proxy stdio
and connect to the control socket to send resize messages.
