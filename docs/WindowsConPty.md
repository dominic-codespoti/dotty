# Windows ConPTY Support

Dotty now supports Windows 10 version 1809 (build 17763) and later through the Windows Console Pseudo Terminal (ConPTY) API.

## Overview

Windows ConPTY provides a pseudo-terminal implementation that allows Dotty to:
- Spawn console applications (cmd.exe, PowerShell, pwsh.exe) with full terminal emulation
- Handle ANSI escape sequences natively through Windows Terminal's infrastructure
- Support resize operations, color output, and mouse events
- Use standard .NET Streams for I/O operations

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Windows 11 (all versions supported)
- .NET 10.0 or later

## Architecture

```
┌─────────────────────────────────────────┐
│           Dotty.App (Avalonia)          │
├─────────────────────────────────────────┤
│        TerminalSession (ViewModel)      │
├─────────────────────────────────────────┤
│     Dotty.NativePty (new project)     │
│  ┌─────────────────┐ ┌────────────────┐ │
│  │   WindowsPty    │ │    UnixPty     │ │
│  │  (ConPTY API)   │ │ (pty-helper.c) │ │
│  └─────────────────┘ └────────────────┘ │
└─────────────────────────────────────────┘
```

## Key Components

### 1. IPty Interface (`Dotty.Abstractions/Pty/IPty.cs`)

Common interface for all PTY implementations:

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

### 2. WindowsPty Implementation (`Dotty.NativePty/Windows/WindowsPty.cs`)

Uses Windows ConPTY APIs:
- `CreatePseudoConsole` - Creates the pseudo console
- `ResizePseudoConsole` - Handles terminal resize
- `CreateProcess` with `EXTENDED_STARTUPINFO_PRESENT` and `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` - Attaches process to PTY

### 3. UnixPty Implementation (`Dotty.NativePty/Unix/UnixPty.cs`)

Wraps the existing C-based pty-helper process for Linux/macOS.

### 4. PtyFactory (`Dotty.NativePty/PtyFactory.cs`)

Factory pattern for creating platform-appropriate PTY instances:

```csharp
var pty = PtyFactory.Create();  // Returns WindowsPty on Windows, UnixPty on Unix
```

## Building on Windows

### Prerequisites

1. Visual Studio 2022 or .NET 10.0 SDK
2. Windows 10/11 SDK

### Build Steps

```powershell
# Clone the repository
git clone https://github.com/dominic-codespoti/dotty.git
cd dotty

# Build the solution
dotnet build Dotty.sln

# Run the application
dotnet run --project src/Dotty.App/Dotty.App.csproj
```

## Testing

### Manual Testing Checklist

1. **Basic Shell Launch**
   - [ ] cmd.exe launches successfully
   - [ ] Windows PowerShell (powershell.exe) launches successfully
   - [ ] PowerShell Core (pwsh.exe) launches successfully

2. **Input/Output**
   - [ ] Keyboard input appears correctly
   - [ ] Command output displays correctly
   - [ ] Special characters render properly

3. **Resize**
   - [ ] Window resize triggers PTY resize
   - [ ] Running commands like `top` or `vim` adapt to new size
   - [ ] No visual artifacts after resize

4. **Color Output**
   - [ ] ANSI color codes render correctly
   - [ ] 256 colors work
   - [ ] True color (24-bit) works
   - [ ] Bold, italic, underline attributes work

5. **Interactive Applications**
   - [ ] `vim` or `nano` editors work
   - [ ] `htop` or `top` system monitors work
   - [ ] `less` pager works
   - [ ] Mouse events in supported applications

6. **Process Management**
   - [ ] Shell exit is detected
   - [ ] Process cleanup on window close
   - [ ] Force kill works for hung processes

### Automated Testing

Create a test script `test-conpty.ps1`:

```powershell
# Test Windows ConPTY implementation
param(
    [Parameter()]
    [string]$DottyPath = ".\src\Dotty.App\bin\Debug\net10.0\Dotty.App.exe"
)

# Check OS version
$osVersion = [System.Environment]::OSVersion.Version
if ($osVersion.Build -lt 17763) {
    Write-Error "Windows build $($osVersion.Build) does not support ConPTY. Requires build 17763 or later."
    exit 1
}

# Test platform detection
Add-Type -Path ".\src\Dotty.Abstractions\bin\Debug\net10.0\Dotty.Abstractions.dll"
Add-Type -Path ".\src\Dotty.NativePty\bin\Debug\net10.0\Dotty.NativePty.dll"

$platform = [Dotty.Abstractions.Pty.PtyPlatform]
Write-Host "IsWindows: $($platform::IsWindows)"
Write-Host "IsConPtySupported: $($platform::IsConPtySupported)"
Write-Host "Default Shell: $($platform::GetDefaultShell())"

# Test PTY creation
$factory = [Dotty.NativePty.PtyFactory]
Write-Host "IsSupported: $($factory::IsSupported)"

if (-not $factory::IsSupported) {
    Write-Error "PTY not supported: $($factory::GetUnsupportedReason())"
    exit 1
}

Write-Host "All tests passed!"
```

