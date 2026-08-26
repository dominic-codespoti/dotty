using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// GPU-plan Phase 3 draw operation: leases the compositor's Skia canvas and
/// renders the terminal frame through the quad path directly — no
/// WriteableBitmap, no full-surface CPU-to-GPU upload. The frame's data comes
/// from an immutable <see cref="RenderSnapshot"/> captured under a short
/// SyncRoot hold on the UI thread; the snapshot is disposed when the renderer
/// disposes this operation.
///
/// Threading: <see cref="Render"/> executes on the render thread. The composer
/// is accessed under <c>TerminalFrameComposer.RenderLock</c>, which also guards
/// the UI thread's next cache build, so composer caches are never mutated
/// concurrently. The passed SKPaint/SKFont are UI-created SkiaSharp handles
/// used sequentially (not concurrently mutated) — an accepted v1 risk.
/// </summary>
public sealed class QuadGlyphDrawOperation : ICustomDrawOperation, IDisposable
{
    private readonly TerminalFrameComposer _composer;
    private readonly RenderSnapshot _snapshot;
    private readonly QuadGlyphRenderer _renderer;
    private readonly float _scale;
    private readonly float _cellW;
    private readonly float _cellH;
    private readonly float _translateX;
    private readonly float _translateY;
    private readonly SKColor _background;
    private readonly SKPaint _framePaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKFont _frameFont;
    private bool _disposed;

    public QuadGlyphDrawOperation(
        TerminalFrameComposer composer,
        RenderSnapshot snapshot,
        QuadGlyphRenderer renderer,
        Rect bounds,
        float scale,
        float cellW,
        float cellH,
        float translateX,
        float translateY,
        SKColor background)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Bounds = bounds;
        _scale = scale <= 0 ? 1f : scale;
        _cellW = cellW;
        _cellH = cellH;
        _translateX = translateX;
        _translateY = translateY;
        _background = background;
        _frameFont = new SKFont(_composer.PrimaryTypeface, _composer.GlyphSize);
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => true;

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null) return; // software backend: caller keeps the bitmap fallback

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        if (canvas == null) return;

        canvas.Clear(_background);

        // One logical-to-physical transform, identical to the bitmap path.
        if (_scale != 1f)
            canvas.SetMatrix(SKMatrix.CreateScale(_scale, _scale));

        canvas.Translate(_translateX, _translateY);

        _composer.RenderTo(
            canvas,
            _snapshot,
            _framePaint,
            _frameFont,
            _cellW,
            _cellH,
            startRow: 0,
            endRow: _snapshot.Rows - 1,
            quadGlyphs: true); // GPU canvas: quads are the fast path here

        canvas.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Snapshot after the op has rendered; the atlas image is owned by the
        // renderer and outlives this operation.
        _snapshot.Dispose();
        _frameFont.Dispose();
        _framePaint.Dispose();
    }
}
