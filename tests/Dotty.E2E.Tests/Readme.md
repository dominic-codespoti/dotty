# Dotty E2E Test Suite

Comprehensive end-to-end (E2E) GUI test suite for the Dotty terminal emulator.

## Overview

This test suite provides end-to-end testing of the Dotty terminal emulator by spinning up the actual application and testing it through a TCP-based test command interface.

## Features

- **Headless Mode Support**: Runs without display for CI environments
- **Test Isolation**: Each test gets a fresh app instance
- **State Inspection**: Can read terminal buffer, cursor position, config
- **Command Interface**: Send commands to control the app
- **Screenshot on Failure**: Captures visual state when tests fail
- **Timeout Handling**: Prevents hung tests
- **Parallel Test Support**: Run tests in parallel where possible
- **Performance Measurement**: Collects FPS, frame times, parser throughput, memory usage, GC metrics
- **Baseline Tracking**: Compares performance against historical baselines
- **Regression Detection**: Fails tests on performance regressions
- **Performance Reports**: HTML/JSON reports with charts and trends
- **CI/CD Integration**: Automatic artifact upload and trend analysis

## Project Structure

```
tests/Dotty.E2E.Tests/
├── Infrastructure/
│   ├── TestApplicationHost.cs              # Application lifecycle management
│   ├── TestCommandInterface.cs             # TCP command interface
│   ├── HeadlessApplicationRunner.cs        # Headless mode runner
│   ├── TestTimeoutManager.cs               # Timeout handling
│   ├── PerformanceCounterCollector.cs      # Performance metrics collection with accurate CPU/memory
│   ├── SystemPerformanceCollector.cs       # Cross-platform system metrics
│   ├── PerformanceBaselineManager.cs       # Baseline tracking
│   └── E2EPerformanceReport.cs             # Performance reporting
├── Assertions/
│   ├── ScreenBufferAssertions.cs           # Screen content assertions
│   ├── RenderingAssertions.cs              # Rendering state assertions
│   ├── ConfigurationAssertions.cs          # Configuration assertions
│   └── PerformanceAssertions.cs            # Performance threshold assertions
├── Scenarios/
│   ├── BasicFunctionalityE2ETests.cs       # Comprehensive basic functionality tests
│   ├── RenderingE2ETests.cs                # Rendering tests with performance
│   ├── InputE2ETests.cs                    # Input handling tests
│   ├── AnsiE2ETests.cs                     # ANSI sequence tests with throughput
│   ├── ColorsE2ETests.cs                   # Comprehensive color tests (8, 256, TrueColor)
│   ├── WindowManagementE2ETests.cs         # Tab/window tests with switching performance
│   ├── SearchE2ETests.cs                   # Search functionality tests
│   ├── LargeBufferE2ETests.cs              # 500k line buffer tests
│   ├── ConfigurationE2ETests.cs              # Configuration tests
│   ├── PerformanceE2ETests.cs              # Performance tests with metrics
│   └── IntegrationE2ETests.cs                # Shell integration tests
├── TestData/                                # Test fixtures and data
│   ├── Colors/                              # ANSI color test sequences
│   ├── LargeFiles/                          # 500k line test files
│   ├── Search/                              # Search test patterns
│   └── Stress/                              # Stress test scenarios
├── docs/
│   └── E2EPerformance.md                    # Performance testing documentation
├── e2e.appsettings.json                     # Test configuration with performance settings
├── E2EPerformanceTestBase.cs                # Base class for performance tests
├── E2ETestBase.cs                           # Base class for all E2E tests
└── xunit.runner.json                        # xUnit configuration
```

## Running the Tests

### Prerequisites

- .NET 10.0 SDK or later
- Dotty application built and available

### Run all tests

```bash
cd /home/dom/projects/dotnet-term
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### Run specific test class

```bash
# Basic functionality tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~BasicFunctionalityE2ETests"

# Color tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~ColorsE2ETests"

# Tab management tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~WindowManagementE2ETests"

# Search tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~SearchE2ETests"

# Large buffer tests (500k lines - may take 5-10 minutes)
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~LargeBufferE2ETests"

# Rendering tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~RenderingE2ETests"
```

### Run tests by category

```bash
# Run only color tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "Category=Colors"

# Run only large buffer stress tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "Category=LargeBuffer"

# Run only search tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "Category=Search"

