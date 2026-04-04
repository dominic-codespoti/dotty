# Native PTY Integration

Dotty now supports both Unix (Linux/macOS) and Windows platforms through a unified abstraction layer.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Dotty.App (Avalonia)                     │
├─────────────────────────────────────────────────────────────┤
│              TerminalSession (ViewModel)                    │
├─────────────────────────────────────────────────────────────┤
│         Dotty.NativePty (Cross-Platform PTY)              │
│  ┌─────────────────────┐      ┌─────────────────────────┐   │
│  │     UnixPty         │      │      WindowsPty         │   │
│  │  (Managed Wrapper)  │      │    (ConPTY API)         │   │
│  └──────────┬──────────┘      └──────────┬──────────────┘   │
│             │                            │                  │
│  ┌──────────▼──────────┐      ┌──────────▼──────────────┐   │
│  │   pty-helper.c      │      │  kernel32.dll P/Invoke  │   │
│  │   (forkpty-based)   │      │  CreatePseudoConsole    │   │
│  └─────────────────────┘      └─────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

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

The Unix implementation wraps the existing C-based `pty-helper` process:

1. **Process**: Starts `pty-helper` binary with the desired shell
2. **I/O**: Uses redirected stdin/stdout streams
3. **Resize**: Communicates via Unix domain socket (`DOTTY_CONTROL_SOCKET`)
4. **Cleanup**: Handles process termination and socket cleanup

**C Helper**: `src/Dotty.NativePty/pty-helper.c`

The native C helper:
- Uses `posix_openpt()` and `forkpty()` for PTY creation
- Spawns the shell as a child process
- Proxies PTY master ↔ stdin/stdout
- Listens on Unix socket for resize JSON messages
- Handles `SIGINT`, `SIGTERM`, `SIGHUP` for cleanup

### Windows Implementation

**File**: `src/Dotty.NativePty/Windows/WindowsPty.cs`

The Windows implementation uses the ConPTY API directly via P/Invoke:

1. **Pipes**: Creates anonymous pipes for PTY I/O
2. **ConPTY**: Calls `CreatePseudoConsole()` with pipe handles
3. **Process**: Uses `CreateProcess()` with `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`
4. **Resize**: Calls `ResizePseudoConsole()` directly
5. **Cleanup**: Closes handles and pseudo console reference

**Key Windows APIs**:
- `CreatePseudoConsole` - Creates the pseudo console
- `ResizePseudoConsole` - Resizes the PTY
- `ClosePseudoConsole` - Cleans up the PTY
- `InitializeProcThreadAttributeList` / `UpdateProcThreadAttribute` - Sets up ConPTY attribute

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

| Platform | Min Version | Implementation | Status |
|----------|-------------|----------------|--------|
| Linux    | Any modern  | pty-helper.c   | ✅ Supported |
| macOS    | Any modern  | pty-helper.c   | ✅ Supported |
| Windows  | 10 (1809)   | ConPTY API     | ✅ Supported |
| Windows  | < 10 (1809) | -              | ❌ Not Supported |

## Building

### Linux/macOS

```bash
cd src/Dotty.NativePty
make
```

### Windows

No separate native build required - uses P/Invoke to system APIs.

```powershell
dotnet build src/Dotty.NativePty/Dotty.NativePty.csproj
dotnet build src/Dotty.App/Dotty.App.csproj
```

## Migration from Legacy Code

The old `TerminalSession.cs` directly managed the pty-helper process. The new architecture:

1. **Extracted PTY logic** into `Dotty.NativePty` project
2. **Created IPty interface** for cross-platform abstraction
3. **TerminalSession now** uses `PtyFactory.Create()` instead of direct process management
4. **Platform detection** is automatic and cached

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
dotnet run --project src/Dotty.App
```

## References

- [Windows ConPTY Documentation](https://docs.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [POSIX forkpty](https://man7.org/linux/man-pages/man3/forkpty.3.html)
- [Dotty Windows ConPTY Guide](./WindowsConPty.md)
