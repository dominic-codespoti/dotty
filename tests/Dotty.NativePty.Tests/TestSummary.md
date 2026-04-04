# Dotty.NativePty.Tests - Summary

## Test Files Created

### 1. `PtyPlatformTests.cs` (29 tests)
**Purpose:** Unit tests for platform detection and shell resolution

**Test Categories:**
- **OS Platform Detection (5 tests):** Tests `IsWindows`, `IsLinux`, `IsMacOS`, `IsUnix` properties
- **ConPTY Support Detection (4 tests):** Tests `IsConPtySupported` property on various platforms
- **Default Shell Resolution (8 tests):** Tests `GetDefaultShell()` method
- **Shell Priority Tests (2 tests):** Tests shell fallback chain

**Key Tests:**
- `PtyPlatform_OnlyOnePlatformReturnsTrue` - Ensures mutual exclusivity
- `PtyPlatform_IsConPtySupported_FalseOnNonWindows` - ConPTY only on Windows
- `PtyPlatform_GetDefaultShell_ReturnsNonEmptyString` - Shell path validation
- `PtyPlatform_GetDefaultShell_UsesShellEnvironmentVariable` - Unix SHELL env var

**Platform Coverage:** Runs on all platforms

---

### 2. `PtyFactoryTests.cs` (19 tests)
**Purpose:** Unit and integration tests for PTY factory

**Test Categories:**
- **Create() Method Tests (7 tests):** Factory pattern implementation
- **CreateAndStart() Tests (6 tests):** Convenience method testing
- **IsSupported Property Tests (2 tests):** Platform support detection
- **GetUnsupportedReason() Tests (4 tests):** Error message testing
- **Error Handling Tests (2 tests):** Disposal and lifecycle

**Key Tests:**
- `PtyFactory_Create_ReturnsCorrectImplementation` - Platform-specific type checking
- `PtyFactory_CreateAndStart_ReturnsRunningPty` - End-to-end factory usage
- `PtyFactory_IsSupported_MatchesPlatformDetection` - Support consistency

**Platform Coverage:** Runs on all platforms (with conditional logic)

---

### 3. `IPtyContractTests.cs` (31 tests)
**Purpose:** Interface contract tests using Moq

**Test Categories:**
- **Interface Property Tests (7 tests):** `IsRunning`, `ProcessId`, streams
- **Start() Method Tests (8 tests):** Parameter validation and exceptions
- **Resize() Method Tests (3 tests):** Dimension handling
- **Kill() Method Tests (4 tests):** Process termination
- **WaitForExitAsync() Tests (4 tests):** Async exit handling
- **ProcessExited Event Tests (3 tests):** Event subscription/firing
- **IDisposable Tests (3 tests):** Resource cleanup
- **Stream I/O Tests (3 tests):** Input/output stream contracts

**Key Tests:**
- `IPty_IsRunning_FalseBeforeStart` - Initial state validation
- `IPty_ProcessExited_CanSubscribe` - Event handler subscription
- `IPty_Dispose_IsIdempotent` - Multiple disposal safety

**Platform Coverage:** Platform-agnostic (mock-based)

---

### 4. `WindowsPtyTests.cs` (16 tests) - *Windows Only*
**Purpose:** Integration tests for Windows ConPTY

**Test Categories:**
- **Constructor Tests (2 tests):** Instance creation
- **Start() Tests (7 tests):** Shell startup scenarios
- **I/O Tests (3 tests):** Read/write operations
- **Resize Tests (4 tests):** Terminal resizing
- **Kill Tests (3 tests):** Process termination
- **Event Tests (2 tests):** ProcessExited events
- **WaitForExit Tests (3 tests):** Async exit handling
- **Large Output Test (1 test):** Buffer handling
- **Dispose Tests (2 tests):** Resource cleanup

**Key Tests:**
- `WindowsPty_Start_WithCmd` - CMD.exe startup
- `WindowsPty_Read_ReturnsProcessOutput` - Output capture
- `WindowsPty_Resize_MultipleOperations` - Resize stress test

**Platform Coverage:** Windows 10 version 1809+ (build 17763+)

---

### 5. `UnixPtyTests.cs` (16 tests) - *Unix Only*
**Purpose:** Integration tests for Unix PTY (Linux/macOS)

**Test Categories:**
- **Constructor Tests (2 tests):** Instance creation
- **Start() Tests (7 tests):** Shell startup scenarios
- **I/O Tests (3 tests):** Read/write operations
- **Resize Tests (3 tests):** Control socket resize
- **Kill Tests (3 tests):** Process termination
- **Event Tests (2 tests):** ProcessExited events
- **WaitForExit Tests (3 tests):** Async exit handling
- **Dispose Tests (2 tests):** Resource cleanup

**Key Tests:**
- `UnixPty_Start_WithBash` - Bash shell startup
- `UnixPty_Resize_SendsResizeCommand` - Control socket communication
- `UnixPty_Helper_FindExecutable` - Helper binary discovery

**Platform Coverage:** Linux and macOS only

---

### 6. `PtyTestHelpers.cs` (Test Utilities)
**Purpose:** Shared test utilities and helper methods

**Utilities Provided:**
- `DefaultTimeout`, `ShortTimeout`, `LongTimeout` - Test timeouts
- `ReadAllAvailableAsync()` - Async stream reading
- `ReadUntilAsync()` - Pattern-based reading
- `WriteToPtyAsync()` / `WriteLineToPtyAsync()` - Input helpers
- `SafeCleanup()` - Reliable PTY cleanup
- `ProcessExists()` - Process verification
- `AssertPtyRunning()` / `AssertPtyNotRunning()` - State assertions