## Debugging

### Enable Debug Logging

Set environment variable before launching:
```powershell
$env:DOTTY_DEBUG = "1"
dotnet run --project src/Dotty.App
```

### Common Issues

#### Issue: "ConPTY is not supported on this Windows version"

**Cause**: Windows version is too old (pre-1809).

**Solution**: Upgrade to Windows 10 version 1809 or later, or use Windows 11.

#### Issue: "Failed to create pseudo console"

**Cause**: Usually indicates Windows corruption or missing system files.

**Solution**: 
1. Run Windows Update
2. Run `sfc /scannow` and `DISM` repair commands
3. Check Windows Console/Terminal Services are running

#### Issue: App exits immediately with code `0xC0000005`

**Cause**: Older ConPTY interop code can crash during `UpdateProcThreadAttribute`
if the pseudo console handle attribute is marshaled incorrectly.

**Solution**:
1. Update to a build that includes the ConPTY attribute list fix in `WindowsPty`
2. Rebuild `Dotty.NativePty` on Windows so `WINDOWS`-guarded ConPTY code is compiled

#### Issue: "Handle does not support asynchronous operations" on startup

**Cause**: Windows `CreatePipe` returns synchronous handles. Wrapping those handles
as async `FileStream` instances throws this exception.

**Solution**:
1. Use synchronous `FileStream` wrappers for ConPTY pipe handles
2. Rebuild and rerun Dotty

#### Issue: Terminal opens but shows no prompt and ignores input

**Cause**: Usually one of these ConPTY integration issues:
1. Incorrect `CreatePipe` interop signature (security attributes not passed by reference)
2. Incorrect `UpdateProcThreadAttribute` payload for `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`
3. Empty `DOTTY_SHELL`/`SHELL` environment values being treated as a valid shell command

**Solution**:
1. Ensure `CreatePipe` passes `SECURITY_ATTRIBUTES` by reference
2. Pass `HPCON` directly to `UpdateProcThreadAttribute` with `cbSize = IntPtr.Size`
3. Treat empty or whitespace shell environment values as unset and fall back to platform default
4. Verify that `powershell.exe` (or chosen shell) appears as a child process of `Dotty.App.exe`

#### Issue: Prompt text appears in old output rows until window is maximized

**Cause**: The PTY can start at default dimensions (for example 80x24), while the
actual first UI size is different. If that first resize is not propagated to ConPTY,
PowerShell cursor math (PSReadLine) can drift and write input on stale rows.

**Solution**:
1. Ensure the first UI resize is sent to PTY when it differs from startup size
2. Ensure terminal replies to cursor position queries (`CSI 6n` / `CSI ? 6n`) are
   routed back to PTY input so the shell can track cursor location correctly

#### Issue: Shell doesn't start or crashes immediately

**Cause**: The specified shell executable might not exist or have permission issues.

**Solution**:
1. Check shell path exists: `Test-Path "C:\Windows\System32\cmd.exe"`
2. Try different shells to isolate the issue
3. Check Windows Event Viewer for crash details

#### Issue: Resize doesn't work

**Cause**: Some legacy console applications don't handle resize signals.

**Solution**: This is expected behavior for some older Windows console applications. Modern shells (PowerShell, Windows Terminal) handle resize correctly.

## Performance Considerations

1. **Buffer Sizes**: WindowsPty uses 4KB-128KB buffers for I/O, optimized for interactive use
2. **Async I/O**: All operations use async/await to prevent UI blocking
3. **Memory Pooling**: Uses `ArrayPool<byte>` to reduce GC pressure during high-volume output

## Security Considerations

1. **Process Isolation**: Each terminal tab runs in a separate process
2. **Handle Security**: Pipe handles are properly secured with inheritance flags
3. **No Elevation Required**: Standard user privileges work for most shells

## Future Improvements

1. Windows Terminal integration for shared settings
2. Windows clipboard integration for copy/paste
3. Acrylic/Mica background effects on Windows 11
4. Tab tear-out to new windows (when Avalonia supports it)

## References

- [Windows ConPTY Documentation](https://docs.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Microsoft Terminal ConPTY Sample](https://github.com/microsoft/terminal/tree/main/samples/ConPTY)
- [Windows Console API](https://docs.microsoft.com/en-us/windows/console/console-functions)