# Run basic functionality tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "Category=Basic"
```

### Run specific tests

```bash
# Run a specific test method
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~Buffer_Creation_500k_Lines_Should_Succeed"

# Run all TrueColor tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~TrueColor"
```

### Run in headless mode

```bash
DOTTY_E2E_HEADLESS=1 dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### Run with increased verbosity

```bash
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --verbosity normal
```

## Performance Testing

The E2E test suite includes comprehensive performance measurement capabilities.

### Quick Start

```bash
# Run all tests with performance collection
DOTTY_E2E_HEADLESS=1 dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj

# Run only performance tests
dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj --filter "FullyQualifiedName~Performance"

# Run with performance regression detection
DOTTY_PERF_FAIL_ON_REGRESSION=1 dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
```

### Available Metrics

Tests automatically collect these performance metrics:

- **FPS**: Frames per second (current, min, max, average)
- **Frame Time**: Rendering time in ms (min, max, avg, p95, p99)
- **Parser Throughput**: Bytes/sec and sequences/sec processed
- **Memory**: Heap size, allocated bytes, working set
- **GC Collections**: Gen0, Gen1, Gen2 counts and pause time
- **Input Latency**: Min, max, avg, p95 input processing times
- **Scroll Performance**: Lines/sec, average scroll time
- **Cell Updates**: Cells updated per second

### Writing Performance Tests

Extend `E2EPerformanceTestBase` to add performance measurement:

```csharp
public class MyPerformanceTests : E2EPerformanceTestBase
{
    public MyPerformanceTests(ITestOutputHelper outputHelper) 
        : base("MyCategory", outputHelper) { }

    [Fact]
    public async Task My_Test_With_Performance()
    {
        await RunPerformanceTestAsync(nameof(My_Test_With_Performance), async () =>
        {
            // Your test logic
            await SendTextAndWaitAsync("test content");
            
        }, assertBaseline: true); // Compare against baseline
    }
}
```

### Performance Thresholds

Use built-in assertion methods:

```csharp
// Assert FPS
PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 30);

// Assert frame times
PerformanceAssertions.AssertFrameTime(
    snapshot.FrameTimeAvg, 
    snapshot.FrameTimeP95, 
    snapshot.FrameTimeP99,
    maxAvgMs: 16.67, 
    maxP95Ms: 33.33, 
    maxP99Ms: 50);

// Assert parser throughput
PerformanceAssertions.AssertParserThroughput(
    snapshot.ParserBytesPerSecond,
    snapshot.ParserSequencesPerSecond,
    minBytesPerSec: 100000);

// Assert no regression from baseline
await AssertNoPerformanceRegressionAsync(
    nameof(My_Test_With_Performance), 
    tolerancePercentage: 10.0);
```

### Threshold Presets

```csharp
// Conservative (for CI environments)
var thresholds = PerformanceThresholds.Conservative;

// Aggressive (for high-performance testing)
var thresholds = PerformanceThresholds.Aggressive;

// Headless (for CI headless mode)
var thresholds = PerformanceThresholds.Headless;
```

### Performance Reports

Reports are automatically generated in `artifacts/reports/`:

- `performance_report_*.html` - HTML report with charts
- `performance_report_*.json` - JSON data for processing
- `regression_report_*.html` - Regression analysis
- `trend_*.html` - Historical trends

### Baseline Management

Baselines are stored in `artifacts/baselines/` as JSON files.

**First run**: Records baselines automatically

**Update baselines**:
```bash
# Run tests to update baselines
dotnet test --filter "FullyQualifiedName~Performance" 

# Export baselines for sharing
# (See E2EPerformance.md for details)
```

**Environment-specific baselines**:
Set `DOTTY_E2E_BASELINES_PATH` to use different baselines for different environments.

### Configuration

See `e2e.appsettings.json` for all performance settings:

```json
{
  "E2ETest": {
    "Performance": {
      "Enabled": true,
      "CollectMetrics": true,
      "SamplingIntervalMs": 500,
      "BaselinesPath": "./artifacts/baselines",
      "RegressionTolerancePercentage": 10.0,
      "FailOnRegression": false,
      "Thresholds": {
        "MinFps": 30.0,
        "MaxFrameTimeP95Ms": 33.33,
        "MaxHeapSizeMB": 512
      }
    }
  }
}
```

### CI/CD Integration

