# Rendering Loop & Avalonia Integration

The rendering system in Dotty is built for high-performance terminal display using GPU acceleration through SkiaSharp and Avalonia's composition layer. It implements a sophisticated multi-stage pipeline optimized for 60+ FPS terminal output with minimal CPU and memory overhead.

## Table of Contents

1. [Rendering Pipeline Architecture](#rendering-pipeline-architecture)
2. [GPU Rendering with SkiaSharp](#gpu-rendering-with-skiasharp)
3. [Canvas Management](#canvas-management)
4. [Cell Rendering Strategies](#cell-rendering-strategies)
5. [Font and Glyph Handling](#font-and-glyph-handling)
6. [Performance Optimizations](#performance-optimizations)
7. [Screen Buffer Management](#screen-buffer-management)
8. [Source File References](#source-file-references)

---

## Rendering Pipeline Architecture

The rendering architecture follows a **composition-based, deferred rendering model** that separates preparation work from actual GPU draw calls.

### High-Level Data Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Rendering Pipeline                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐ │
│  │  Terminal    │   │  Glyph       │   │  Background  │   │  GPU         │ │
│  │  Buffer      │──▶│  Discovery   │──▶│  Synthesis   │──▶│  Composition │ │
│  │  (Mutation)  │   │  (Async)     │   │  (CPU)       │   │  (Skia)      │ │
│  └──────────────┘   └──────────────┘   └──────────────┘   └──────────────┘ │
│         │                    │                  │                │          │
│         │                    │                  │                │          │
│         ▼                    ▼                  ▼                ▼          │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐ │
│  │  Cell Grid   │   │  Glyph Atlas │   │  Region      │   │  SkCanvas    │ │
│  │  (2D Array)  │   │  (Texture)   │   │  Collection  │   │  (GPU)       │ │
│  └──────────────┘   └──────────────┘   └──────────────┘   └──────────────┘ │
│                                                                             │
│  Stage 1: Buffer      Stage 2: Discovery    Stage 3: Synthesis    Stage 4:  │
│  Mutation             & Atlas Population    & Region Build        Render    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities

| Component | Responsibility | Performance Goal |
|-----------|----------------|------------------|
| **TerminalBuffer** | Stores cell grid with attributes | O(1) cell access, minimal locking |
| **GlyphDiscovery** | Identifies new glyphs, enqueues for atlas | Slice-based processing (5 rows/frame) |
| **GlyphAtlas** | GPU texture with pre-rendered glyphs | Shared across tabs, lazy population |
| **TerminalFrameComposer** | Background region synthesis, glyph batching | Zero-allocation per frame |
| **TerminalVisualHandler** | GPU draw commands, composition integration | 60+ FPS target, minimal CPU |
| **TerminalCanvas** | Avalonia control, scrollable viewport | Virtual scrolling, dirty tracking |

### Frame Lifecycle

```
┌────────────────────────────────────────────────────────────┐
│                    Frame Lifecycle                           │
├────────────────────────────────────────────────────────────┤
│                                                              │
│  1. MUTATION          TerminalAdapter writes to buffer     │
│     ↓                                                        │
│  2. INVALIDATION      Buffer signals change, enqueues frame  │
│     ↓                                                        │
│  3. DISCOVERY           GlyphDiscovery.ProcessSlice(5)       │
│     │                      (runs a slice of pending rows)    │
│     ↓                                                        │
│  4. DEBOUNCE            1ms timer coalesces rapid changes    │
│     ↓                                                        │
│  5. COMPOSITION         TerminalFrameComposer builds regions  │
│     │                      - CollectBackgroundRegions()       │
│     │                      - ClassifyRowCells()               │
│     │                      - MergeRowSpans()                  │
│     ↓                                                        │
│  6. RENDER              TerminalVisualHandler draws to GPU    │
│     │                      - Clear canvas                    │
│     │                      - DrawBackgroundRegions()         │
│     │                      - DrawGlyphs()                    │
│     │                      - DrawSelection/Search overlays   │
│     ↓                                                        │
│  7. PRESENT             Avalonia composition presents frame  │
│                                                              │
└────────────────────────────────────────────────────────────┘
```

---

## GPU Rendering with SkiaSharp

Dotty uses **SkiaSharp** as the GPU abstraction layer, enabling hardware-accelerated rendering across platforms (Windows, Linux, macOS).

### Avalonia Integration

```csharp
// TerminalCanvas extends Avalonia Control with custom rendering
public class TerminalCanvas : Control, ILogicalScrollable
{
    private CompositionCustomVisual? _customVisual;
    private TerminalVisualHandler _handler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Create composition visual with isolated surface
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        var handler = new TerminalVisualHandler();
        _customVisual = compositor.CreateCustomVisual(handler);
        ElementComposition.SetElementChildVisual(this, _customVisual);
    }
}
```

### SkiaSharp Rendering Context

The `TerminalVisualHandler` receives an `ImmediateDrawingContext` and extracts the Skia canvas:

```csharp
public override void OnRender(ImmediateDrawingContext context)
{
    var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
    if (leaseFeature == null) return;

    using var lease = leaseFeature.Lease();
    var canvas = lease.SkCanvas;

    // Clear with background color
    canvas.Clear(s.BgColor);

    // Apply transforms for virtual scroll
    canvas.Translate(0, (float)(sbCount * s.CellHeight - s.ScrollY));

    // Render background regions and glyphs
    composer.RenderTo(canvas, buffer, paint, cellW, cellH, startRow, endRow);
}
```

### GPU Features Used

| Feature | Skia API | Purpose |
|---------|----------|---------|
| **Texture atlas** | `SKImage` from bitmap | Pre-rendered glyph cache |
| **Round rectangles** | `DrawRoundRect()` | Terminal "pill" backgrounds |
| **Text rendering** | `DrawText()` | Glyph drawing with subpixel LCD |
| **Clipping** | `ClipRoundRect()` | Clean rounded region edges |
| **Matrix transforms** | `Concat(ref SKMatrix)` | Virtual scroll offset |
| **Anti-aliasing** | `IsAntialias = true` | Smooth edges for UI elements |

---

## Canvas Management

### Surface Isolation

Each `TerminalCanvas` instance has its own dedicated composition surface that is destroyed when the control is detached and recreated when attached. This prevents content stacking when switching tabs.

```csharp
private void CreateCompositionVisual()
{
    // Destroy any existing visual first
    DestroyCompositionVisual();

    var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
    if (compositor == null) return;

    // Create fresh handler and visual
    var handler = new TerminalVisualHandler();
    _customVisual = compositor.CreateCustomVisual(handler);
    _customVisual.Size = new Avalonia.Vector(Bounds.Width, Bounds.Height);

    ElementComposition.SetElementChildVisual(this, _customVisual);
    _frameComposer?.ResetCaches();
}

private void DestroyCompositionVisual()
{
    if (_customVisual != null)
    {
        ElementComposition.SetElementChildVisual(this, null);
        _customVisual = null;
    }
}
```

### Lifecycle Management

```
┌────────────────────────────────────────────────────────────────┐
│                    Canvas Lifecycle                              │
├────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────┐          ┌──────────┐          ┌──────────┐      │
│  │ Created  │─────────▶│ Attached │─────────▶│ Visible  │      │
│  └──────────┘          └──────────┘          └──────────┘      │
│       │                     │                     │              │
│       │                     │                     │              │
│       ▼                     ▼                     ▼              │
│  ┌──────────┐          ┌──────────┐          ┌──────────┐      │
│  │ - Init   │          │ - Create │          │ - Start  │      │
│  │   state  │          │   visual │          │   render │      │
│  └──────────┘          │ - Setup  │          │ - Enable │      │
│                        │   timer  │          │   input  │      │
│                        └──────────┘          └──────────┘      │
│                                                        │         │
│                                                        ▼         │
│                        ┌──────────┐          ┌──────────┐      │
│                        │ Detached │◀─────────│ Hidden   │      │
│                        └──────────┘          └──────────┘      │
│                             │                     │              │
│                             │                     │              │
│                             ▼                     ▼              │
│                        ┌──────────┐          ┌──────────┐      │
│                        │ - Destroy│          │ - Pause  │      │
│                        │   visual │          │   render │      │
│                        │ - Dispose│          │ - Keep   │      │
│                        │   paint  │          │   state  │      │
│                        └──────────┘          └──────────┘      │
│                                                                  │
└────────────────────────────────────────────────────────────────┘
```

### Virtual Scrolling

The canvas supports virtual scrolling for large scrollback buffers:

```csharp
// Calculate visible rows based on scroll offset
int startVisibleRow = (int)Math.Floor(s.ScrollY / s.CellHeight) - sbCount;
int endVisibleRow = (int)Math.Ceiling((s.ScrollY + s.ViewportHeight) / s.CellHeight) - sbCount;

// Translate canvas for scroll position
canvas.Translate(0, (float)(sbCount * s.CellHeight - s.ScrollY));

// Render only visible rows
composer.RenderTo(canvas, buffer, paint, cellW, cellH, composerStart, composerEnd);
```

---

## Cell Rendering Strategies

### Background Synthesis (BackgroundSynth)

The terminal uses a sophisticated background synthesis system that creates continuous regions for uniform background colors, reducing overdraw:

```csharp
// Build row spans from cell classifications
private void CollectBackgroundRegions(TerminalBuffer buffer, int startRow, int endRow)
{
    for (int row = startRow; row <= endRow; row++)
    {
        ClassifyRowCells(buffer, row);
        BuildRowSpans(_cellClasses, row);
        MergeRowSpans(row);
    }
    FlushActiveRegions();
}
```

### Region Merging Algorithm

```
Row 0:  [AAABBBCCCC]  →  Span(0,3,A), Span(3,6,B), Span(6,10,C)
Row 1:  [AAABBBDDDD]  →  Span(0,3,A), Span(3,6,B), Span(6,10,D)

Active Regions:
┌─────────┐     ┌─────────┐     ┌─────────┐
│ A (0,3) │     │ B (3,6) │     │ C (6,10)│  After Row 0
└────┬────┘     └────┬────┘     └────┬────┘
     │               │               │  (C closed, D opened)
     │               │               ▼
     │               │          ┌─────────┐
     │               │          │ D (6,10)│  After Row 1
     │               │          └─────────┘
     ▼               ▼
┌─────────────────────────────────┐
│  Final Regions:                 │
│  - Region A: (0,3), rows 0-2    │
│  - Region B: (3,6), rows 0-2    │
│  - Region C: (6,10), row 0      │
│  - Region D: (6,10), row 1      │
└─────────────────────────────────┘
```

### Cell Classification

Each cell is classified before rendering to determine background and foreground handling:

```csharp
private struct CellClass
{
    public bool IsContinuation;     // Multi-column character continuation
    public int Width;               // Cell width (1 or 2 for wide chars)
    public bool HasBg;              // Has non-default background
    public SKColor Bg;              // Background color
    public bool HasFg;              // Has non-default foreground
    public SKColor Fg;              // Foreground color
    public string Grapheme;         // Display text
    public int FirstRune;           // First Unicode codepoint
    public bool IsSeparatorGlyph;   // Powerline/Nerd Font separator
    public bool ShouldDrawGlyph;     // Whether to draw text
    public Cell RawCell;            // Original cell data
    public ushort HyperlinkId;      // Hyperlink identifier
}
```

### Drawing Strategy

1. **Background Regions**: Draw merged regions as rounded rectangles ("pills")
2. **Glyphs**: Draw text on top of backgrounds
3. **Decorations**: Underline, strikethrough, overline, hyperlink underlines
4. **Overlays**: Selection highlight, search match highlights

---

## Font and Glyph Handling

### Font Resolution

```csharp
// Default font stack from generated config
public static readonly StyledProperty<FontFamily> FontFamilyProperty =
    AvaloniaProperty.Register<TerminalCanvas, FontFamily>(
        nameof(FontFamily), 
        new FontFamily(Generated.Config.FontFamily));  // "JetBrainsMono Nerd Font Mono,..."

// Metrics calculation
var fontSize = double.IsNaN(FontSize) || FontSize <= 0 ? 13.0 : FontSize;
var scale = Math.Max(0.1, _renderScaling);
var scaledFontSize = Math.Max(1f, (float)(fontSize * scale));

var typeface = SKTypeface.FromFamilyName(familyName);
var paint = new SKPaint
{
    Typeface = typeface,
    TextSize = scaledFontSize,
    IsAntialias = true,
    IsLinearText = true,
    SubpixelText = true,
    IsAutohinted = true,
    LcdRenderText = true,
};
```

### Glyph Atlas

The glyph atlas is a shared GPU texture that caches pre-rendered glyphs:

```csharp
public class GlyphAtlas
{
    private readonly SKSurface _surface;
    private readonly Dictionary<(int Rune, SKTypeface Typeface), GlyphInfo> _glyphs;

    public GlyphInfo GetOrAddGlyph(int rune, SKPaint paint)
    {
        var key = (rune, paint.Typeface);
        if (_glyphs.TryGetValue(key, out var info))
            return info;

        // Render glyph to atlas texture
        info = RenderGlyphToAtlas(rune, paint);
        _glyphs[key] = info;
        return info;
    }
}
```

### Glyph Discovery

New glyphs are discovered asynchronously to avoid blocking the UI thread:

```csharp
public void OnBufferUpdated(TerminalBuffer buffer)
{
    if (buffer == null || _glyphDiscovery == null) return;
    _glyphDiscovery.EnsureSize(buffer.Rows);

    // Enqueue all rows for discovery
    for (int r = 0; r < buffer.Rows; r++)
        _glyphDiscovery.EnqueueRow(r);
}

private void ProcessGlyphDiscoverySlice()
{
    if (_glyphDiscovery == null) return;
    var buf = Buffer;
    if (buf != null)
    {
        // Process up to 5 rows per frame to maintain 60 FPS
        _glyphDiscovery.Process(buf, 5);
    }
}
```

### Atlas Sharing

Multiple tabs with the same font configuration share a single atlas:

```csharp
// Get or create shared atlas
_glyphAtlas = GlyphAtlasService.GetOrCreateAtlas(
    SkPaint.Typeface, 
    SkPaint.TextSize, 
    _glyphRasterizationOptions);
```

---

## Performance Optimizations

### Rendering Constraints

The rendering system operates under strict performance constraints:

| Constraint | Implementation | Rationale |
|------------|------------------|-----------|
| **No LINQ in hot path** | Explicit loops only | LINQ allocations and overhead |
| **No boxing** | `ref struct`, `Span<T>` | Prevent heap allocations |
| **Zero-allocation frames** | Reusable buffers, object pooling | Consistent 60 FPS |
| **Minimal property access** | Cache values locally | Reduce virtual calls |
| **Batch draw calls** | Draw regions, then glyphs | GPU batching efficiency |

### Frame Debouncing

Rapid terminal output is coalesced into single frames:

```csharp
private DispatcherTimer? _frameDebounceTimer;
private const double FrameDebounceMs = 1;

public void RequestFrame()
{
    if (!IsVisible || !_isAttached) return;

    if (_frameDebounceTimer == null)
    {
        EnsureFrameTimer();
        _frameDebounceTimer.Start();
    }
    _framePending = true;
}

private void FrameDebounceTick(object? sender, EventArgs e)
{
    _frameDebounceTimer?.Stop();
    if (!_framePending) return;
    _framePending = false;

    ProcessGlyphDiscoverySlice();
    SendRenderState();
}
```

### Dirty Tracking

The system uses a scroll generation counter for coarse-grained dirty tracking:

```csharp
public int ScrollGeneration => _scrollGeneration;

private void BumpScrollGeneration()
{
    unchecked { _scrollGeneration++; }
}

// Renderer checks generation to detect changes
if (buffer.ScrollGeneration != _lastRenderedGeneration)
{
    InvalidateVisual();
}
```

### Memory Optimizations

| Technique | Implementation |
|-----------|----------------|
| **Glyph atlas reuse** | Shared across tabs via `GlyphAtlasService` |
| **Paint pooling** | Single `SKPaint` instance per canvas |
| **Region pooling** | `Stack<ActiveRegion>` for reuse |
| **Scratch buffers** | Reusable arrays for classification |
| **Lazy atlas population** | Only render glyphs when first seen |

### Performance Targets

| Metric | Target | Typical |
|--------|--------|---------|
| Frame rate | 60 FPS | 60-120 FPS |
| Input latency | <16ms | ~8ms |
| Memory per tab | <50MB | ~30MB |
| Atlas memory | Shared | ~10MB for common glyphs |
| CPU usage (idle) | <1% | ~0.5% |
| CPU usage (active) | <10% | ~5% |

---

## Screen Buffer Management

### Buffer Structure

```csharp
public class TerminalBuffer
{
    private readonly ScreenManager _screens;
    private readonly ScrollbackLine[] _scrollbackRing;
    private int _scrollbackHead;
    private int _scrollbackCount;

    public int Rows { get; private set; }
    public int Columns { get; private set; }
    public int ScrollbackCount => _scrollbackCount;

    public Cell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return new Cell { Rune = 32, Width = 1 };
        return ActiveBuffer.GetCell(row, col);
    }
}
```

### Double Buffering

The terminal uses a screen manager to handle primary and alternate screen buffers:

```csharp
internal class ScreenManager
{
    private readonly Screen _primary;
    private readonly Screen _alternate;
    private bool _isAlternateActive;

    public Screen Active => _isAlternateActive ? _alternate : _primary;

    public void SetAlternate(bool active)
    {
        _isAlternateActive = active;
    }

    public void ClearAll()
    {
        _primary.Clear();
        _alternate.Clear();
    }
}
```

### Scrollback Management

Scrollback uses a ring buffer for efficient line storage:

```csharp
public void ClearScrollback()
{
    _scrollbackCount = 0;
    _scrollbackHead = 0;
    _scrollbackRing = System.Array.Empty<ScrollbackLine>();
    BumpScrollGeneration();
}

public void TrimScrollback(int maxLines)
{
    if (_scrollbackCount <= maxLines) return;

    int linesToKeep = System.Math.Min(maxLines, _scrollbackCount);
    int linesToRemove = _scrollbackCount - linesToKeep;

    _scrollbackHead = (_scrollbackHead + linesToRemove) % _scrollbackRing.Length;
    _scrollbackCount = linesToKeep;
    BumpScrollGeneration();
}
```

### Cell Structure

Each cell stores:

```csharp
public struct Cell
{
    public uint Rune;              // Unicode codepoint
    public string? Grapheme;       // Full grapheme cluster
    public uint Foreground;        // ARGB foreground color
    public uint Background;        // ARGB background color
    public byte Width;             // 1 or 2 for wide chars
    public bool IsContinuation;    // Continuation of wide char
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public bool DoubleUnderline;
    public bool Strikethrough;
    public bool Overline;
    public bool SlowBlink;
    public bool RapidBlink;
    public bool Inverse;
    public bool Invisible;
    public uint UnderlineColor;    // ARGB underline color
    public ushort HyperlinkId;     // Hyperlink identifier
}
```

---

## Source File References

### Core Rendering Implementation

| File | Description |
|------|-------------|
| `src/Dotty.App/Controls/Canvas/TerminalCanvas.cs` | Main control integrating with Avalonia, handling scrolling and composition |
| `src/Dotty.App/Controls/Canvas/Rendering/TerminalVisualHandler.cs` | GPU rendering handler, SkiaSharp draw commands |
| `src/Dotty.App/Controls/Canvas/Rendering/TerminalFrameComposer.cs` | Background synthesis and glyph batching |
| `src/Dotty.App/Controls/Canvas/Rendering/BackgroundSynth.cs` | Background region merging algorithm |

### Glyph and Font Management

| File | Description |
|------|-------------|
| `src/Dotty.App/Discovery/GlyphDiscovery.cs` | Asynchronous glyph identification and atlas population |
| `src/Dotty.App/Services/GlyphAtlasService.cs` | Shared glyph atlas management |
| `src/Dotty.App/Services/GlyphAtlas.cs` | GPU texture atlas implementation |

### Avalonia Integration

| File | Description |
|------|-------------|
| `src/Dotty.App/App.axaml.cs` | Application startup, resource initialization |
| `src/Dotty.App/Configuration/ConfigBridge.cs` | Converts generated config to Avalonia types |

### Buffer Management

| File | Description |
|------|-------------|
| `src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs` | Terminal buffer with scrollback |
| `src/Dotty.Terminal/Adapter/Buffer/ScreenManager.cs` | Primary/alternate screen handling |
| `src/Dotty.Terminal/Adapter/Buffer/CellGrid.cs` | 2D cell storage |
| `src/Dotty.Terminal/Adapter/Buffer/Cell.cs` | Cell structure definition |

### Appearance and Styling

| File | Description |
|------|-------------|
| `src/Dotty.App/Controls/Canvas/Rendering/TerminalAppearanceSettings.cs` | Visual appearance configuration |
| `src/Dotty.App/Controls/Canvas/Rendering/SgrColorExtensions.cs` | Color conversion utilities |

### Test Files

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/BackgroundSynthTests.cs` | Background region synthesis tests |
| `tests/Dotty.App.Tests/AsciiArtRenderTests.cs` | ASCII art rendering tests |
| `tests/Dotty.App.Tests/PermutationScrollRenderTests.cs` | Scroll rendering tests |
| `tests/Dotty.App.Tests/ScrollbackRenderTest.cs` | Scrollback integration tests |
| `tests/Dotty.App.Tests/HyperlinkRenderingTests.cs` | Hyperlink rendering tests |
| `tests/Dotty.App.Tests/SearchHighlightRenderingTests.cs` | Search highlight rendering tests |

---

## Additional Resources

- **SkiaSharp Documentation**: https://mono.github.io/SkiaSharp.Extended/
- **Avalonia UI Composition**: https://docs.avaloniaui.net/docs/concepts/composition
- **GPU Text Rendering Best Practices**: Subpixel anti-aliasing, texture atlases
- **Terminal Emulator Performance**: Casey Muratori's "How fast should an unoptimized terminal run?"

---

*Document version: 1.0*  
*Last updated: 2026-04-04*
