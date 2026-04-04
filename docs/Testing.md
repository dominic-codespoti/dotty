# Test Suite

Dotty employs a comprehensive testing strategy using **xUnit** with platform-specific test filtering, headless UI testing via Avalonia, and source generator testing. The test suite ensures correctness across all layers: parser, buffer, rendering, PTY management, and configuration generation.

## Table of Contents

1. [Test Framework (xUnit)](#test-framework-xunit)
2. [Test Project Structure](#test-project-structure)
3. [Unit Tests vs Integration Tests](#unit-tests-vs-integration-tests)
4. [Platform-Specific Test Filtering](#platform-specific-test-filtering)
5. [Running Tests Locally](#running-tests-locally)
6. [CI/CD Test Execution](#cicd-test-execution)
7. [Writing New Tests Guidelines](#writing-new-tests-guidelines)
8. [Coverage Reporting](#coverage-reporting)
9. [Source File References](#source-file-references)

---

## Test Framework (xUnit)

Dotty uses **xUnit** as the primary testing framework, complemented by additional packages for specific testing needs.

### Core Testing Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `xunit` | 2.5.0-2.6.2 | Core testing framework |
| `xunit.runner.visualstudio` | 2.5.0-2.5.4 | Visual Studio / VS Code integration |
| `Microsoft.NET.Test.Sdk` | 17.8.0-18.0.1 | Test host for .NET |
| `FluentAssertions` | 6.12.0 | Readable assertion syntax |
| `Moq` | 4.18.4-4.20.69 | Mocking framework |
| `Xunit.SkippableFact` | 1.4.13 | Conditional test skipping |
| `Avalonia.Headless` | 11.3.8 | Headless UI testing |
| `Avalonia.Headless.XUnit` | 11.3.8 | xUnit integration for Avalonia |
| `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing.XUnit` | Latest | Source generator testing |

### Test Configuration

```xml
<!-- Example: tests/Dotty.App.Tests/Dotty.App.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="xunit" Version="2.5.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.0" />
    <PackageReference Include="Avalonia" Version="11.3.8" />
    <PackageReference Include="Avalonia.Headless" Version="11.3.8" />
    <PackageReference Include="Avalonia.Headless.XUnit" Version="11.3.8" />
    <PackageReference Include="Moq" Version="4.18.4" />
  </ItemGroup>
</Project>
```

### Assertion Styles

Tests use both standard xUnit assertions and FluentAssertions for readability:

```csharp
// Standard xUnit assertions
Assert.Equal("expected", actual);
Assert.True(condition);
Assert.NotNull(obj);

// FluentAssertions (preferred for complex scenarios)
result.Should().NotBeNull();
result.Value.Should().Be(expected);
collection.Should().ContainSingle();
action.Should().Throw<ArgumentException>();
```

---

## Test Project Structure

The test suite is organized into four main test projects, mirroring the source project structure:

```
tests/
├── Dotty.App.Tests/              # UI and integration tests
│   ├── BasicAnsiParserTests.cs
│   ├── SgrParserTests.cs
│   ├── SgrColorTests.cs
│   ├── ControlCodeTests.cs
│   ├── AsciiArtRenderTests.cs
│   ├── BackgroundSynthTests.cs
│   ├── ScrollbackRenderTest.cs
│   ├── PermutationScrollRenderTests.cs
│   ├── ThemeTests.cs
│   ├── ThemeManagerTests.cs
│   ├── EndToEndTests.cs
│   └── ...
│
├── Dotty.Terminal.Tests/         # Core terminal logic tests
│   ├── HyperlinkStorageTests.cs
│   ├── Osc8ParserTests.cs
│   ├── TerminalSearchTests.cs
│   ├── SearchMatchTests.cs
│   └── CellHyperlinkTests.cs
│
├── Dotty.NativePty.Tests/        # PTY platform tests
│   ├── IPtyContractTests.cs      # Cross-platform contract tests
│   ├── PtyFactoryTests.cs
│   ├── PtyPlatformTests.cs
│   ├── UnixPtyTests.cs           # Linux/macOS specific
│   └── WindowsPtyTests.cs        # Windows specific
│
└── Dotty.Config.SourceGenerator.Tests/  # Source generator tests
    ├── IntegrationTests.cs
    ├── ExpressionEvaluatorTests.cs
    ├── ConfigDiscoveryTests.cs
    ├── ConfigExtractorTests.cs
    ├── EmitterTests.cs
    ├── ThemeResolverTests.cs
    └── TestHelpers.cs
```

### Test Organization

Each test project follows consistent naming conventions:

| Naming Pattern | Purpose | Example |
|----------------|---------|---------|
| `*Tests.cs` | Unit tests for a specific component | `BasicAnsiParserTests.cs` |
| `*Test.cs` | Single-feature tests | `ScrollbackRenderTest.cs` |
| `*IntegrationTests.cs` | Cross-component integration tests | `HyperlinkIntegrationTests.cs` |
| `*ReproTests.cs` | Bug reproduction and regression tests | `StressFuzzReproTests.cs` |
| `*ContractTests.cs` | Interface contract verification | `IPtyContractTests.cs` |

---

## Unit Tests vs Integration Tests

### Unit Tests

Unit tests isolate individual components with mocked dependencies:

```csharp
public class BasicAnsiParserTests
{
    [Fact]
    public void DecGraphicsTranslateToUnicode()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Act
        parser.Feed(Encoding.UTF8.GetBytes("\u001b(0lqk\u001b(BABC"));

        // Assert
        Assert.Equal("┌─┐", handler.PrintCalls[0]);
        Assert.Equal("ABC", handler.PrintCalls[1]);
    }
}
```

**Characteristics:**
- Fast execution (<100ms per test)
- No external dependencies (filesystem, network, PTY)
- Deterministic results
- Test one behavior per test

### Integration Tests

Integration tests verify component interactions:

```csharp
public class EndToEndTests : IDisposable
{
    private readonly TerminalInstance _terminal;

    public EndToEndTests()
    {
        _terminal = new TerminalInstance(rows: 24, cols: 80);
    }

    [Fact]
    public void Terminal_CanRunShellAndCaptureOutput()
    {
        // Start a shell via PTY
        using var pty = PtyFactory.CreateAndStart(
            shell: "/bin/bash",
            columns: 80,
            rows: 24);

        // Connect parser to buffer
        var adapter = new TerminalAdapter(_terminal.Buffer);
        var parser = new BasicAnsiParser { Handler = adapter };

        // Send command and read output
        pty.Write("echo 'Hello World'\n");

        // Process output
        var buffer = new byte[4096];
        int read = pty.Read(buffer);
        parser.Feed(buffer.AsSpan(0, read));

        // Verify output appears in buffer
        var lastRow = _terminal.Buffer.GetRowText(_terminal.Buffer.CursorRow);
        Assert.Contains("Hello World", lastRow);
    }

    public void Dispose()
    {
        _terminal?.Dispose();
    }
}
```

**Characteristics:**
- Slower execution (100ms-10s per test)
- Real dependencies (PTY, filesystem)
- May have platform-specific behavior
- Test complete workflows

### Headless UI Tests

Avalonia Headless enables UI testing without a display:

```csharp
public class ThemeTests
{
    [AvaloniaFact]  // Headless UI test attribute
    public void ThemeChange_UpdatesBackgroundColor()
    {
        // Arrange
        var window = new Window();
        var canvas = new TerminalCanvas();
        window.Content = canvas;

        // Act
        window.Show();
        canvas.Buffer = new TerminalBuffer(24, 80);

        // Trigger theme change
        Application.Current.Resources["TerminalBackground"] = Brushes.Red;

        // Assert
        // Verify visual tree updated
        window.Close();
    }
}
```

---

## Platform-Specific Test Filtering

Tests are filtered by platform using compile-time constants and runtime checks.

### Conditional Compilation

```csharp
public class UnixPtyTests : IDisposable
{
#if !WINDOWS
    [Fact]
    public void Pty_CanStartBash()
    {
        using var pty = new UnixPty();
        pty.Start("/bin/bash", 80, 24);
        Assert.True(pty.IsRunning);
    }

    [Fact]
    public void Pty_ReadWrite()
    {
        using var pty = new UnixPty();
        pty.Start("/bin/bash", 80, 24);

        pty.Write("echo test123\n");

        var buffer = new byte[1024];
        int read = pty.Read(buffer, timeoutMs: 5000);

        Assert.True(read > 0);
        var output = Encoding.UTF8.GetString(buffer, 0, read);
        Assert.Contains("test123", output);
    }
#endif
}
```

### Runtime Platform Detection

```csharp
public class PtyPlatformTests
{
    [SkippableFact]
    public void Pty_IsSupportedOnCurrentPlatform()
    {
        Skip.IfNot(PtyFactory.IsSupported, "PTY not supported on this platform");

        using var pty = PtyFactory.Create();
        Assert.NotNull(pty);
    }

    [SkippableFact]
    public void WindowsPty_RequiresConPtySupport()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not available");

        using var pty = new WindowsPty();
        Assert.NotNull(pty);
    }
}
```

### Platform Test Distribution

| Test Category | Windows | Linux | macOS |
|---------------|---------|-------|-------|
| Parser Tests | ✅ | ✅ | ✅ |
| Buffer Tests | ✅ | ✅ | ✅ |
| Rendering Tests | ✅ (Headless) | ✅ (Headless) | ✅ (Headless) |
| PTY Tests | ✅ ConPTY | ✅ POSIX | ✅ POSIX |
| Config Generator | ✅ | ✅ | ✅ |

### Test Runtime Identifiers

```xml
<!-- Different test projects target different platforms -->
<!-- Dotty.App.Tests.csproj -->
<PropertyGroup>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
</PropertyGroup>

<!-- Dotty.NativePty.Tests.csproj - multi-target -->
<PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
  <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
</PropertyGroup>
```

---

## Running Tests Locally

### Using dotnet CLI

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --verbosity normal

# Run specific test project
dotnet test tests/Dotty.App.Tests/

# Run specific test class
dotnet test --filter "FullyQualifiedName~BasicAnsiParserTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~DecGraphicsTranslateToUnicode"

# Run with code coverage
dotnet test --collect:"XPlat Code Coverage"

# Run in Release mode
dotnet test -c Release
```

### Using VS Code

1. Install the **.NET Test Explorer** extension
2. Open the Test Explorer panel
3. Click the refresh icon to discover tests
4. Run individual tests or entire suites

### Using Visual Studio

1. Open `Dotty.sln`
2. Build the solution (Ctrl+Shift+B)
3. Open Test Explorer (Test → Test Explorer)
4. Click "Run All" or select specific tests

### Environment-Specific Testing

```bash
# Run with specific environment variables
DOTTY_BENCH_THROUGHPUT=1 dotnet test tests/Dotty.App.Tests/ --filter "FullyQualifiedName~Throughput"

# Disable glyph discovery for faster tests
DOTTY_DISABLE_GLYPH_DISCOVERY=1 dotnet test
```

---

## CI/CD Test Execution

### GitHub Actions Workflow

```yaml
# .github/workflows/test.yml
name: Test Suite

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

jobs:
  test:
    strategy:
      matrix:
        os: [ubuntu-latest, windows-latest, macos-latest]
        dotnet-version: ['10.0.x']

    runs-on: ${{ matrix.os }}

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ matrix.dotnet-version }}

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test (Unix)
        if: runner.os != 'Windows'
        run: dotnet test --no-build --verbosity normal

      - name: Test (Windows)
        if: runner.os == 'Windows'
        run: dotnet test --no-build --verbosity normal --filter "FullyQualifiedName!~UnixPtyTests"

      - name: Upload coverage
        uses: codecov/codecov-action@v3
        with:
          files: ./tests/**/coverage.cobertura.xml
```

### Test Execution Order

```yaml
# Run tests in dependency order
- name: Test Abstractions
  run: dotnet test tests/Dotty.Terminal.Tests/ --no-build

- name: Test Config Generator
  run: dotnet test tests/Dotty.Config.SourceGenerator.Tests/ --no-build

- name: Test Native PTY
  run: dotnet test tests/Dotty.NativePty.Tests/ --no-build

- name: Test App (Integration)
  run: dotnet test tests/Dotty.App.Tests/ --no-build
```

---

## Writing New Tests Guidelines

### Test Naming Conventions

```csharp
// Pattern: {MethodUnderTest}_{Scenario}_{ExpectedResult}

[Fact]
public void Feed_EmptyArray_DoesNothing()
{
    // Test code
}

[Fact]
public void Feed_CsiSequence_CallsCorrectHandler()
{
    // Test code
}

[Fact]
public void Resize_LargerDimensions_PreservesContent()
{
    // Test code
}

[Fact]
public void LineFeed_AtBottomOfScrollRegion_ScrollsUp()
{
    // Test code
}
```

### Test Structure (AAA Pattern)

```csharp
[Fact]
public void SgrColor_Parse256Color_ReturnsCorrectArgb()
{
    // Arrange
    var parser = new SgrParserArgb();
    var input = "38;5;208";  // 256-color orange

    // Act
    var result = parser.ParseForeground(input.AsSpan());

    // Assert
    Assert.Equal(0xFFFF8700, result);  // Expected ARGB
}
```

### Mocking External Dependencies

```csharp
public class TerminalAdapterTests
{
    [Fact]
    public void OnPrint_WritesToBuffer()
    {
        // Arrange
        var mockBuffer = new Mock<TerminalBuffer>(24, 80);
        var adapter = new TerminalAdapter(mockBuffer.Object);

        // Act
        adapter.OnPrint("Hello".AsSpan());

        // Assert
        mockBuffer.Verify(b => b.WriteText(
            It.Is<ReadOnlySpan<char>>(s => s.ToString() == "Hello"),
            It.IsAny<CellAttributes>()),
            Times.Once);
    }
}
```

### Async Test Patterns

```csharp
public class AsyncTests
{
    [Fact]
    public async Task Pty_ReadAsync_ReturnsData()
    {
        using var pty = PtyFactory.Create();
        pty.Start("/bin/bash", 80, 24);

        pty.Write("echo test\n");

        var buffer = new byte[1024];
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        int read = await pty.ReadAsync(buffer, cts.Token);

        Assert.True(read > 0);
    }
}
```

### Test Categories and Traits

```csharp
[Trait("Category", "Parser")]
[Trait("Priority", "High")]
public class BasicAnsiParserTests
{
    [Fact]
    [Trait("SequenceType", "CSI")]
    public void HandleCsi_CursorMovement()
    {
        // Test code
    }
}

// Filter by trait
dotnet test --filter "Category=Parser"
dotnet test --filter "Priority=High"
```

### Fuzzing and Stress Tests

```csharp
public class StressFuzzReproTests
{
    [Theory]
    [InlineData("\x1b[31m")]           // Simple SGR
    [InlineData("\x1b[38;2;255;0;0m")] // True color
    [InlineData("\x1b[?1049h")]        // Alternate screen
    [InlineData("\x1b]0;title\x07")]   // OSC title
    public void Parse_DoesNotCrash(string input)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var bytes = Encoding.UTF8.GetBytes(input);

        // Should not throw
        parser.Feed(bytes);

        // Buffer should remain valid
        Assert.NotNull(handler);
    }

    [Fact]
    public void RandomByteSequences_DoNotCrash()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var random = new Random(42);  // Seeded for reproducibility
        var buffer = new byte[1000];

        for (int i = 0; i < 1000; i++)
        {
            random.NextBytes(buffer);
            parser.Feed(buffer);
        }

        // If we get here, no crashes occurred
        Assert.True(true);
    }
}
```

### Headless UI Test Guidelines

```csharp
public class CanvasRenderingTests
{
    [AvaloniaFact]
    public void TerminalCanvas_RendersText()
    {
        // Must run on UI thread
        Dispatcher.UIThread.Invoke(() =>
        {
            var canvas = new TerminalCanvas();
            var buffer = new TerminalBuffer(24, 80);
            buffer.WriteText("Hello".AsSpan(), new CellAttributes());
            canvas.Buffer = buffer;

            // Trigger render
            canvas.InvalidateVisual();

            // Verify no exceptions during render
            // (Visual verification requires screenshot comparison)
        });
    }
}
```

---

## Coverage Reporting

### Collecting Coverage

```bash
# Install coverlet collector
dotnet add tests/Dotty.App.Tests package coverlet.collector

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Generate report
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coveragereport
```

### Coverage Configuration

```xml
<!-- tests/.runsettings -->
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>
            [*.Tests]*,
            [Dotty.App]Avalonia.*,
            [Dotty.NativePty]NativeMethods
          </Exclude>
          <IncludeTestAssembly>false</IncludeTestAssembly>
          <Threshold>80</Threshold>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

### Coverage Targets

| Component | Target Coverage | Current |
|-----------|-----------------|---------|
| Dotty.Abstractions | 90% | - |
| Dotty.Terminal | 85% | - |
| Dotty.NativePty | 70% | - |
| Dotty.App | 75% | - |
| Dotty.Config.SourceGenerator | 80% | - |

### Excluded Code

```csharp
// Code excluded from coverage
[ExcludeFromCodeCoverage]
public class DebugUtilities
{
    // Debug-only code
}

// Platform-specific native methods
[ExcludeFromCodeCoverage]
internal static class NativeMethods
{
    [DllImport("libc")]
    internal static extern int fork();
}
```

---

## Source File References

### Test Projects

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/Dotty.App.Tests.csproj` | App/UI test project configuration |
| `tests/Dotty.Terminal.Tests/Dotty.Terminal.Tests.csproj` | Terminal core test configuration |
| `tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj` | PTY test configuration |
| `tests/Dotty.Config.SourceGenerator.Tests/Dotty.Config.SourceGenerator.Tests.csproj` | Source generator test configuration |

### Parser Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/BasicAnsiParserTests.cs` | Core ANSI parser tests |
| `tests/Dotty.App.Tests/SgrParserTests.cs` | SGR attribute parsing tests |
| `tests/Dotty.App.Tests/SgrColorTests.cs` | Color parsing and conversion tests |
| `tests/Dotty.App.Tests/ControlCodeTests.cs` | C0 control character tests |
| `tests/Dotty.Terminal.Tests/Osc8ParserTests.cs` | OSC 8 hyperlink parsing tests |

### Buffer Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/BufferWriterTests.cs` | Buffer text writing tests |
| `tests/Dotty.App.Tests/TerminalBufferCursorTests.cs` | Cursor movement tests |
| `tests/Dotty.App.Tests/GraphemeStorageTests.cs` | Wide character handling tests |
| `tests/Dotty.App.Tests/ScreenContinuationTests.cs` | Multi-cell character tests |
| `tests/Dotty.App.Tests/ContinuationClearTests.cs` | Clear operations with continuations |

### Rendering Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/BackgroundSynthTests.cs` | Background region synthesis tests |
| `tests/Dotty.App.Tests/AsciiArtRenderTests.cs` | ASCII art rendering tests |
| `tests/Dotty.App.Tests/PermutationScrollRenderTests.cs` | Scroll rendering permutations |
| `tests/Dotty.App.Tests/ScrollbackRenderTest.cs` | Scrollback rendering tests |
| `tests/Dotty.App.Tests/ScrollbackMinimalTest.cs` | Minimal scrollback scenarios |
| `tests/Dotty.App.Tests/SimpleScrollbackTest.cs` | Basic scrollback tests |

### PTY Tests

| File | Description |
|------|-------------|
| `tests/Dotty.NativePty.Tests/IPtyContractTests.cs` | Cross-platform PTY interface tests |
| `tests/Dotty.NativePty.Tests/PtyFactoryTests.cs` | Factory pattern tests |
| `tests/Dotty.NativePty.Tests/PtyPlatformTests.cs` | Platform detection tests |
| `tests/Dotty.NativePty.Tests/UnixPtyTests.cs` | Unix-specific PTY tests |
| `tests/Dotty.NativePty.Tests/WindowsPtyTests.cs` | Windows ConPTY tests |
| `tests/Dotty.NativePty.Tests/PtyTestHelpers.cs` | Shared test utilities |

### Config Generator Tests

| File | Description |
|------|-------------|
| `tests/Dotty.Config.SourceGenerator.Tests/IntegrationTests.cs` | End-to-end generator tests |
| `tests/Dotty.Config.SourceGenerator.Tests/ExpressionEvaluatorTests.cs` | Expression evaluation tests |
| `tests/Dotty.Config.SourceGenerator.Tests/ConfigDiscoveryTests.cs` | Config class discovery tests |
| `tests/Dotty.Config.SourceGenerator.Tests/ConfigExtractorTests.cs` | Property extraction tests |
| `tests/Dotty.Config.SourceGenerator.Tests/EmitterTests.cs` | Code generation tests |
| `tests/Dotty.Config.SourceGenerator.Tests/ThemeResolverTests.cs` | Theme resolution tests |
| `tests/Dotty.Config.SourceGenerator.Tests/TestHelpers.cs` | Shared test utilities |

### Theme Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/ThemeTests.cs` | Theme rendering tests |
| `tests/Dotty.App.Tests/ThemeManagerTests.cs` | Theme manager tests |
| `tests/Dotty.App.Tests/ThemeRegistryTests.cs` | Theme registry tests |
| `tests/Dotty.App.Tests/UserThemeLoaderTests.cs` | User theme loading tests |
| `tests/Dotty.App.Tests/ThemeValidatorTests.cs` | Theme validation tests |

### Search and Hyperlink Tests

| File | Description |
|------|-------------|
| `tests/Dotty.Terminal.Tests/TerminalSearchTests.cs` | Search algorithm tests |
| `tests/Dotty.Terminal.Tests/SearchMatchTests.cs` | Match result tests |
| `tests/Dotty.App.Tests/SearchColoredTextTest.cs` | Colored text search tests |
| `tests/Dotty.App.Tests/SearchBufferContentTest.cs` | Buffer content search tests |
| `tests/Dotty.App.Tests/SearchOverlayLogicTests.cs` | Search UI logic tests |
| `tests/Dotty.App.Tests/SearchHighlightRenderingTests.cs` | Search highlight rendering tests |
| `tests/Dotty.App.Tests/HyperlinkServiceTests.cs` | Hyperlink service tests |
| `tests/Dotty.App.Tests/HyperlinkIntegrationTests.cs` | Hyperlink integration tests |
| `tests/Dotty.App.Tests/HyperlinkRenderingTests.cs` | Hyperlink rendering tests |
| `tests/Dotty.Terminal.Tests/CellHyperlinkTests.cs` | Cell hyperlink storage tests |
| `tests/Dotty.Terminal.Tests/HyperlinkStorageTests.cs` | Hyperlink storage tests |

### Stress and Fuzz Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/StressFuzzReproTests.cs` | Fuzzing reproduction tests |
| `tests/Dotty.App.Tests/NeovimReplayTests.cs` | Neovim interaction replay tests |
| `tests/Dotty.App.Tests/MoreReproTests.cs` | Additional regression tests |
| `tests/Dotty.App.Tests/ReproAttemptsTests.cs` | Bug reproduction attempts |
| `tests/Dotty.App.Tests/EndToEndTests.cs` | Full end-to-end integration tests |

### Mouse Tests

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/MouseModeTests.cs` | Mouse protocol handling tests |

---

## Additional Resources

- **xUnit Documentation**: https://xunit.net/
- **FluentAssertions**: https://fluentassertions.com/
- **Moq Quickstart**: https://github.com/moq/moq4/wiki/Quickstart
- **Avalonia Headless Testing**: https://docs.avaloniaui.net/docs/concepts/headless
- **.NET Testing Best Practices**: Microsoft testing guidelines

---

*Document version: 1.0*  
*Last updated: 2026-04-04*
