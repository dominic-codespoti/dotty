# Native PTY Integration

Dotty uses the platform-neutral IPty contract from Dotty.Abstractions. Runtime
selection is implemented by PtyFactory in src/Dotty.NativePty/PtyFactory.cs.

The platform-specific backends are:

| Platform | Backend | Release target |
|---|---|---|
| Linux | POSIX pty-helper process, controlled over a Unix-domain socket | x64 |
| macOS | POSIX pty-helper process, controlled over a Unix-domain socket | x64, arm64 |
| Windows 10 build 17763+ / Windows 11 | Windows ConPTY API | x64 |

Linux arm64 and Windows arm64 are build-only candidates, not promoted release
targets. See [Platform Support](PlatformSupport.md) and the
[Release Policy](Releasing.md) for the current matrix.

## IPty contract

IPty exposes process state and streams, a ProcessExited event, and operations to
start, resize, terminate, and await the child process. Start accepts an optional
shell or command argv, initial dimensions, working directory, environment
variables, and a shellIsExecutable flag. See the complete current declaration
in [IPty.cs](../src/Dotty.Abstractions/Pty/IPty.cs); the excerpt below summarizes
its method shapes:

```csharp
void Start(
    string? shell = null,
    int columns = 80,
    int rows = 24,
    string? workingDirectory = null,
    IDictionary<string, string>? environmentVariables = null,
    IReadOnlyList<string>? command = null,
    bool shellIsExecutable = false);
void Resize(int columns, int rows);
void Kill(bool force = false);
Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
```

The convenient factory method accepts the shell executable, dimensions, working
directory, and optional environment variables:

```csharp
var pty = PtyFactory.CreateAndStart(shell: "/bin/bash", columns: 120, rows: 30);
```

## Unix backend

UnixPty launches the packaged pty-helper and redirects its standard streams.
The helper allocates a pseudoterminal with posix_openpt/grantpt/unlockpt,
creates the child session and controlling terminal, proxies terminal I/O, and
accepts resize JSON over a Unix-domain socket. Published Unix apps resolve the
helper beside the application before development locations or PATH. See the
[helper README](../src/Dotty.NativePty/Readme.md) for its direct invocation and
build instructions.

Build the helper and app from the repository root:

```sh
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release
```

## Windows backend

WindowsPty uses ConPTY directly:

1. Create anonymous pseudoconsole pipes.
2. Call CreatePseudoConsole.
3. Attach the child through PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
4. Resize with ResizePseudoConsole.
5. Close process and native handles during cleanup.

No Unix helper is required on Windows. Build the solution on Windows so the
WindowsPty implementation is included in the WINDOWS compilation.

## Capability diagnostics

PtyFactory.GetCapabilities reports the derived runtime identifier,
architecture, selected backend, native dependency availability, and an
explanation when PTY startup is unavailable. Run the native PTY test project on
the host platform to exercise native startup and cleanup; see [Testing](Testing.md).

## Historical extraction

The former host directly managed the pty-helper process. PTY ownership was
subsequently extracted into Dotty.NativePty behind IPty, with TerminalSession
using PtyFactory. This describes the completed migration; for the current
architecture see [Architecture](Architecture.md).

## External references

- [Windows ConPTY documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [POSIX posix_openpt manual](https://man7.org/linux/man-pages/man3/posix_openpt.3.html)
- [Windows ConPTY guide](WindowsConPty.md)
