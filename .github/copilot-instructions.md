<!-- .github/copilot-instructions.md for dotnet-term -->
# Quick guide for AI coding agents working on Dotty (dotnet-term)

Purpose: give a compact, actionable orientation so an AI can be productive quickly.

1) Big picture (what touches what)
- Dotty.App (UI): Avalonia GUI, runs the terminal renderer and user input handling. See `src/Dotty.App/` (e.g. `Program.cs`, `TerminalAdapter.cs`, `MainWindow.axaml.cs`).
- Dotty.Core: PTY abstractions and implementations. Key interfaces: `IPseudoTerminal`, `IPseudoTerminalFactory` in `src/Dotty.Core/PseudoTerminal.cs`.
- Dotty.NativePty: small native helper `pty-helper` (C) used to allocate PTYs and proxy master FD over stdio. Build with `make` in `src/Dotty.NativePty/` and binary appears at `src/Dotty.NativePty/bin/pty-helper`.
- Dotty.Terminal: minimal ANSI/VT parser and terminal buffer logic. See `src/Dotty.Terminal/BasicAnsiParser.cs` and `TerminalBuffer.cs`.

Data flow summary:
- UI spawns a PTY (managed `IPseudoTerminal` or external `pty-helper`).
- PTY exposes Input/Output streams. Output -> `BasicAnsiParser.Feed(...)` -> `ITerminalHandler` callbacks (e.g. `TerminalAdapter`) -> `TerminalBuffer` -> UI render via `RenderRequested`.
- Resize/control messages may be sent over a Unix domain control socket (env `DOTTY_CONTROL_SOCKET`) in JSON form, e.g. `{"type":"resize","cols":100,"rows":30}\n` (see `src/Dotty.NativePty/README.md`).

2) Build / run / test (project-specific commands)
- Build all: `dotnet build` (repo root).
- Run GUI: `dotnet run --project src/Dotty.App/Dotty.App.csproj` (or open in IDE and run Dotty.App target).
- Build native helper: `cd src/Dotty.NativePty && make` → produces `src/Dotty.NativePty/bin/pty-helper`.
- Run tests: `dotnet test` (note: some PTY functional tests are flaky under xUnit — consult `TESTING.md` and `PTY_STATUS.md`).

3) Project-specific conventions & gotchas
- Minimal ANSI handling: `BasicAnsiParser` intentionally implements a small subset (CSI J/K/H/m, BEL, OSC with BEL/ST). Many sequences are ignored on purpose. When modifying SGR/OSC behavior update both `BasicAnsiParser` and `TerminalAdapter.OnSetGraphicsRendition`.
- Rendering model: `TerminalAdapter` maintains a `TerminalBuffer` and raises `RenderRequested` with a snapshot string; renderers pull `TerminalAdapter.Buffer` for snapshots. Avoid making changes that bypass this contract.
- Debug flags: two env vars are used to enable noisy debugging:
  - `DOTTY_DEBUG_PARSER` (prints parser diagnostics to stderr)
  - `DOTTY_DEBUG_ADAPTER` (prints adapter prints)
- PTY implementation: prefer using `Dotty.NativePty/pty-helper` for portability; `Dotty.Core.UnixPty` contains managed P/Invoke code if you need to modify low-level termios/fork/exec behavior. Tests may avoid forkpty in certain CI contexts.

4) Integration points for common tasks
- To change how the GUI resizes the terminal: update code that sends resize JSON to the control socket (see `Dotty.NativePty/README.md`) and ensure `IPseudoTerminal.Resize` is implemented in `UnixPty`/factory.
- To add richer ANSI support: expand `BasicAnsiParser.HandleCsi` and add corresponding handling in `ITerminalHandler` implementations (e.g., `TerminalAdapter`).
- To change how output is rendered (e.g., keep OSC titles): modify `TerminalAdapter.OnOperatingSystemCommand`/`OnPrint` and `TerminalBuffer` snapshotting.

5) Files you will commonly edit (quick map)
- UI / entry: `src/Dotty.App/Program.cs`, `src/Dotty.App/MainWindow.axaml.cs`, `src/Dotty.App/TerminalAdapter.cs`
- Parser/handler: `src/Dotty.Terminal/BasicAnsiParser.cs`, `src/Dotty.Terminal/TerminalBuffer.cs`
- PTY core: `src/Dotty.Core/PseudoTerminal.cs`, `src/Dotty.Core/UnixPty*.cs`
- Native helper: `src/Dotty.NativePty/pty-helper.c`, `src/Dotty.NativePty/README.md`

6) Debugging and verification tips
- Reproduce UI behavior locally by running the GUI and connecting it to `pty-helper` manually: `src/Dotty.NativePty/bin/pty-helper /bin/bash`.
- Use `DOTTY_DEBUG_PARSER=1` or `DOTTY_DEBUG_ADAPTER=1` to see diagnostic output on stderr.
- When tests fail involving PTYs, run the same scenario manually using the helper binary to separate xUnit issues from PTY behavior.

7) Merge guidance for existing agent docs
- No repo-level `.github/copilot-instructions.md` or AGENT.md found — this file is being added. If you update, keep the short sections above and add only repo-specific facts (commands, file paths, env vars). Avoid aspirational guidance; document only behavior you can confirm from code or README.

References: `README.md`, `src/Dotty.NativePty/README.md`, `src/Dotty.Core/PseudoTerminal.cs`, `src/Dotty.Terminal/BasicAnsiParser.cs`, `src/Dotty.App/TerminalAdapter.cs`.

If any part is unclear or you want extra examples (e.g., common refactors, test harness snippets, or more integration details), tell me which area and I'll expand the instructions.
