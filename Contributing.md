# Contributing to Dotty

Thanks for your interest in contributing. This guide covers local development; the [documentation index](docs/index.md) links to the detailed architecture, platform, testing, and release references.

## Prerequisites

- .NET SDK 10.0.100 or a later .NET 10 feature-band SDK. The repository's [global.json](global.json) pins 10.0.100 and allows roll-forward within .NET 10; .NET 9 is not sufficient for the net10.0 projects.
- On Linux and macOS, a C compiler and `make` to build the POSIX PTY helper.
- Git.
- For running the desktop application, a supported desktop environment and OpenGL 3.3-capable driver. Headless builds and tests do not demonstrate physical GUI behavior; see [native desktop verification](docs/NativeDesktopAndIme.md).

Install .NET 10 from the [official download page](https://dotnet.microsoft.com/download/dotnet/10.0). On Windows, no separate PTY helper is needed; the app uses ConPTY.

## Build and run

Run commands from the repository root. On Linux or macOS, build the native helper, then build the solution:

```sh
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release
```

For iterative development, use `-c Debug`. To launch the app from source:

```sh
dotnet run --project src/Dotty/Dotty.csproj
```

Pass application arguments after `--`, for example:

```sh
dotnet run --project src/Dotty/Dotty.csproj -- -d "$PWD" -- nvim .
```

Release configuration enables Native AOT for applicable targets. Publishing and supported release targets are described in the [release policy](docs/Releasing.md) and [platform support reference](docs/PlatformSupport.md); a successful build is not proof of physical desktop support.

## Tests

The repository uses Microsoft.Testing.Platform via global.json. Run the suite with the .NET 10 SDK:

```sh
# Run all solution test projects
dotnet test --solution Dotty.slnx -c Release

# Run one project
dotnet test --project tests/Dotty.App.Tests/Dotty.App.Tests.csproj -c Release
```

CI runs test projects individually. For a CI-style TRX report, use a unique report filename:

```sh
dotnet test --project tests/Dotty.App.Tests/Dotty.App.Tests.csproj -c Release --no-build --report-xunit-trx --report-xunit-trx-filename app-tests.trx
```

If a test command reports zero tests, check whether dotnet on PATH is a wrapper injecting raw MSBuild switches into dotnet test; use the installed .NET SDK executable directly to diagnose. See the [testing guide](docs/Testing.md) for test scope and additional procedures. The performance test project is a benchmark executable, not one of the test projects. Benchmark results are workload- and machine-specific; see [Performance](docs/Performance.md) for recorded evidence and its limits.

## Code and review practices

- Match existing conventions: four-space indentation, braces on control structures, PascalCase for public types/members, and camelCase for locals. Keep changes focused and preserve neighboring code style.
- Treat allocations and copies in hot paths deliberately. Use spans or other allocation-conscious patterns only where appropriate; measure relevant workloads instead of assuming a change is faster.
- Use unsafe code only when required for interop or measured performance, and keep pointer operations bounded by explicit lifetime and ownership guarantees.
- Include tests for behavior changes where practical. Run the affected tests and build; report exact commands and platform limitations.
- Update the relevant user or developer documentation when behavior or commands change. Do not describe unimplemented work as available.

Before opening a pull request, inspect the diff, run relevant checks, and note any platform coverage limitations. CI is the authoritative cross-platform validation. Real-user checks—focus, physical keyboard/layout, pointer, clipboard, compositor/window-manager decoration, HiDPI, and sleep/resume—require native interactive desktop lanes and are not covered by ordinary headless tests; see [Native Desktop and IME](docs/NativeDesktopAndIme.md).

## Project map

- `src/Dotty/` — desktop application host.
- `src/Dotty.Terminal/` — terminal core.
- `src/Dotty.Runtime/`, `src/Dotty.Rendering.Gpu/`, `src/Dotty.Abstractions/` — runtime, GPU rendering, and shared contracts.
- `src/Dotty.NativePty/` — POSIX helper and native interop.
- `tests/` — test projects and the separate performance benchmark executable.
- `docs/` — user guides, technical references, design history, and benchmark documentation.

See [Architecture](docs/Architecture.md) for system design and [docs/index.md](docs/index.md) for the documentation catalog.

## Pull requests

1. Keep each change focused and describe the problem and intended behavior.
2. Include relevant tests and report the exact commands run.
3. Identify platform and physical-GUI coverage limitations.
4. Update relevant documentation and avoid claims beyond what was implemented and verified.

All contributions are licensed under the MIT License; see [License.md](License.md). Report bugs or request changes via [GitHub Issues](https://github.com/dominic-codespoti/dotty/issues).