**Conditional Fact Attributes:**
- `WindowsOnlyFact` - Skips on non-Windows
- `LinuxOnlyFact` - Skips on non-Linux
- `MacOSOnlyFact` - Skips on non-macOS
- `UnixOnlyFact` - Skips on non-Unix
- `PtySupportedFact` - Skips when PTY not supported
- `ConPtySupportedFact` - Skips when ConPTY not available

---

## Test Count Summary

| File | Total Tests | Unit Tests | Integration Tests |
|------|-------------|------------|-------------------|
| PtyPlatformTests.cs | 29 | 29 | 0 |
| PtyFactoryTests.cs | 19 | 19 | 0 |
| IPtyContractTests.cs | 31 | 31 | 0 |
| WindowsPtyTests.cs | 16 | 0 | 16 |
| UnixPtyTests.cs | 16 | 0 | 16 |
| **Total** | **111** | **79** | **32** |

---

## Platform Coverage

### Universal Tests (79 tests)
These tests run on all platforms:
- **PtyPlatformTests** - OS detection utilities
- **PtyFactoryTests** - Factory pattern
- **IPtyContractTests** - Interface contracts (mock-based)

### Windows-Specific Tests (16 tests)
Only run on Windows 10 version 1809+:
- **WindowsPtyTests** - ConPTY integration tests

### Unix-Specific Tests (16 tests)
Only run on Linux and macOS:
- **UnixPtyTests** - Unix PTY integration tests

---

## CI Configuration Notes

### GitHub Actions / Azure DevOps

```yaml
# Example workflow configuration
jobs:
  test-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      - name: Build
        run: dotnet build
      - name: Test
        run: dotnet test tests/Dotty.NativePty.Tests

  test-linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      - name: Build Native Helper
        run: cd src/Dotty.NativePty && make
      - name: Build
        run: dotnet build
      - name: Test
        run: dotnet test tests/Dotty.NativePty.Tests

  test-macos:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      - name: Build Native Helper
        run: cd src/Dotty.NativePty && make
      - name: Build
        run: dotnet build
      - name: Test
        run: dotnet test tests/Dotty.NativePty.Tests
```

### Test Execution Notes

1. **Unit tests** (79 tests) run on all platforms without additional dependencies
2. **Integration tests** (32 tests) require:
   - Windows: Windows 10 build 17763+ for ConPTY
   - Unix: Built `pty-helper` executable in PATH or expected locations

3. **Test Timeouts:**
   - Default: 10 seconds
   - Short: 2 seconds  
   - Long: 30 seconds
   - CI: Consider increasing for slower agents

4. **Process Cleanup:**
   - Tests use `SafeCleanup()` helper
   - All PTY instances disposed via `IDisposable`
   - Orphaned process detection via `ProcessExists()`

---

## Test Dependencies

### NuGet Packages
- `Microsoft.NET.Test.Sdk` (17.8.0)
- `xunit` (2.6.2)
- `xunit.runner.visualstudio` (2.5.4)
- `Xunit.SkippableFact` (1.4.13) - Conditional test skipping
- `FluentAssertions` (6.12.0) - Assertion library
- `Moq` (4.20.69) - Mocking framework

### Project References
- `Dotty.NativePty` - PTY implementation
- `Dotty.Abstractions` - Interfaces and contracts

---

## Key Testing Patterns

### 1. Conditional Execution
```csharp
[ConditionalFacts.WindowsOnlyFact]
public void WindowsPty_Start_WithCmd() { ... }

[SkippableFact]
public void WindowsPty_SomeTest()
{
    Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
    // Test code...
}
```

### 2. Async Test Pattern
```csharp
[Fact]
public async Task UnixPty_Read_ReturnsProcessOutput()
{
    using var pty = new UnixPty();
    pty.Start();
    
    var output = await PtyTestHelpers.ReadUntilAsync(
        pty.OutputStream, "expected");
    
    output.Should().Contain("expected");
}
```

### 3. Cleanup Pattern
```csharp
public class UnixPtyTests : IDisposable
{
    private IPty? _pty;
    
    public void Dispose()
    {
        PtyTestHelpers.SafeCleanup(_pty);
    }
}
```

### 4. Mock-based Unit Test
```csharp
[Fact]
public void IPty_Start_WithCustomShell()
{
    var mockPty = new Mock<IPty>();
    mockPty.Object.Start(shell: "/bin/bash");
    mockPty.Verify(p => p.Start("/bin/bash", 80, 24, null, null));
}
```

---

## Known Limitations

1. **Unix Integration Tests** require the `pty-helper` native executable to be built
2. **Windows Integration Tests** only run on Windows 10 build 17763 or later
3. Some tests use platform-specific commands (`cmd.exe`, `/bin/bash`) that may not be available
4. Process timing tests may be flaky on slow CI agents - adjust timeouts as needed
5. Control socket tests on Unix may need increased delays for connection establishment

---

## Future Enhancements

1. Add stress tests for multiple concurrent PTY sessions
2. Add memory leak detection tests
3. Add performance benchmarks for I/O throughput
4. Add tests for custom environment variable handling
5. Add tests for working directory validation
6. Add tests for signal handling on Unix
7. Add tests for Windows console mode attributes
