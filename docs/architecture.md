# Architectural Overview

Dotty employs a strictly decoupled, domain-driven architecture designed for high performance and zero-allocation hot paths. The codebase is broken down into four distinct layers:

* **`Dotty.Abstractions`**: Shared contracts/interfaces (e.g., `ITerminalHandler`, `ITerminalParser`). This layer has zero dependencies.
* **`Dotty.Terminal`**: The headless core business logic. It manages the `TerminalBuffer` logic and internal scrollback structures. There is no UI or OS-specific code here.
* **`Dotty.NativePty`**: A low-level POSIX C wrapper (containing `pty-helper.c`) that orchestrates UNIX PTY pipes and forks outside of .NET's managed execution context.
* **`Dotty.App`**: The frontend User Interface built with Avalonia. This maps the `Dotty.Terminal` logic to the physical display via specialized controls (like `TerminalCanvas`).
