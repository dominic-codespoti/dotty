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
variables, a shellIsExecutable flag, and the initial text-area size in physical
pixels. Resize takes the grid size and the pixel size. See the complete current
declaration in [IPty.cs](../src/Dotty.Abstractions/Pty/IPty.cs); the excerpt below
summarizes its method shapes:

```csharp
void Start(
    string? shell = null,
    int columns = 80,
    int rows = 24,
    string? workingDirectory = null,
    IDictionary<string, string>? environmentVariables = null,
    IReadOnlyList<string>? command = null,
    bool shellIsExecutable = false,
    int initialPixelWidth = 0,
    int initialPixelHeight = 0);
void Resize(int columns, int rows, int pixelWidth, int pixelHeight);
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

### Environment contract

`TerminalSession` builds the environment passed to `IPty.Start` on every backend: `TERM_PROGRAM=dotty`, `TERM_PROGRAM_VERSION` (release version, SemVer build metadata stripped), and `DOTTY_SURFACE_ID` (a per-session identifier that is also the owning pane's `Id`) first, then the caller's variables, which win on a name clash. The caller's dictionary is not modified; names compare case-insensitively on Windows and case-sensitively elsewhere. See [Configuration](Configuration.md#session-launch-context).

On Unix the host passes the helper its own protocol variables, `DOTTY_CONTROL_SOCKET`, `DOTTY_INITIAL_COLS`, `DOTTY_INITIAL_ROWS`, `DOTTY_INITIAL_XPIXEL`, and `DOTTY_INITIAL_YPIXEL`. The helper consumes them and calls `unsetenv` on all five after closing its descriptors and immediately before `exec`, so the user's shell or command never sees them. `UnixPty_DoesNotLeakHelperProtocolVariablesIntoCommand` covers this on Unix runners; the Windows ConPTY backend has no helper and never sets them.

### Pixel sizes

Graphics clients size images from the pixel size of the terminal, so the PTY
carries it next to the grid size:

- `IPty.Start(..., initialPixelWidth, initialPixelHeight)` and
  `IPty.Resize(columns, rows, pixelWidth, pixelHeight)`; `0` means unknown.
  `TerminalTabManager` computes the size of a new tab from the latest layout's cell
  size times the grid (unknown, so `0`, before the first layout) and
  `TerminalSession.Resize` forwards a pixel-only change (zoom, DPI) even when the
  grid is unchanged.
- On Unix the helper receives `DOTTY_INITIAL_XPIXEL`/`DOTTY_INITIAL_YPIXEL`
  (clamped to `0..65535`, the `unsigned short` of `struct winsize`) and fills
  `ws_xpixel`/`ws_ypixel`, so the child's first `TIOCGWINSZ` already reports them.
  Later resizes use the control-socket line
  `{"type":"resize","cols":N,"rows":M,"xpixel":W,"ypixel":H}`; `xpixel` and
  `ypixel` are optional for older senders and default to `0`.
- ConPTY has no pixel channel: `WindowsPty` ignores the pixel arguments. See
  [Windows ConPTY](WindowsConPty.md#kitty-graphics-passthrough).
- `PtyFactory.Create(windowsGraphicsPassthrough)` carries the experimental
  `graphics.windowsPassthrough` flag to `WindowsPty`; it is ignored on Unix.

Whether child programs see the right values on every platform `needs runtime
verification` (`UnixPtyPixelTests` covers it on Unix runners).

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
