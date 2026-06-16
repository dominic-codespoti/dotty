---
name: terminal-tester
description: Test the Dotty terminal emulator by running test suites, launching the app, sending keystrokes, and viewing the screen as ANSI-colored text
---

# Terminal Tester Skill for Dotty

This skill lets you test the Dotty terminal emulator interactively. You can run test suites, launch the app headlessly, type commands into it, and see the terminal screen as colored ANSI text — just like looking at a real terminal.

## Prerequisites

- .NET 10 SDK installed
- `make`, `gcc`/`clang` installed (for native PTY helper)
- For visual screenshots (optional): `xdotool`, `imagemagick`, `chafa`
- The skill scripts are in `.opencode/skills/terminal-tester/`
- All script commands are run from the project root unless noted

## Building

```bash
# Build the native PTY helper first
make -C src/Dotty.NativePty

# Build the .NET solution
dotnet build Dotty.slnx -c Debug
```

## Running Tests

Use the `dotty-test.sh` wrapper:

```
bash .opencode/skills/terminal-tester/dotty-test.sh --list
    List all test suites with their descriptions.

bash .opencode/skills/terminal-tester/dotty-test.sh --list --verbose
    List test suites with full test method names.

bash .opencode/skills/terminal-tester/dotty-test.sh --run
    Run all tests. Shows colored summary.

bash .opencode/skills/terminal-tester/dotty-test.sh --run "Category=Basic"
    Run tests matching a filter expression (xunit filter syntax).

bash .opencode/skills/terminal-tester/dotty-test.sh --run --project Dotty.Terminal.Tests
    Run tests from a specific project.

bash .opencode/skills/terminal-tester/dotty-test.sh --failed
    Re-run only the tests that failed in the last run.

bash .opencode/skills/terminal-tester/dotty-test.sh --help
    Full usage.
```

The test runner parses the TRX output and returns a structured summary: pass/fail counts, per-test failure messages, and stack traces.

## Launching and Interacting with the App

Use the `dotty-interact.sh` wrapper. This starts Dotty in headless mode (using Xvfb if available) and communicates with it over a TCP command interface.

```bash
export DOTTY_TERM_DIR=".opencode/skills/terminal-tester"

# 1. Launch the app headlessly
bash "$DOTTY_TERM_DIR/dotty-interact.sh" launch

# 2. Send keystrokes / text
bash "$DOTTY_TERM_DIR/dotty-interact.sh" type "ls -la"
bash "$DOTTY_TERM_DIR/dotty-interact.sh" type Enter

# 3. Dump the current screen content as ANSI-colored text
bash "$DOTTY_TERM_DIR/dotty-interact.sh" dump

# 4. Capture a visual screenshot (converted to ASCII art via chafa)
bash "$DOTTY_TERM_DIR/dotty-interact.sh" screenshot

# 5. Get terminal state
bash "$DOTTY_TERM_DIR/dotty-interact.sh" state

# 6. Send a raw TCP command
bash "$DOTTY_TERM_DIR/dotty-interact.sh" send "STATS"

# 7. Shut down the app
bash "$DOTTY_TERM_DIR/dotty-interact.sh" close
```

You can also chain commands:
```bash
bash "$DOTTY_TERM_DIR/dotty-interact.sh" launch
bash "$DOTTY_TERM_DIR/dotty-interact.sh" type "echo hello world"
bash "$DOTTY_TERM_DIR/dotty-interact.sh" type Enter
bash "$DOTTY_TERM_DIR/dotty-interact.sh" dump
bash "$DOTTY_TERM_DIR/dotty-interact.sh" close
```

## Understanding the Screen Dump

The `dump` command returns the terminal buffer rendered as ANSI escape sequences.
Each line of the output represents one row of the terminal, with:

- **Colors**: Foreground and background colors encoded as 24-bit ANSI SGR codes: `\e[38;2;R;G;Bm` (fg) and `\e[48;2;R;G;Bm` (bg)
- **Bold/Italic/Underline**: `\e[1m`, `\e[3m`, `\e[4m`
- **Cursor**: The cursor position is shown in the `CUR` header
- **Dimensions**: `R` = rows, `C` = columns

The ANSI text is readable directly since most terminals will interpret the escape sequences. If you're viewing this in a terminal that doesn't interpret the escapes, you'll see the raw codes. The important content (the actual text) is between the codes.

The output format:
```
DUMP OK
R=24 C=80 CUR=12,5
<ANSI-colored line 1>
<ANSI-colored line 2>
...
END
```