```yaml
- name: Run E2E Tests with Performance
  run: dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
  env:
    DOTTY_E2E_HEADLESS: 1
    DOTTY_PERF_FAIL_ON_REGRESSION: 1

- name: Upload Performance Reports
  uses: actions/upload-artifact@v3
  with:
    name: performance-reports
    path: tests/Dotty.E2E.Tests/artifacts/reports/

- name: Upload Baselines
  uses: actions/upload-artifact@v3
  with:
    name: performance-baselines
    path: tests/Dotty.E2E.Tests/artifacts/baselines/
```

See [docs/E2EPerformance.md](docs/E2EPerformance.md) for detailed documentation.

## Test Command Interface

The test suite communicates with the running Dotty application via a TCP-based command interface.

### Available Commands

- `send-text <text>` - Send text input to terminal
- `send-key <key>` - Send keyboard input
- `send-keycombo <modifiers>+<key>` - Send key combination
- `resize <cols> <rows>` - Resize terminal
- `set-config <key> <value>` - Change configuration
- `get-state` - Get current terminal state
- `screenshot` - Capture screen state
- `wait-for-idle` - Wait for rendering to complete
- `inject-ansi <sequence>` - Inject raw ANSI sequences
- `scroll <lines>` - Scroll buffer
- `copy-selection` - Copy to clipboard
- `paste-clipboard` - Paste from clipboard
- `create-tab` - Create new tab
- `close-tab` - Close current tab
- `next-tab` - Switch to next tab
- `prev-tab` - Switch to previous tab
- `stats` - Get application statistics
- `start-metrics-collection` - Begin collecting performance data
- `stop-metrics-collection` - Stop and return collected metrics
- `get-metrics` - Get current counter values
- `reset-metrics` - Reset all counters
- `get-performance-snapshot` - Get full performance snapshot

## Configuration

### Environment Variables

- `DOTTY_E2E_HEADLESS=1` - Run tests in headless mode
- `DOTTY_TEST_PORT=<port>` - Specify test command port
- `DOTTY_BENCH_THROUGHPUT=1` - Enable throughput benchmark mode
- `DOTTY_PERF_FAIL_ON_REGRESSION=1` - Fail tests on performance regression
- `DOTTY_E2E_BASELINES_PATH=<path>` - Custom baselines directory
- `CI=1` - Enable CI-specific behavior

### Test Settings (e2e.appsettings.json)

```json
{
  "E2ETest": {
    "DefaultTimeoutMs": 30000,
    "StartupTimeoutMs": 60000,
    "HeadlessMode": false,
    "ScreenshotOnFailure": true,
    "ScreenshotDirectory": "./artifacts/screenshots",
    "StateDumpDirectory": "./artifacts/states",
    "LogDirectory": "./artifacts/logs",
    "Performance": {
      "Enabled": true,
      "CollectMetrics": true,
      "SamplingIntervalMs": 500,
      "BaselinesPath": "./artifacts/baselines",
      "ReportsPath": "./artifacts/reports",
      "RegressionTolerancePercentage": 10.0,
      "FailOnRegression": false,
      "Thresholds": {
        "MinFps": 30.0,
        "MaxFrameTimeAvgMs": 16.67,
        "MaxFrameTimeP95Ms": 33.33,
        "MaxHeapSizeMB": 512
      }
    }
  }
}
```

## Test Categories

### BasicFunctionalityE2ETests
Comprehensive tests for basic terminal functionality:
- Text input/output (alphanumeric, special chars, Unicode)
- Cursor movement (all directions: up, down, left, right, home, end)
- Window operations (create, resize, minimize, maximize)
- Copy/paste operations
- Clear screen and line clearing
- Line editing (backspace, delete, insert, enter, tab, escape)
- Word navigation and selection
- Performance tests for basic operations

### RenderingE2ETests
Tests for terminal rendering functionality:
- Basic text rendering
- Color rendering (basic, 256, TrueColor)
- Font rendering and sizing
- Window resizing and reflow
- Scrollback buffer rendering
- Cursor rendering
- Selection highlighting
- Performance thresholds for rendering

### ColorsE2ETests
Comprehensive color support testing:
- **Basic 8 colors**: Black, Red, Green, Yellow, Blue, Magenta, Cyan, White
  - Foreground colors (30-37)
  - Background colors (40-47)
  - Bright foreground colors (90-97)
  - Bright background colors (100-107)
