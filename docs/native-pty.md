# Native PTY Integration

Located in `src/Dotty.NativePty/pty-helper.c` and managed by `TerminalSession.cs`:

* **Process Isolation**: Instead of direct P/Invoking standard `forkpty()` (which can easily crash a multithreaded managed .NET application), Dotty deploys a standalone C proxy process (`pty-helper`).
* **Standard I/O Bridge**: The .NET `TerminalSession` kicks off the child process, feeding terminal input through `StandardInput` and parsing ANSI sequences back from `StandardOutput`.
* **Socket-Based Resizing**: Uses an inter-process UNIX Socket (`DOTTY_CONTROL_SOCKET`) bound uniquely to `pty-helper` to safely send out-of-band JSON resize commands (like `{"type":"resize","cols":80,"rows":24}`), executing system `ioctl(master_fd, TIOCSWINSZ, ...)` calls natively.
* **Platform Specifics**: Currently relies entirely on UNIX paradigms (Linux/macOS) due to its dependency strictly on standard POSIX sockets and `forkpty`. Windows compatibility (`ConPty`) is not structurally implemented yet.
