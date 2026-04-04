# E2E Testing Guide

This guide covers the End-to-End (E2E) testing infrastructure for the Dotty terminal emulator.

## Overview

The E2E test suite provides comprehensive testing of the Dotty application by:
- Starting the actual application in a controlled environment
- Sending commands via a TCP-based interface
- Capturing screenshots on failures
- Verifying terminal state and behavior

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      E2E Test Suite                            │
├─────────────────────────────────────────────────────────────────┤
│  Test Classes                                                   │
│  ├── RenderingE2ETests                                          │
│  ├── InputE2ETests                                              │
│  ├── AnsiE2ETests                                               │
│  ├── WindowManagementE2ETests                                   │
│  ├── ConfigurationE2ETests                                      │
│  ├── PerformanceE2ETests                                        │
│  └── IntegrationE2ETests                                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              │ Uses
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Test Infrastructure                        │
├─────────────────────────────────────────────────────────────────┤
│  ├── TestApplicationHost (lifecycle management)               │
│  ├── TestCommandInterface (TCP commands)                        │
│  ├── HeadlessApplicationRunner (CI mode)                      │
│  ├── TestTimeoutManager (timeout handling)                      │
│  └── E2ETestBase (test base class)                              │
└─────────────────────────────────────────────────────────────────┘
                              │
                              │ Communicates via TCP
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Dotty Application                            │
├─────────────────────────────────────────────────────────────────┤
│  ├── MainWindow (handles test commands)                         │
│  ├── TerminalView (renders terminal)                            │
│  ├── TerminalSession (manages PTY)                              │
│  └── Command Listener (TCP port)                                │
└─────────────────────────────────────────────────────────────────┘
```

## Test Command Interface

The application exposes a TCP command interface when the `DOTTY_TEST_PORT` environment variable is set. Commands are sent as plain text and responses are returned as JSON.

### Supported Commands

| Command | Description | Response |
|---------|-------------|----------|
| `TYPE:<text>` | Send text to terminal | `OK` |
| `KEY:<key>` | Send key press | `OK` |
| `KEYCOMBO:<mods>+<key>` | Send key combo | `OK` |
| `RESIZE:<cols>:<rows>` | Resize terminal | `OK` |
| `SETCONFIG:<key>:<val>` | Set config value | `OK` |
| `GET_STATE` | Get terminal state | JSON state |
| `SCREENSHOT` | Capture screenshot | Binary PNG |
| `WAIT_FOR_IDLE` | Wait for render | `OK` |
| `INJECT_ANSI:<b64>` | Inject ANSI | `OK` |
| `SCROLL:<lines>` | Scroll buffer | `OK` |
| `COPY` | Copy selection | `OK` |
| `PASTE` | Paste clipboard | `OK` |
| `NEW_TAB` | Create tab | `OK` |
| `CLOSE_TAB` | Close tab | `OK` |
| `NEXT_TAB` | Next tab | `OK` |
| `PREV_TAB` | Previous tab | `OK` |
| `STATS` | Get statistics | JSON stats |

### Example Command Flow

```
Test -> App: "TYPE:hello"
App -> Test: "OK"

Test -> App: "STATS"
App -> Test: {"totalTabs":1, "sessionsStarted":1, ...}
```

## Running Tests

### Prerequisites

1. Build the application:
```bash
dotnet build src/Dotty.App/Dotty.App.csproj
```

2. Ensure test project builds:
```bash
dotnet build tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### Local Development

Run all E2E tests:
```bash
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

Run specific test class:
```bash
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~RenderingE2ETests"
```

Run specific test:
```bash
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~Basic_Rendering_Should_Display_Text"
```

Run in headless mode (no GUI):
```bash
DOTTY_E2E_HEADLESS=1 dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### CI Environment

The tests automatically detect CI environments and run in headless mode. Set these environment variables as needed:

```bash
export DOTTY_E2E_HEADLESS=1
export DOTTY_TEST_PORT=19000  # Optional: specify port
export DOTTY_BENCH_THROUGHPUT=1  # Optional: benchmark mode
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

## Writing Tests

### Basic Test Structure

```csharp
public class MyE2ETests : E2ETestBase
{
    public MyE2ETests() : base("MyTestCategory")
    {
    }

    [Fact]
    public async Task My_Test_Should_Do_Something()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            var initialState = await GetStatsAsync();
            
            // Act
            await SendTextAndWaitAsync("test input");
            