- **256 colors**:
  - Color cube (216 colors, 16-231)
  - Grayscale ramp (24 shades, 232-255)
- **TrueColor (24-bit RGB)**:
  - Pure RGB values
  - Color gradients
  - Background colors
- **Text Attributes**:
  - Bold (1), Dim (2), Italic (3), Underline (4)
  - Blink (5), Reverse (7), Hidden (8), Strikethrough (9)
- **Combinations**: Mixed foreground/background with attributes
- **SGR Reset**: Proper SGR reset behavior (0) and partial resets

### InputE2ETests
Tests for keyboard and input handling:
- Alphanumeric key input
- Special characters
- Control keys (Ctrl, Alt, Shift combinations)
- Function keys (F1-F12)
- Arrow keys and navigation
- Copy/paste operations
- Rapid keystroke buffering

### AnsiE2ETests
Tests for ANSI escape sequence handling:
- SGR codes (colors, styles)
- Cursor movement sequences
- Erase sequences
- Scroll sequences
- Title change sequences
- Complex mixed sequences
- Parser throughput measurements

### WindowManagementE2ETests
Comprehensive tab and window management tests:
- Tab creation (single and multiple)
- Tab switching (keyboard shortcuts: Ctrl+Tab, Alt+1-9)
- Tab closing (mouse and keyboard)
- Tab reordering
- Tab persistence across switching
- Tab titles and activity tracking
- Multiple tab sessions
- Window resize operations
- Tab switching performance (< 100ms avg)
- Memory usage with multiple tabs
- Rapid tab creation/closing stress tests

### SearchE2ETests
Comprehensive search functionality testing:
- Search UI activation (Ctrl+Shift+F)
- Basic text search (forward and backward)
- Case-sensitive search
- Case-insensitive search
- Regular expression search
- Search highlighting
- Navigation between matches (next/previous)
- Search in scrollback buffer
- Search performance with large buffers
- Search with active selection
- Rapid search operations

### LargeBufferE2ETests
Critical tests for 500,000 line buffer handling:
- Buffer creation (500k lines in batches)
- Scroll performance through large buffer
- Search in large buffer (plain text and regex)
- Memory usage monitoring (working set, heap, LOH)
- Performance metrics (FPS maintenance)
- Copy from large buffer
- Window resize with large buffer
- Memory stabilization tests
- Timeout: 10 minutes for full 500k tests
- Memory limit: 2GB for 500k lines

### ConfigurationE2ETests
Tests for configuration and theming:
- Built-in themes
- User themes
- Font configuration
- Color schemes
- Window transparency
- Scrollback buffer size

### PerformanceE2ETests
Tests for performance under load with comprehensive metrics:
- High throughput rendering with FPS monitoring
- Large buffer handling with memory tracking
- Rapid resize operations with frame time measurement
- Multiple tab performance with switch timing
- Startup time measurement
- Parser throughput testing
- Memory stability under load
- Scroll performance
- Cell update rate testing
- Performance regression detection

### IntegrationE2ETests
Tests for shell and tool integration:
- Shell startup
- Basic commands
- Environment variables
- Pipes and redirections
- Interactive programs
- Git integration

## Performance Metrics

The E2E test suite collects comprehensive performance metrics:

### CPU Metrics (Cross-platform)
- **Process CPU percentage**: Overall CPU usage
- **User CPU percentage**: User-mode CPU time
- **System CPU percentage**: Kernel-mode CPU time
- **Per-core CPU usage**: Individual core utilization (Linux)
- **Processor time**: Total seconds of CPU time consumed

### Memory Metrics (Detailed)
**Process Memory:**
- Working set size (current and peak)
- Private memory
- Virtual memory
- Paged memory
- Non-paged memory

**Managed Memory:**
- Managed heap size (total and used)
- Large Object Heap (LOH) size
- Generation 0/1/2 heap sizes
- Allocated bytes (per test)
- Unmanaged memory

**GC Metrics:**
- Gen0/Gen1/Gen2 collection counts
- GC pause percentage
- Finalization pending count
- Commissioned GC count

### Rendering Metrics
- FPS (current, min, max, average)
- Frame times (min, max, avg, p95, p99)
- Parser throughput (bytes/sec, sequences/sec)
- Scroll performance (lines/sec)
- Cell updates per second

### Process Metrics
- Thread count
- Handle count
- Platform and framework info

## Writing New Tests

### Basic Test Structure