## Testing Workflows

### Workflow 1: Quick smoke test

```bash
# Build
make -C src/Dotty.NativePty && dotnet build Dotty.slnx -c Debug

# Run core tests
bash .opencode/skills/terminal-tester/dotty-test.sh --run "Category=Core"

# Launch and verify
bash .opencode/skills/terminal-tester/dotty-interact.sh launch
bash .opencode/skills/terminal-tester/dotty-interact.sh dump  # Should show a shell prompt
bash .opencode/skills/terminal-tester/dotty-interact.sh close
```

### Workflow 2: Test a specific feature (e.g., text rendering)

```bash
# Launch
bash .opencode/skills/terminal-tester/dotty-interact.sh launch

# Take a "before" snapshot
bash .opencode/skills/terminal-tester/dotty-interact.sh dump

# Type a command
bash .opencode/skills/terminal-tester/dotty-interact.sh type "echo Hello World"
bash .opencode/skills/terminal-tester/dotty-interact.sh type Enter

# Wait a moment for the shell to respond, then take an "after" snapshot
sleep 1
bash .opencode/skills/terminal-tester/dotty-interact.sh dump

# Verify "Hello World" appears in the output
# The ANSI text will show the command and its output

# Clean up
bash .opencode/skills/terminal-tester/dotty-interact.sh close
```

### Workflow 3: State transition testing

Capture a sequence of screen dumps to observe terminal behavior:

```bash
bash .opencode/skills/terminal-tester/dotty-interact.sh launch

# Initial state (shell prompt)
bash .opencode/skills/terminal-tester/dotty-interact.sh dump > /tmp/state0.txt

# After typing a command (line being edited)
bash .opencode/skills/terminal-tester/dotty-interact.sh type "ls /"
bash .opencode/skills/terminal-tester/dotty-interact.sh dump > /tmp/state1.txt

# After pressing Enter (command executed, output shown)
bash .opencode/skills/terminal-tester/dotty-interact.sh type Enter
sleep 1
bash .opencode/skills/terminal-tester/dotty-interact.sh dump > /tmp/state2.txt

# Compare states to see what changed
diff /tmp/state0.txt /tmp/state1.txt  # Shows cursor moved, text appeared
diff /tmp/state1.txt /tmp/state2.txt  # Shows output lines, new prompt

bash .opencode/skills/terminal-tester/dotty-interact.sh close
```

### Workflow 4: Investigate test failures

When a test fails:

```bash
# 1. Re-run failed tests
bash .opencode/skills/terminal-tester/dotty-test.sh --failed

# 2. Run a specific failing test with verbose output
bash .opencode/skills/terminal-tester/dotty-test.sh --run "FullyQualifiedName~SpecificTest" --verbose

# 3. Launch the app and manually verify the behavior
bash .opencode/skills/terminal-tester/dotty-interact.sh launch
# ... reproduce the issue ...
bash .opencode/skills/terminal-tester/dotty-interact.sh dump
bash .opencode/skills/terminal-tester/dotty-interact.sh close

# 4. Look at the test source code for the failing test to understand expectations
```

## TCP Command Interface (Reference)

The app listens on a TCP port (set via `DOTTY_TEST_PORT` env var). Available commands:

| Command | Description |
|---------|-------------|
| `TYPE:text` | Send text to the active terminal |
| `DUMP` | Dump screen as ANSI-colored text |
| `GET_STATE` | Get cursor position and terminal dimensions |
| `STATS` | Get application statistics (tabs, sessions) |
| `SCREENSHOT` | Get a screenshot (base64-encoded PNG) |
| `NEW_TAB` | Create a new tab |
| `CLOSE_TAB` | Close the active tab |
| `NEXT_TAB` / `PREV_TAB` | Switch tabs |
| `WAIT_FOR_IDLE` | Wait for rendering to complete |
| `RESIZE:cols:rows` | Resize terminal (e.g. `RESIZE:80:24`) |
| `SHUTDOWN` | Gracefully shut down the app |

## Troubleshooting

- **"dotnet: command not found"**: Ensure .NET 10 SDK is installed and in PATH
- **"make: command not found"**: Install make and gcc: `sudo apt install build-essential`
- **"Connection refused"**: The app may not have started yet. Increase sleep time or check logs.
- **Empty DUMP output**: Make sure the app started with `DOTTY_TEST_PORT` set and that the active tab has a running terminal session.
- **Screenshot shows blank**: Xvfb may not be available, or the app window hasn't rendered yet.