            // Assert
            var finalState = await GetStatsAsync();
            Assert.True(finalState.SessionsStarted > 0);
        });
    }
}
```

### Using Assertions

```csharp
// Screen assertions
ScreenBufferAssertions.ContainsText(lines, "expected text");
ScreenBufferAssertions.LineEquals(lines, 0, "first line");

// Rendering assertions
RenderingAssertions.DimensionsMatch(cols, rows, 80, 24);

// GUI assertions
GuiAssertions.TabCountEquals(stats.TotalTabs, 2);
```

### Adding Test Data

Place test data files in `TestData/`:

```
TestData/
├── ansi-samples/      # ANSI sequence samples
├── shell-scripts/     # Test shell scripts
├── configs/          # Test configuration files
└── expected-outputs/   # Expected output files
```

Access test data in tests:

```csharp
var content = await File.ReadAllTextAsync("TestData/shell-scripts/basic-test.sh");
```

## Test Categories

### RenderingE2ETests
Tests terminal rendering functionality including:
- Basic text rendering
- Color rendering (basic, 256, TrueColor)
- Font rendering and sizing
- Window resizing and reflow
- Scrollback buffer rendering
- Cursor rendering (block, line, bar)
- Selection highlighting

### InputE2ETests
Tests keyboard input handling:
- Alphanumeric keys
- Special characters
- Control keys (Ctrl, Alt, Shift)
- Function keys (F1-F12)
- Arrow keys
- Copy/paste operations

### AnsiE2ETests
Tests ANSI escape sequence handling:
- SGR codes (colors, styles)
- Cursor movement
- Erase sequences
- Scroll sequences
- Title changes
- Complex mixed sequences

### WindowManagementE2ETests
Tests window and tab management:
- Tab creation and closing
- Tab switching
- Window resizing
- Session preservation
- Tab titles

### ConfigurationE2ETests
Tests configuration and theming:
- Built-in themes
- Font configuration
- Color schemes
- Window transparency
- Scrollback size

### PerformanceE2ETests
Tests performance under load:
- High throughput rendering
- Large buffer handling
- Rapid configuration changes
- Multiple tab performance
- Startup time

### IntegrationE2ETests
Tests shell and tool integration:
- Shell startup
- Basic commands
- Environment variables
- Pipes and redirections
- Git integration

## Debugging Failed Tests

### Check Logs

Test logs are saved to `artifacts/logs/`:

```bash
cat artifacts/logs/Rendering_*.log
```

### View Screenshots

Screenshots are captured on failure and saved to `artifacts/screenshots/`:

```bash
ls artifacts/screenshots/*.png
```

### State Dumps

Terminal state is dumped to `artifacts/states/`:

```bash
cat artifacts/states/Input_*_state.txt
```

### Common Issues

1. **Application not starting**
   - Check build: `dotnet build`
   - Verify executable path in logs
   - Check port availability

2. **Timeout issues**
   - Increase timeout in `e2e.appsettings.json`
   - Check if app is responsive
   - Review logs for startup errors

3. **Headless mode failures**
   - Ensure `DOTTY_E2E_HEADLESS=1` is set
   - Check Avalonia headless dependencies
   - Verify X11 is not required

## CI Integration

### GitHub Actions

```yaml
name: E2E Tests
on: [push, pull_request]

jobs:
  e2e-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      
      - name: Build
        run: dotnet build
      
      - name: Run E2E Tests
        run: dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --verbosity normal
        env:
          DOTTY_E2E_HEADLESS: 1
        timeout-minutes: 15
      
      - name: Upload Artifacts
        uses: actions/upload-artifact@v3
        if: failure()
        with:
          name: test-artifacts
          path: tests/Dotty.E2E.Tests/artifacts/
```

## Best Practices

1. **Use RunTestAsync**: Always wrap test logic in `RunTestAsync` for proper error handling
2. **Add timeouts**: Use `TimeoutHelper` for operations that might hang
3. **Clean up**: Tests automatically clean up via `IAsyncLifetime`
4. **Screenshot on failure**: Enabled by default, review artifacts for failures
5. **Parallel execution**: Tests support parallel execution where safe

## Troubleshooting

### Port Conflicts

If the default test port is in use, tests will automatically find an available port. To specify a port:

```bash
DOTTY_TEST_PORT=19123 dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### Slow Tests

If tests are running slowly:
1. Run in headless mode: `DOTTY_E2E_HEADLESS=1`
2. Reduce screenshot capture frequency
3. Use parallel execution: `--parallel`

### Memory Issues

For memory-intensive tests:
1. Increase test timeout
2. Run tests sequentially: `--parallel none`
3. Monitor memory usage in logs
