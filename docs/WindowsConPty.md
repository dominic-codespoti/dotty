# Windows ConPTY Support

Dotty integrates Windows Pseudo Console (ConPTY) through the WindowsPty backend
in src/Dotty.NativePty/Windows/WindowsPty.cs. This is a current implementation
overview; [Native PTY Integration](NativePty.md) and [Platform Support](PlatformSupport.md)
are the canonical references for backend behavior and supported targets.

## Requirements and support

ConPTY requires Windows 10 build 17763 (version 1809) or later. Dotty's promoted
Windows release target is x64; Windows arm64 is a build-only candidate, not a
promoted release target. Source builds use the .NET 11 SDK, and the desktop host
requires a working OpenGL 3.3 driver. See the current [support matrix](PlatformSupport.md#support-matrix).

## Backend implementation

WindowsPty creates anonymous pipes and a pseudoconsole with CreatePseudoConsole,
then starts the child process using the PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
attribute. ResizePseudoConsole propagates grid-size changes. The implementation
wraps ConPTY's synchronous pipe handles in synchronous FileStream instances and
cleans up the process and native handles during shutdown.

WindowsPty implements the shared [IPty contract](../src/Dotty.Abstractions/Pty/IPty.cs).
The host selects it through PtyFactory; application output, including ANSI/VT
sequences, is consumed by Dotty's terminal parser rather than a Windows Terminal
parser. ConPTY supplies the pseudoterminal process and streams.

Child environments preserve inherited variables and apply caller overrides
case-insensitively, without modifying the caller's dictionary. As on Unix,
`TERM=xterm-256color` and `COLORTERM=truecolor` are then forced to advertise
Dotty's terminal capabilities, even when the parent or caller supplies
`TERM=dumb`. This allows shells and prompts to enable their VT/color support.

The Unicode environment block is sorted case-insensitively and double-NUL
terminated. Its allocation uses `Marshal.StringToHGlobalUni`, paired with
`Marshal.FreeHGlobal` after `CreateProcess`, including failed launches.

## Build and verification

Build the solution on Windows with the .NET 11 SDK:

```powershell
dotnet build Dotty.slnx -c Release --nologo
dotnet run --project src\Dotty\Dotty.csproj -c Release
```

The CI Windows job exercises WindowsPty and ConPTY smoke coverage. Consult the
[Testing guide](Testing.md) for current test-runner commands and native coverage;
consult [E2E Testing](E2ETesting.md) for the host control smoke scope.
Manual desktop checks still require an interactive Windows session with a usable
OpenGL 3.3 driver; hosted build/test success is not desktop GUI evidence.

## Historical integration notes

Earlier development notes recorded failures involving ConPTY attribute-list
marshalling, treating synchronous pipe handles as asynchronous streams, empty
shell environment values, and propagation of the initial terminal size. These
are historical troubleshooting records, not current diagnostic instructions or
an assertion that the old workarounds should be applied to this implementation.
For a present-day failure, use the current source and test coverage, then inspect
PtyFactory capability diagnostics and host logs. The earlier checklist also
contained a DOTTY_DEBUG setting, but the Windows PTY implementation does not use
that variable; do not rely on it for diagnostics.

## External references

- [Microsoft ConPTY documentation](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Native PTY Integration](NativePty.md)
- [Platform Support](PlatformSupport.md)
