# Native PTY Integration

Dotty uses one `IPty` contract with a native backend selected at runtime:

```text
Dotty host (Silk.NET/OpenGL)
        │
TerminalSession
        │
Dotty.NativePty
   ┌────┴────┐
UnixPty   WindowsPty
   │          │
pty-helper  Windows ConPTY
```

The terminal core is platform-neutral. Native PTY startup, resize, process
cleanup, and artifact provisioning are platform-specific.

## Common Interface: IPty

All PTY implementations implement the `IPty` interface defined in `Dotty.Abstractions`:

```csharp
public interface IPty : IDisposable
{
    bool IsRunning { get; }
    int ProcessId { get; }
    Stream? OutputStream { get; }
    Stream? InputStream { get; }
    
    void Start(string? shell, int columns, int rows, ...);
    void Resize(int columns, int rows);
    void Kill(bool force = false);
    Task<int> WaitForExitAsync(CancellationToken token);
}
```

## Platform Implementations

### Unix Implementation (Linux/macOS)

**File**: `src/Dotty.NativePty/Unix/UnixPty.cs`

`UnixPty` launches the packaged `pty-helper` executable, redirects its standard
streams, and sends resize messages over a Unix-domain socket.

**Helper**: `src/Dotty.NativePty/pty-helper.c`

The POSIX helper uses `posix_openpt`, `grantpt`, `unlockpt`, `fork`, `setsid`,
and a controlling-terminal ioctl. It proxies PTY I/O and accepts resize
messages over a Unix-domain socket.

### Windows Implementation

**File**: `src/Dotty.NativePty/Windows/WindowsPty.cs`

`WindowsPty` uses Windows ConPTY APIs directly:

1. Create anonymous PTY pipes.
2. Call `CreatePseudoConsole`.
3. Attach the child with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
4. Resize with `ResizePseudoConsole`.
5. Close the process and native handles deterministically.


## Factory Pattern

**File**: `src/Dotty.NativePty/PtyFactory.cs`

Platform detection and instance creation:

```csharp
// Automatic platform detection
var pty = PtyFactory.Create();

// Check platform support
if (PtyFactory.IsSupported) {
    var pty = PtyFactory.CreateAndStart(shell: "pwsh.exe", columns: 120, rows: 30);
}
```

## Platform Support

| Platform | Release target | Build-only target | Backend |
|---|---|---|---|
| Linux | x64 | arm64 | POSIX helper |
| macOS | x64, arm64 | — | POSIX helper |
| Windows 10 build 17763+ / Windows 11 | x64 | arm64 | ConPTY |

The factory reports the runtime identifier, architecture, selected backend,
and missing native dependencies through `PtyCapabilities`.

## Building

### Linux/macOS

```bash
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release
```

### Windows

No separate native helper build is required. Build on Windows so the
`WINDOWS` compilation constant includes the ConPTY implementation:

```powershell
dotnet build Dotty.slnx -c Release
```

## Migration from Legacy Code

The older host directly managed the pty-helper process. The current architecture:

1. **Extracts PTY logic** into the `Dotty.NativePty` project.
2. **Uses `IPty`** for cross-platform abstraction.
3. **Lets `TerminalSession`** use `PtyFactory.Create()` instead of direct process management.
4. **Reports capabilities** when the platform or native dependency is unavailable.

## Security Considerations

1. **Unix**: Socket path is unique per session (includes GUID)
2. **Windows**: Process handles are properly secured and closed
3. **Both**: Process isolation ensures crashes don't affect the UI

## Debugging

### Unix
```bash
# Run with debug output
DOTTY_CONTROL_SOCKET=/tmp/dotty-debug.sock ./pty-helper /bin/bash

# Test resize manually
echo '{"type":"resize","cols":100,"rows":30}' | nc -U /tmp/dotty-debug.sock
```

### Windows
```powershell
# Check ConPTY support
[Environment]::OSVersion.Version.Build  # Should be >= 17763

# Enable verbose logging (if implemented)
$env:DOTTY_DEBUG_PTY = "1"
dotnet run --project src/Dotty/Dotty.csproj
```

## References

- [Windows ConPTY Documentation](https://docs.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [POSIX PTY functions](https://man7.org/linux/man-pages/man3/posix_openpt.3.html)
- [Dotty Windows ConPTY Guide](./WindowsConPty.md)