```csharp
public class MyE2ETests : E2ETestBase
{
    public MyE2ETests() : base("MyTestCategory")
    {
    }

    [Fact]
    public async Task My_Test_Should_Pass()
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
// Screen buffer assertions
ScreenBufferAssertions.ContainsText(screenLines, "expected text");
ScreenBufferAssertions.LineEquals(screenLines, 0, "first line content");
ScreenBufferAssertions.CursorAt(state.CursorRow, state.CursorCol, 0, 10);

// Rendering assertions
RenderingAssertions.DimensionsMatch(actualCols, actualRows, 80, 24);
RenderingAssertions.CellSizeWithinRange(cellWidth, cellHeight, 8, 12, 16, 20);

// GUI assertions
GuiAssertions.TabCountEquals(stats.TotalTabs, 3);
GuiAssertions.ActiveTabIndexEquals(stats.ActiveTabIndex, 0);

// Performance assertions
PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 30);
PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 33.33);
PerformanceAssertions.AssertParserThroughput(
    snapshot.ParserBytesPerSecond, 
    snapshot.ParserSequencesPerSecond,
    minBytesPerSec: 100000);
PerformanceAssertions.AssertHeapSize(snapshot.HeapSizeBytes, maxHeapSizeBytes: 512 * 1024 * 1024);
```

### Writing Performance Tests

For tests that measure performance, extend `E2EPerformanceTestBase`:

```csharp
public class MyPerformanceTests : E2EPerformanceTestBase
{
    public MyPerformanceTests(ITestOutputHelper outputHelper) 
        : base("MyTestCategory", outputHelper)
    {
    }

    [Fact]
    public async Task My_Performance_Test()
    {
        await RunPerformanceTestAsync(nameof(My_Performance_Test), async () =>
        {
            // Arrange
            var data = GenerateTestData();
            
            // Act
            await ProcessDataAsync(data);
            
            // Assert - Performance thresholds
            var snapshot = CurrentSnapshot;
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 30);
            PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 33.33);
            
        }, assertBaseline: true); // Compare to baseline
    }
}
```

## Troubleshooting

### Tests Failing in CI

1. Ensure headless mode is enabled: `DOTTY_E2E_HEADLESS=1`
2. Check that X11 or display is not required
3. Verify application builds correctly
4. For performance tests, check if thresholds are too aggressive for CI

### Application Not Starting

1. Check build artifacts exist: `dotnet build src/Dotty.App`
2. Verify test port is available
3. Check logs in `artifacts/logs/`

### Screenshots Not Captured

1. Verify `artifacts/screenshots/` directory exists and is writable
2. Check that `ScreenshotOnFailure` is enabled in config
3. Ensure app is running when failure occurs

### Timeouts

1. Increase timeout in `e2e.appsettings.json`
2. Check if application is responsive
3. Review logs for startup issues

### Performance Test Issues

**Performance counters not available:**
1. Verify Dotty app exposes performance counters via TCP interface
2. Check that `PERF:START` command is implemented in the app
3. Review logs for command interface connection errors

**Unstable performance metrics:**
1. Add warm-up period before measuring
2. Increase sampling interval in config
3. Use percentile metrics (p95/p99) instead of averages
4. Check for background processes affecting results

**False regression alerts:**
1. Increase `RegressionTolerancePercentage` for CI environments
2. Ensure consistent test hardware/environment
3. Use environment-specific baselines
4. Review if thresholds are realistic for target environment

**High memory usage in tests:**
1. Check for memory leaks in the application
2. Verify GC is running (check `Gen0/1/2 Collections` in reports)
3. Consider adjusting `MaxHeapSizeMB` thresholds for CI
4. Review large buffer handling in tests

## CI Integration

### GitHub Actions Example

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
        run: dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
        env:
          DOTTY_E2E_HEADLESS: 1
      
      - name: Upload Test Artifacts
        uses: actions/upload-artifact@v3
        if: failure()
        with:
          name: test-artifacts
          path: tests/Dotty.E2E.Tests/artifacts/
      
      - name: Upload Performance Reports
        uses: actions/upload-artifact@v3
        with:
          name: performance-reports
          path: tests/Dotty.E2E.Tests/artifacts/reports/
```

See [docs/E2EPerformance.md](docs/E2EPerformance.md) for more CI/CD integration examples.

## Contributing

Same as Dotty project license.
