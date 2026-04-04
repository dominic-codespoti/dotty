# Architectural Overview

Dotty employs a **strictly decoupled, domain-driven architecture** designed for high performance and zero-allocation hot paths. The codebase is broken down into four distinct layers with clear separation of concerns, enabling maintainability, testability, and platform flexibility.

## Table of Contents

1. [High-Level System Architecture](#high-level-system-architecture)
2. [Component Diagram/Description](#component-diagramdescription)
3. [Data Flow Between Components](#data-flow-between-components)
4. [Layered Architecture](#layered-architecture)
5. [Plugin/Extensibility Points](#pluginextensibility-points)
6. [Configuration System Integration](#configuration-system-integration)
7. [Platform Abstraction Layer](#platform-abstraction-layer)
8. [Source File References](#source-file-references)

---

## High-Level System Architecture

Dotty follows a **layered architecture pattern** with strict dependency rules. Dependencies always flow downward, ensuring that core abstractions remain independent of implementation details.

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Dotty Terminal Emulator                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                           PRESENTATION LAYER                             │ │
│  │                         (Dotty.App - Avalonia)                          │ │
│  │                                                                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │ │
│  │  │  Terminal    │  │   Search     │  │    Tab       │  │   Settings  │ │ │
│  │  │   Canvas     │  │   Overlay    │  │   Manager    │  │     UI      │ │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  └─────────────┘ │ │
│  │         │                 │                 │                │          │ │
│  │         └─────────────────┴─────────────────┴────────────────┘          │ │
│  │                                    │                                    │ │
│  │                                    ▼                                    │ │
│  │  ┌─────────────────────────────────────────────────────────────────┐   │ │
│  │  │                     Composition Renderer                         │   │ │
│  │  │              (SkiaSharp + Avalonia Composition)                  │   │ │
│  │  └─────────────────────────────────────────────────────────────────┘   │ │
│  └─────────────────────────────────────────────────────────────────────────┘ │
│                                    │                                          │
│                                    │ Uses                                     │
│                                    ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                          CORE LOGIC LAYER                              │ │
│  │                        (Dotty.Terminal)                                │ │
│  │                                                                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │ │
│  │  │    ANSI      │  │   Terminal   │  │   Screen     │  │   Search    │ │ │
│  │  │   Parser     │──│   Adapter    │──│   Buffer     │──│   Engine    │ │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  └─────────────┘ │ │
│  │         │                 │                 │                │          │ │
│  │         │                 │                 │                │          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │ │
│  │  │    SGR       │  │   Scrollback │  │    Cell      │  │ Hyperlink   │ │ │
│  │  │   Parser     │  │   Manager    │  │    Grid      │  │   Storage   │ │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  └─────────────┘ │ │
│  └─────────────────────────────────────────────────────────────────────────┘ │
│                                    │                                          │
│                                    │ Uses                                     │
│                                    ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                          ABSTRACTIONS LAYER                              │ │
│  │                       (Dotty.Abstractions)                               │ │
│  │                                                                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │ │
│  │  │ ITerminal    │  │   ITerminal  │  │    IPty      │  │  IColorScheme│ │ │
│  │  │   Handler    │  │   Parser     │  │  Interface   │  │  Interface   │ │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  └─────────────┘ │ │
│  │                                                                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐ │ │
│  │  │  IDottyConfig│  │  IKeyBindings│  │ ICursorSettings│  │IWindowDimensions│ │
│  │  │  Interface   │  │  Interface   │  │  Interface   │  │  Interface   │ │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  └─────────────┘ │ │
│  └─────────────────────────────────────────────────────────────────────────┘ │
│                                    │                                          │
│                                    │ Uses (optional)                          │
│                                    ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │                      PLATFORM ADAPTATION LAYER                           │ │
│  │                        (Dotty.NativePty)                                 │ │
│  │                                                                          │ │
│  │  ┌────────────────────────┐    ┌────────────────────────┐             │ │
│  │  │      Unix PTY          │    │     Windows PTY          │             │ │
│  │  │    (UnixPty.cs)        │    │   (WindowsPty.cs)        │             │ │
│  │  │                        │    │                          │             │ │
│  │  │  - posix_openpt()      │    │  - CreatePseudoConsole() │             │ │
│  │  │  - ptsname()           │    │  - ConPTY API            │             │ │
│  │  │  - forkpty()           │    │  - Process creation      │             │ │
│  │  └────────────────────────┘    └────────────────────────┘             │ │
│  │                                                                          │ │
│  │  ┌─────────────────────────────────────────────────────────────────┐   │ │
│  │  │                    PtyFactory                                    │   │ │
│  │  │              (Platform Detection & Creation)                     │   │ │
│  │  └─────────────────────────────────────────────────────────────────┘   │ │
│  └─────────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Dependency Direction

```
Dotty.App ─────────┐
                   │
                   ▼
Dotty.Terminal ────┤
                   │
                   ▼
Dotty.Abstractions─┤
                   │
                   ▼
Dotty.NativePty ───┘
```

---

## Component Diagram/Description

### Key Components

#### 1. TerminalCanvas (Presentation)

```
┌─────────────────────────────────────┐
│         TerminalCanvas              │
├─────────────────────────────────────┤
│  - Avalonia Control                 │
│  - ILogicalScrollable               │
│  - CompositionCustomVisual          │
├─────────────────────────────────────┤
│  Responsibilities:                  │
│  - Viewport management              │
│  - Scroll handling                  │
│  - Render state composition         │
│  - Input event forwarding           │
└─────────────────────────────────────┘
```

#### 2. TerminalBuffer (Core Logic)

```
┌─────────────────────────────────────┐
│         TerminalBuffer              │
├─────────────────────────────────────┤
│  - ScreenManager                    │
│  - CellGrid                         │
│  - ScrollbackRing                   │
│  - CursorController                 │
├─────────────────────────────────────┤
│  Responsibilities:                  │
│  - Cell storage                     │
│  - Scrollback management            │
│  - Cursor positioning               │
│  - Dirty tracking                   │
└─────────────────────────────────────┘
```

#### 3. BasicAnsiParser (Core Logic)

```
┌─────────────────────────────────────┐
│         BasicAnsiParser             │
├─────────────────────────────────────┤
│  - State machine                    │
│  - Sequence handlers                │
│  - UTF-8 decoder                  │
│  - Charset translator             │
├─────────────────────────────────────┤
│  Responsibilities:                  │
│  - Byte stream parsing              │
│  - Escape sequence recognition      │
│  - Handler callback dispatch        │
└─────────────────────────────────────┘
```

#### 4. IPty Implementations (Platform)

```
┌─────────────────────────────────────┐
│           IPty Interface            │
├─────────────────────────────────────┤
│  + Start()                          │
│  + Write()                          │
│  + Read()                           │
│  + Resize()                         │
│  + Kill()                           │
├─────────────────────────────────────┤
│           ▼                         │
│  ┌──────────────┐  ┌──────────────┐ │
│  │   UnixPty    │  │  WindowsPty  │ │
│  │  (POSIX)     │  │   (ConPTY)   │ │
│  └──────────────┘  └──────────────┘ │
└─────────────────────────────────────┘
```

### Component Interactions

```
┌─────────┐    Feed()     ┌─────────┐    Callbacks    ┌─────────┐
│  PTY    │──────────────▶│  Parser │──────────────▶│ Adapter │
│  Input  │ ReadOnlySpan  │         │ ITerminalHandler│         │
└─────────┘               └─────────┘               └────┬────┘
                                                         │
                                                         │ Mutations
                                                         ▼
                                               ┌─────────────────┐
                                               │  TerminalBuffer │
                                               │  - Cell grid    │
                                               │  - Scrollback   │
                                               └────────┬────────┘
                                                        │
                                                        │ Render State
                                                        ▼
                                               ┌─────────────────┐
                                               │  FrameComposer  │
                                               │  - Background   │
                                               │  - Glyphs       │
                                               └────────┬────────┘
                                                        │
                                                        │ GPU Commands
                                                        ▼
                                               ┌─────────────────┐
                                               │ VisualHandler   │
                                               │  - Skia Canvas  │
                                               │  - Draw calls   │
                                               └─────────────────┘
```

---

## Data Flow Between Components

### Input Data Flow (PTY → Screen)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Input Data Flow                                │
└─────────────────────────────────────────────────────────────────────────┘

1. READ PHASE
   ┌─────────┐
   │ PTY     │  Read() → byte[]
   │ Device  │
   └────┬────┘
        │ Raw bytes (UTF-8 + ANSI sequences)
        ▼

2. PARSE PHASE
   ┌───────────────┐
   │ BasicAnsi     │  Feed(ReadOnlySpan<byte>)
   │ Parser        │
   └───────┬───────┘
           │ Span-based parsing
           │ Zero-allocation
           ▼

3. DISPATCH PHASE
   ┌─────────────────────────────────────────────────┐
   │ ITerminalHandler Callbacks                       │
   ├─────────────────────────────────────────────────┤
   │ OnPrint(), OnMoveCursor(), OnEraseDisplay(), etc.│
   └─────────────────────┬───────────────────────────┘
                         │
                         ▼

4. BUFFER MUTATION
   ┌───────────────┐
   │ Terminal      │  Update cells, cursor, scrollback
   │ Adapter       │
   └───────┬───────┘
           │
           ▼

5. STATE UPDATE
   ┌───────────────┐
   │ Terminal      │  SyncRoot lock
   │ Buffer        │  ScrollGeneration++
   │               │  MarkDirty()
   └───────┬───────┘
           │
           ▼

6. RENDER INVALIDATION
   ┌───────────────┐
   │ TerminalCanvas  │  RequestFrame()
   │               │  DispatcherTimer (1ms debounce)
   └───────┬───────┘
           │
           ▼

7. RENDER COMPOSITION
   ┌───────────────┐
   │ Glyph         │  ProcessSlice(5 rows)
   │ Discovery     │  Enqueue new glyphs
   └───────┬───────┘
           │
           ▼
   ┌───────────────┐
   │ FrameComposer │  CollectBackgroundRegions()
   │               │  ClassifyRowCells()
   │               │  BuildRowSpans()
   └───────┬───────┘
           │
           ▼

8. GPU RENDER
   ┌───────────────┐
   │ TerminalVisual│  OnRender()
   │ Handler       │  SkiaSharp canvas
   │               │  DrawRoundRect(), DrawText()
   └───────────────┘
```

### Output Data Flow (Keyboard → PTY)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          Output Data Flow                               │
└─────────────────────────────────────────────────────────────────────────┘

1. INPUT EVENT
   ┌───────────────┐
   │ Avalonia      │  KeyDown event
   │ Input         │
   └───────┬───────┘
           │
           ▼

2. KEY TRANSLATION
   ┌───────────────┐
   │ Key           │  Map to terminal sequences
   │ Translation   │  CSI u, CSI ~, or raw char
   │ Service       │  Handle bracketed paste
   └───────┬───────┘
           │
           ▼

3. PTY WRITE
   ┌───────────────┐
   │ IPty          │  Write(byte[])
   │ Implementation│
   └───────┬───────┘
           │
           ▼

4. PROCESS RECEIVE
   ┌───────────────┐
   │ Shell         │  Process input
   │ Process       │  Generate output
   └───────────────┘
```

### Configuration Data Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                       Configuration Data Flow                             │
└─────────────────────────────────────────────────────────────────────────┘

1. COMPILE TIME
   UserConfig.cs ──▶ ConfigGenerator ──▶ Generated/*.g.cs
   (IDottyConfig)    (Source Generator)   - Config.g.cs
                                        - ColorScheme.g.cs
                                        - KeyBindings.g.cs

2. RUNTIME INITIALIZATION
   Dotty.Generated.Config ──▶ ConfigBridge ──▶ Avalonia Resources
   (static constants)        (Type          - Brushes
                             conversion)    - Fonts
                                              - Thickness

3. CONSUMPTION
   Avalonia Resources ──▶ TerminalCanvas, Controls, Styles
```

---

## Layered Architecture

### Layer 1: Dotty.Abstractions (Foundation)

**Purpose:** Define contracts and interfaces with **zero dependencies**.

| Component | Interface | Description |
|-----------|-----------|-------------|
| Parser | `ITerminalParser` | Feed bytes, attach handler |
| Handler | `ITerminalHandler` | Receive terminal actions |
| PTY | `IPty` | Platform terminal interface |
| Config | `IDottyConfig` | User configuration contract |
| Colors | `IColorScheme` | ANSI color palette |
| Keys | `IKeyBindings` | Key binding definitions |

**Key Characteristics:**
- No external NuGet packages
- No platform-specific code
- Pure C# interfaces and simple types
- Used by all other layers

```csharp
// Example: ITerminalHandler in Abstractions layer
public interface ITerminalHandler
{
    object? Buffer { get; }
    void OnPrint(ReadOnlySpan<char> text);
    void OnMoveCursor(int row, int col);
    void OnEraseDisplay(int mode);
    // ... 40+ methods
}
```

### Layer 2: Dotty.Terminal (Core Logic)

**Purpose:** Headless terminal emulator business logic.

**Key Components:**

```
Dotty.Terminal/
├── Parser/
│   └── BasicAnsiParser.cs         # ANSI/VT parser
├── Adapter/
│   ├── TerminalAdapter.cs         # ITerminalHandler impl
│   ├── Buffer/
│   │   ├── TerminalBuffer.cs      # Main buffer
│   │   ├── ScreenManager.cs       # Primary/Alternate screens
│   │   ├── CellGrid.cs            # 2D cell storage
│   │   └── Cell.cs                # Cell structure
│   ├── SgrParserArgb.cs           # SGR attribute parser
│   ├── SgrColorArgb.cs            # Color handling
│   └── UnicodeWidth.cs            # Character width calc
```

**Design Principles:**
- No UI code (no Avalonia references)
- No OS-specific code
- Thread-safe where required (SyncRoot)
- Deterministic, testable behavior

### Layer 3: Dotty.NativePty (Platform Adaptation)

**Purpose:** Low-level PTY management for each platform.

**Platform Support:**

| Platform | Implementation | Technology |
|----------|----------------|------------|
| Linux | `UnixPty.cs` | POSIX openpt(), forkpty() |
| macOS | `UnixPty.cs` | POSIX openpt(), forkpty() |
| Windows | `WindowsPty.cs` | Windows ConPTY API |

**Factory Pattern:**

```csharp
public static class PtyFactory
{
    public static IPty Create()
    {
        if (PtyPlatform.IsWindows)
            return new WindowsPty();
        if (PtyPlatform.IsUnix)
            return new UnixPty();
        throw new PlatformNotSupportedException();
    }
}
```

### Layer 4: Dotty.App (Presentation)

**Purpose:** Avalonia UI, rendering, and user interaction.

**Key Components:**

```
Dotty.App/
├── Controls/
│   ├── TerminalControl.axaml        # Main terminal control
│   ├── SearchOverlay.axaml          # Search UI
│   └── Canvas/
│       ├── TerminalCanvas.cs        # Core canvas
│       └── Rendering/
│           ├── TerminalVisualHandler.cs   # GPU render
│           ├── TerminalFrameComposer.cs   # Background synth
│           ├── BackgroundSynth.cs         # Region merging
│           └── GlyphDiscovery.cs          # Async glyph load
├── Services/
│   ├── GlyphAtlasService.cs         # Shared atlas
│   └── HyperlinkService.cs          # URL handling
├── Configuration/
│   ├── ConfigBridge.cs              # Config → Avalonia
│   └── DefaultConfig.cs             # Fallback config
└── App.axaml.cs                     # App lifecycle
```

### Layer 5: Dotty.Config.SourceGenerator (Build-Time)

**Purpose:** Compile-time code generation for zero-overhead configuration.

**Data Flow:**

```
User Config (IDottyConfig)
    │
    ▼
┌─────────────────────────────────────┐
│ ConfigGenerator                     │
│ (IIncrementalGenerator)             │
├─────────────────────────────────────┤
│ 1. ConfigDiscovery - Find config    │
│ 2. ConfigExtractor - Parse values   │
│ 3. ThemeResolver - Load colors      │
│ 4. Emitters - Generate code         │
└─────────────────────────────────────┘
    │
    ▼
Generated Code (CS8001)
    - Dotty.Generated.Config
    - Dotty.Generated.ColorScheme
    - Dotty.Generated.KeyBindings
```

---

## Plugin/Extensibility Points

### Extension Mechanisms

#### 1. Custom Configuration

Users implement `IDottyConfig` to customize behavior:

```csharp
public class MyConfig : IDottyConfig
{
    public string? FontFamily => "FiraCode Nerd Font";
    public double? FontSize => 16.0;
    public IColorScheme? Colors => BuiltInThemes.CatppuccinMocha;
}
```

#### 2. Custom Themes

Themes can be added via JSON or C#:

```json
// themes.json (embedded resource)
{
  "MyCustomTheme": {
    "background": "#1E1E1E",
    "foreground": "#D4D4D4",
    "ansiColors": ["#000000", "#CD3131", ...]
  }
}
```

```csharp
// C# theme implementation
public class MyCustomTheme : IColorScheme
{
    public uint Background => 0xFF1E1E1E;
    public uint Foreground => 0xFFD4D4D4;
    public uint[] AnsiColors => new[] { ... };
}
```

#### 3. Custom PTY (Advanced)

Implement `IPty` for custom backends:

```csharp
public class SshPty : IPty
{
    private readonly SshClient _ssh;

    public void Start(string? shell, int columns, int rows, ...)
    {
        _ssh.Connect();
        _shellStream = _ssh.CreateShellStream();
    }

    public int Read(Span<byte> buffer)
        => _shellStream.Read(buffer);

    public void Write(ReadOnlySpan<byte> data)
        => _shellStream.Write(data);
}
```

#### 4. Handler Decorators

Wrap `ITerminalHandler` for custom behavior:

```csharp
public class LoggingHandler : ITerminalHandler
{
    private readonly ITerminalHandler _inner;
    private readonly ILogger _logger;

    public void OnPrint(ReadOnlySpan<char> text)
    {
        _logger.LogDebug("Print: {Text}", text.ToString());
        _inner.OnPrint(text);
    }
}
```

### Extension Points Table

| Extension Point | Interface/Class | Use Case |
|-------------------|-----------------|----------|
| Configuration | `IDottyConfig` | Custom fonts, colors, key bindings |
| Color Scheme | `IColorScheme` | Custom themes |
| PTY Backend | `IPty` | SSH, Docker, remote shells |
| Handler Decorator | `ITerminalHandler` | Logging, analytics |
| Parser Extension | `ITerminalParser` | Custom sequence handling |

---

## Configuration System Integration

### Configuration Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Configuration System                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  User Code                      Source Generator                 │
│  ┌───────────────┐              ┌──────────────────────┐         │
│  │ MyConfig.cs   │─────────────▶│  ConfigGenerator.cs  │         │
│  │ (implements   │  Compile-time │  (IIncrementalGen)   │         │
│  │  IDotty)     │              └──────────┬───────────┘         │
│  └───────────────┘                         │                      │
│                                            ▼                      │
│                              ┌──────────────────────┐             │
│                              │  Generated Files     │             │
│                              │  - Config.g.cs       │             │
│                              │  - ColorScheme.g.cs  │             │
│                              │  - KeyBindings.g.cs  │             │
│                              └──────────┬───────────┘             │
│                                         │                         │
│                                         ▼                         │
│  Runtime                      ┌──────────────────────┐             │
│  ┌───────────────┐            │  Dotty.App           │             │
│  │ Avalonia      │◀───────────│  - ConfigBridge.cs   │             │
│  │ Resources     │  Convert   │  - App.axaml.cs      │             │
│  └───────────────┘            └──────────────────────┘             │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Configuration Flow

1. **Discovery**: Source generator finds `IDottyConfig` implementations
2. **Extraction**: Property values extracted via semantic analysis
3. **Resolution**: Theme names resolved to color palettes
4. **Emission**: Static classes generated with constants
5. **Bridge**: `ConfigBridge` converts to Avalonia types
6. **Application**: Resources applied at app startup

### Detailed Configuration Documentation

For comprehensive configuration system documentation, see:
- [`docs/architecture/ConfigSourceGenerator.md`](architecture/ConfigSourceGenerator.md) - Complete source generator architecture
- [`docs/Configuration.md`](Configuration.md) - User-facing configuration guide
- [`docs/ConfigurationAdvanced.md`](ConfigurationAdvanced.md) - Advanced configuration options

---

## Platform Abstraction Layer

### Platform Detection

```csharp
public static class PtyPlatform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsUnix => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                              || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static bool IsConPtySupported => IsWindows &&
        Environment.OSVersion.Version.Build >= 17763; // Windows 10 1809
}
```

### Platform-Specific Implementations

#### Unix (Linux/macOS)

```csharp
internal sealed class UnixPty : IPty
{
    private int _masterFd;
    private int _slaveFd;
    private int _shellPid;
    private FileStream _masterStream;

    public void Start(string? shell, int columns, int rows, ...)
    {
        // Open PTY master/slave pair
        _masterFd = posix_openpt(O_RDWR | O_NOCTTY);
        grantpt(_masterFd);
        unlockpt(_masterFd);
        var slaveName = ptsname(_masterFd);
        _slaveFd = open(slaveName, O_RDWR);

        // Fork and exec shell
        _shellPid = fork();
        if (_shellPid == 0)
        {
            // Child: setup terminal and exec shell
            setsid();
            dup2(_slaveFd, 0);
            dup2(_slaveFd, 1);
            dup2(_slaveFd, 2);
            execvp(shell, args);
        }

        // Parent: create stream for I/O
        _masterStream = new FileStream(
            new SafeFileHandle(_masterFd, false),
            FileAccess.ReadWrite);
    }

    public int Read(Span<byte> buffer)
        => _masterStream.Read(buffer);

    public void Write(ReadOnlySpan<byte> data)
        => _masterStream.Write(data);
}
```

#### Windows

```csharp
internal sealed class WindowsPty : IPty
{
    private nint _consoleHandle;
    private nint _inputPipe;
    private nint _outputPipe;

    public void Start(string? shell, int columns, int rows, ...)
    {
        // Create ConPTY
        CreatePseudoConsole(
            new COORD { X = (short)columns, Y = (short)rows },
            inputRead,
            outputWrite,
            0,
            out _consoleHandle);

        // Create process attached to ConPTY
        CreateProcessW(
            shell,
            commandLine,
            ...,
            startupInfoEx,  // With PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
            ...);
    }
}
```

### Platform Capability Matrix

| Feature | Linux | macOS | Windows |
|---------|-------|-------|---------|
| PTY Creation | ✅ POSIX | ✅ POSIX | ✅ ConPTY |
| 256 Colors | ✅ | ✅ | ✅ |
| True Color | ✅ | ✅ | ✅ |
| Mouse Support | ✅ | ✅ | ✅ |
| Unicode | ✅ | ✅ | ✅ |
| Shell Integration | ✅ | ✅ | ✅ |
| Transparency | ✅ | ✅ | ✅ |
| Blur/Acrylic | ✅ | ⚠️ Limited | ✅ |

---

## Source File References

### Architecture Documentation

| File | Description |
|------|-------------|
| `docs/architecture/ConfigSourceGenerator.md` | Complete source generator architecture |
| `docs/Parsing.md` | Parser architecture and ANSI handling |
| `docs/Rendering.md` | Rendering pipeline and GPU integration |
| `docs/Testing.md` | Test architecture and guidelines |

### Abstractions Layer

| File | Description |
|------|-------------|
| `src/Dotty.Abstractions/Adapter/ITerminalHandler.cs` | Terminal action handler interface |
| `src/Dotty.Abstractions/Parser/ITerminalParser.cs` | Parser interface |
| `src/Dotty.Abstractions/Pty/IPty.cs` | PTY interface |
| `src/Dotty.Abstractions/Pty/PtyPlatform.cs` | Platform detection |
| `src/Dotty.Abstractions/Config/IDottyConfig.cs` | Configuration interface |
| `src/Dotty.Abstractions/Config/IColorScheme.cs` | Color scheme interface |
| `src/Dotty.Abstractions/Config/IKeyBindings.cs` | Key bindings interface |
| `src/Dotty.Abstractions/Config/ICursorSettings.cs` | Cursor settings interface |
| `src/Dotty.Abstractions/Config/IWindowDimensions.cs` | Window dimensions interface |

### Terminal Core Layer

| File | Description |
|------|-------------|
| `src/Dotty.Terminal/Parser/BasicAnsiParser.cs` | ANSI parser implementation |
| `src/Dotty.Terminal/Adapter/TerminalAdapter.cs` | Handler implementation |
| `src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs` | Terminal buffer |
| `src/Dotty.Terminal/Adapter/Buffer/ScreenManager.cs` | Screen management |
| `src/Dotty.Terminal/Adapter/Buffer/CellGrid.cs` | Cell storage |
| `src/Dotty.Terminal/Adapter/SgrParserArgb.cs` | SGR parser |
| `src/Dotty.Terminal/Adapter/SgrColorArgb.cs` | Color handling |

### Native PTY Layer

| File | Description |
|------|-------------|
| `src/Dotty.NativePty/PtyFactory.cs` | Platform PTY factory |
| `src/Dotty.NativePty/Unix/UnixPty.cs` | Unix PTY implementation |
| `src/Dotty.NativePty/Windows/WindowsPty.cs` | Windows ConPTY implementation |
| `src/Dotty.NativePty/Windows/NativeMethods.cs` | Windows P/Invoke |

### Application Layer

| File | Description |
|------|-------------|
| `src/Dotty.App/App.axaml.cs` | Application startup |
| `src/Dotty.App/Controls/TerminalControl.axaml` | Main terminal UI |
| `src/Dotty.App/Controls/Canvas/TerminalCanvas.cs` | Canvas control |
| `src/Dotty.App/Controls/Canvas/Rendering/TerminalVisualHandler.cs` | GPU rendering |
| `src/Dotty.App/Controls/Canvas/Rendering/TerminalFrameComposer.cs` | Frame composition |
| `src/Dotty.App/Configuration/ConfigBridge.cs` | Config bridge |
| `src/Dotty.App/Configuration/DefaultConfig.cs` | Default configuration |

### Source Generator Layer

| File | Description |
|------|-------------|
| `src/Dotty.Config.SourceGenerator/ConfigGenerator.cs` | Generator entry point |
| `src/Dotty.Config.SourceGenerator/Pipeline/ConfigDiscovery.cs` | Config discovery |
| `src/Dotty.Config.SourceGenerator/Pipeline/ConfigExtractor.cs` | Value extraction |
| `src/Dotty.Config.SourceGenerator/Pipeline/ExpressionEvaluator.cs` | Expression eval |
| `src/Dotty.Config.SourceGenerator/Pipeline/ThemeResolver.cs` | Theme resolution |
| `src/Dotty.Config.SourceGenerator/Emission/ConfigEmitter.cs` | Config code gen |
| `src/Dotty.Config.SourceGenerator/Emission/ColorSchemeEmitter.cs` | Theme code gen |
| `src/Dotty.Config.SourceGenerator/Emission/KeyBindingsEmitter.cs` | Key binding gen |

---

## Additional Resources

- **Clean Architecture**: Robert C. Martin's Clean Architecture principles
- **Ports and Adapters**: Hexagonal architecture pattern
- **Source Generators**: Microsoft Roslyn source generator documentation
- **Avalonia Architecture**: Avalonia UI framework documentation
- **PTY Internals**: "The TTY Demystified" by Linus Åkesson

---

*Document version: 1.0*  
*Last updated: 2026-04-04*
