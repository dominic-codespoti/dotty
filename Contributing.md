# Contributing to Dotty

Thank you for your interest in contributing to Dotty! This document provides comprehensive guidelines for setting up your development environment, building the project, running tests, and submitting contributions.

## Table of Contents

- [Development Environment Setup](#development-environment-setup)
- [Build Requirements](#build-requirements)
- [Building the Project](#building-the-project)
- [Running Tests](#running-tests)
- [Code Style Guidelines](#code-style-guidelines)
- [Project Structure](#project-structure)
- [Pull Request Process](#pull-request-process)
- [Development Resources](#development-resources)
- [Troubleshooting](#troubleshooting)

## Development Environment Setup

### Prerequisites

To build and run Dotty, you'll need the following tools installed:

#### Required

| Tool | Version | Purpose |
|------|---------|---------|
| .NET SDK | 10.0.x (or 9.0+) | Primary build system |
| make | any | Native PTY helper build |
| gcc or clang | any | Compiling C code for POSIX PTY support |
| git | any | Source control |

#### Optional but Recommended

- **Visual Studio 2022** (Windows) or **Rider** / **VS Code** (cross-platform)
- **Docker** (for isolated build testing)

### Platform-Specific Setup

#### Linux (Ubuntu/Debian)

```bash
# Install .NET SDK
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0

# Install build essentials
sudo apt-get install -y build-essential

# Clone repository
git clone https://github.com/dominic-codespoti/dotty.git
cd dotty
```

#### macOS

```bash
# Install Homebrew if not already installed
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

# Install .NET SDK and build tools
brew install dotnet
brew install make

# Clone repository
git clone https://github.com/dominic-codespoti/dotty.git
cd dotty
```

#### Windows

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Install [Visual Studio 2022](https://visualstudio.microsoft.com/) with C++ workload (optional, for native development)
3. Clone the repository:
   ```powershell
   git clone https://github.com/dominic-codespoti/dotty.git
   cd dotty
   ```

## Build Requirements

### .NET Version

- **Target Framework**: .NET 10.0
- **Minimum Supported**: .NET 9.0 (may work but not officially supported)

### Native Dependencies

On Linux and macOS, Dotty requires a native PTY helper (`pty-helper`) written in C. This is built automatically by the Makefile in `src/Dotty.NativePty/`.

### Build Configurations

| Configuration | Purpose | Native AOT |
|--------------|---------|------------|
| `Debug` | Development, debugging | Disabled |
| `Release` | Production builds, CI | Enabled |

### Solution Structure

```
Dotty.sln
├── src/
│   ├── Dotty.App/                    # Avalonia UI application
│   ├── Dotty.Terminal/               # Terminal core engine
│   ├── Dotty.NativePty/              # POSIX PTY helper (C + C# wrapper)
│   ├── Dotty.Abstractions/           # Shared interfaces
│   └── Dotty.Config.SourceGenerator/ # Compile-time config generator
└── tests/
    └── Dotty.App.Tests/              # Unit and integration tests
```

## Building the Project

### Full Build (All Platforms)

```bash
# Build native PTY helper (Linux/macOS only)
cd src/Dotty.NativePty && make && cd ../..

# Restore dependencies
dotnet restore Dotty.sln

# Build entire solution
dotnet build Dotty.sln -c Release
```

### Quick Build (Iterative Development)

```bash
# Build without native helper (uses existing binary if present)
dotnet build Dotty.sln -c Debug

# Run the application
dotnet run --project src/Dotty.App
```

### Native AOT Publishing

For production releases with Native AOT:

```bash
# Linux x64
dotnet publish src/Dotty.App/Dotty.App.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishAot=true

# macOS x64
# (similar command with -r osx-x64)

# Windows x64
# (similar command with -r win-x64)
```

## Running Tests

Dotty uses **xUnit** for unit testing with **Avalonia.Headless** for UI component testing.

### Run All Tests

```bash
# Run all tests
dotnet test Dotty.sln

# Run with verbose output
dotnet test Dotty.sln --verbosity normal

# Run specific test project
dotnet test tests/Dotty.App.Tests
```

### Platform-Specific Test Filtering

Tests can be filtered by platform requirements:

```bash
# Skip Unix-specific tests on Windows
dotnet test Dotty.sln --filter "FullyQualifiedName!~Unix"

# Run only parser tests
dotnet test Dotty.sln --filter "FullyQualifiedName~Parser"
```

### Test Categories

| Test Type | Description | Location |
|-----------|-------------|----------|
| Buffer Tests | Terminal buffer correctness | `BasicAnsiParserTests.cs`, `SgrColorTests.cs` |
| Rendering Tests | Visual state assertions | `AsciiArtRenderTests.cs`, `PermutationScrollRenderTests.cs` |
| Fuzz/Stress Tests | Boundary and safety testing | `StressFuzzReproTests.cs`, `NeovimReplayTests.cs` |
| Integration Tests | End-to-end scenarios | `EndToEndTests.cs` |

### Running Tests in CI Mode

```bash
# Generate TRX test results for CI
dotnet test Dotty.sln --logger trx --results-directory ./TestResults
```

## Code Style Guidelines

### General Principles

Dotty prioritizes **performance** and **memory safety**. Follow these principles:

1. **Zero allocations in hot paths** - Use `Span<T>`, `Memory<T>`, and stackalloc where appropriate
2. **Use `ref struct` for buffer manipulation** - Ensures stack-only semantics
3. **Prefer value types over reference types** in performance-critical code
4. **Avoid LINQ in tight loops** - Use explicit loops for performance-critical paths

### C# Style Guidelines

#### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes, Structs | PascalCase | `TerminalBuffer`, `AnsiParser` |
| Interfaces | PascalCase with `I` prefix | `ITerminalHandler`, `IConfigProvider` |
| Methods | PascalCase | `ParseSequence()`, `RenderBuffer()` |
| Properties | PascalCase | `public int Width { get; }` |
| Private fields | `_camelCase` | `private readonly int _bufferSize;` |
| Constants | `PascalCase` or `UPPER_SNAKE` | `DefaultScrollbackLines` |
| Local variables | `camelCase` | `var currentLine = 0;` |

#### Code Formatting

- Use 4 spaces for indentation (no tabs)
- Opening braces on the same line (K&R style)
- Maximum line length: 120 characters
- Always use braces for control structures, even for single-line blocks

```csharp
// Good
if (condition) {
    DoSomething();
}

// Avoid
if (condition)
    DoSomething();
```

#### Performance-Oriented Patterns

```csharp
// Use Span<T> for zero-copy string processing
public void ProcessBuffer(ReadOnlySpan<byte> input) {
    // Process without allocations
}

// Use ref struct for stack-only safety
public ref struct BufferWriter {
    private Span<byte> _buffer;
    // ...
}

// Prefer Try-pattern for performance
if (int.TryParse(input, out var value)) {
    // Use value
}
```

### Unsafe Code Guidelines

Dotty uses unsafe code (`AllowUnsafeBlocks=true`) for native interop and performance:

1. **Document unsafe blocks** with clear comments explaining the safety invariants
2. **Minimize unsafe scope** - Keep unsafe code blocks as small as possible
3. **Validate inputs** before entering unsafe code
4. **Use `fixed` statements** properly with pinned references

### Source Generator Guidelines

When working with `Dotty.Config.SourceGenerator`:

1. Follow the existing emitter patterns in `Emission/` folder
2. Use `StringBuilder` efficiently for code generation
3. Add diagnostic messages for configuration errors (see `Diagnostics/`)
4. Update `AnalyzerReleases.Shipped.md` for new analyzer versions

## Project Structure

### Architecture Layers

```
┌─────────────────────────────────────────────┐
│           Dotty.App (UI Layer)              │
│     Avalonia-based GUI application          │
├─────────────────────────────────────────────┤
│         Dotty.Terminal (Core Layer)         │
│   Terminal engine, parsers, buffers         │
├─────────────────────────────────────────────┤
│        Dotty.NativePty (Native Layer)       │
│    POSIX PTY helper (C + C# wrapper)        │
├─────────────────────────────────────────────┤
│      Dotty.Abstractions (Contracts)         │
│    Shared interfaces, zero dependencies     │
└─────────────────────────────────────────────┘
```

### Key Directories

| Directory | Purpose |
|-----------|---------|
| `src/Dotty.App/` | Avalonia application, views, view models |
| `src/Dotty.Terminal/` | Terminal buffer, ANSI parser, rendering logic |
| `src/Dotty.NativePty/` | C pty-helper and C# bindings |
| `src/Dotty.Abstractions/` | Interfaces, config contracts, theme definitions |
| `src/Dotty.Config.SourceGenerator/` | Roslyn source generator for config |
| `tests/Dotty.App.Tests/` | xUnit tests, headless UI tests |
| `docs/` | Architecture documentation, guides |
| `artifacts/perf/` | Benchmark harnesses |

## Pull Request Process

### Before Submitting

1. **Ensure tests pass**:
   ```bash
   dotnet test Dotty.sln
   ```

2. **Check code formatting**:
   ```bash
   dotnet format --verify-no-changes Dotty.sln
   ```

3. **Update documentation** if your changes affect:
   - Public APIs (update relevant docs in `docs/`)
   - Configuration options
   - Build/development process

4. **Add tests** for new functionality or bug fixes

### PR Checklist

- [ ] Code builds without warnings (`dotnet build -c Release`)
- [ ] All tests pass (`dotnet test`)
- [ ] Code follows style guidelines
- [ ] Documentation updated (if applicable)
- [ ] Commit messages are clear and descriptive
- [ ] PR description explains the "why" and "what"

### Commit Message Guidelines

Follow conventional commit format:

```
type(scope): description

[optional body]

[optional footer]
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `style`: Code style changes (formatting, semicolons, etc.)
- `refactor`: Code refactoring
- `perf`: Performance improvements
- `test`: Adding or updating tests
- `chore`: Build process, dependencies, etc.

**Examples:**
```
feat(terminal): add support for bracketed paste mode

fix(parser): handle malformed OSC sequences without crashing

docs(readme): update build instructions for macOS
```

### Review Process

1. All PRs must pass CI checks (build + tests on Ubuntu, Windows, macOS)
2. At least one maintainer approval is required
3. Address review feedback promptly
4. Keep PRs focused - one logical change per PR

## Development Resources

### Documentation

| Document | Description |
|----------|-------------|
| [docs/Architecture.md](docs/Architecture.md) | Architectural overview |
| [docs/rendering.md](docs/rendering.md) | Rendering system details |
| [docs/parsing.md](docs/parsing.md) | ANSI/VT parser implementation |
| [docs/Testing.md](docs/Testing.md) | Testing strategy and patterns |
| [docs/guides/configuration.md](docs/guides/configuration.md) | Configuration system guide |
| [docs/architecture/config-source-generator.md](docs/architecture/config-source-generator.md) | Source generator architecture |

### External References

- [Avalonia UI Documentation](https://docs.avaloniaui.net/)
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [POSIX Pseudo-Terminals](https://pubs.opengroup.org/onlinepubs/9699919799/functions/openpty.html)
- [XTerm Control Sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)

## Troubleshooting

### Common Build Issues

#### "No precompiled XAML found" error

**Solution**: Ensure Avalonia SDK is properly referenced. Clean and rebuild:
```bash
dotnet clean Dotty.sln
dotnet build Dotty.sln
```

#### Native PTY helper not found (Linux/macOS)

**Solution**: Build the native helper:
```bash
cd src/Dotty.NativePty && make && cd ../..
```

#### Tests fail on Windows with Unix-specific tests

**Solution**: This is expected. Use the filter:
```bash
dotnet test Dotty.sln --filter "FullyQualifiedName!~Unix"
```

### Getting Help

- **GitHub Issues**: [github.com/dominic-codespoti/dotty/issues](https://github.com/dominic-codespoti/dotty/issues)
- **Discussions**: Use GitHub Discussions for questions and ideas

---

## License

By contributing to Dotty, you agree that your contributions will be licensed under the MIT License. See [License.md](License.md) for details.

Thank you for contributing to Dotty!
