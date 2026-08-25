using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Dotty.App.Controls.Canvas;
using Dotty.App.Controls.Canvas.Rendering;
using Dotty.App.Rendering;
using Dotty.App.Services;
using Dotty.App.Configuration;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls;

public enum TerminalCursorShape
{
	Block,
	Beam,
	Underline
}

/// <summary>
/// TerminalCanvas with complete surface isolation.
/// Each instance has its own dedicated composition surface that is
/// destroyed when the control is detached and recreated when attached.
/// This prevents content stacking when switching tabs.
/// </summary>
public class TerminalCanvas : Control, ILogicalScrollable
{
	public static readonly StyledProperty<TerminalBuffer?> BufferProperty =
		AvaloniaProperty.Register<TerminalCanvas, TerminalBuffer?>(nameof(Buffer));

	public static readonly StyledProperty<FontFamily> FontFamilyProperty =
		AvaloniaProperty.Register<TerminalCanvas, FontFamily>(nameof(FontFamily), new FontFamily(Generated.Config.FontFamily));

	public static readonly StyledProperty<double> FontSizeProperty =
		AvaloniaProperty.Register<TerminalCanvas, double>(nameof(FontSize), Generated.Config.FontSize);

	public static readonly StyledProperty<double> CellPaddingProperty =
		AvaloniaProperty.Register<TerminalCanvas, double>(nameof(CellPadding), Generated.Config.CellPadding);

	public TerminalBuffer? Buffer
	{
		get => GetValue(BufferProperty);
		set => SetValue(BufferProperty, value);
	}

	public static readonly StyledProperty<Thickness> ContentPaddingProperty =
		AvaloniaProperty.Register<TerminalCanvas, Thickness>(nameof(ContentPadding), new Thickness(
			Generated.Config.ContentPaddingLeft,
			Generated.Config.ContentPaddingTop,
			Generated.Config.ContentPaddingRight,
			Generated.Config.ContentPaddingBottom));

	public static readonly StyledProperty<IBrush> SelectionBrushProperty =
		AvaloniaProperty.Register<TerminalCanvas, IBrush>(nameof(SelectionBrush),
			new SolidColorBrush(ConfigBridge.ToColor(Generated.Config.SelectionColor)));

	public Thickness ContentPadding
	{
		get => GetValue(ContentPaddingProperty);
		set => SetValue(ContentPaddingProperty, value);
	}

	private TerminalSelectionRange _selectionRange = TerminalSelectionRange.Empty;

	public TerminalSelectionRange SelectionRange
	{
		get => _selectionRange;
		set
		{
			if (_selectionRange == value) return;
			_selectionRange = value;
			_contentDirty = true; // selection is rasterized into the bitmap
			InvalidateVisual();
		}
	}

	private IReadOnlyList<SearchMatch> _searchMatches = Array.Empty<SearchMatch>();

	public IReadOnlyList<SearchMatch> SearchMatches
	{
		get => _searchMatches;
		set
		{
			if (_searchMatches == value) return;
			_searchMatches = value ?? Array.Empty<SearchMatch>();
			InvalidateVisual();
		}
	}

	public static readonly StyledProperty<TerminalCursorShape> CursorShapeProperty =
		AvaloniaProperty.Register<TerminalCanvas, TerminalCursorShape>(nameof(CursorShape), TerminalCursorShape.Block);

	public TerminalCursorShape CursorShape
	{
		get => GetValue(CursorShapeProperty);
		set => SetValue(CursorShapeProperty, value);
	}

	public IBrush SelectionBrush
	{
		get => GetValue(SelectionBrushProperty);
		set => SetValue(SelectionBrushProperty, value);
	}

	public FontFamily FontFamily
	{
		get => GetValue(FontFamilyProperty);
		set => SetValue(FontFamilyProperty, value);
	}

	public double FontSize
	{
		get => GetValue(FontSizeProperty);
		set => SetValue(FontSizeProperty, value);
	}

	public double CellPadding
	{
		get => GetValue(CellPaddingProperty);
		set => SetValue(CellPaddingProperty, value);
	}

	static TerminalCanvas()
	{
		AffectsRender<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty, CellPaddingProperty, ContentPaddingProperty, SelectionBrushProperty);
		AffectsMeasure<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty, CellPaddingProperty, ContentPaddingProperty);
	}

	private float _cellWidth = 8;
	private float _cellHeight = 16;
	private bool _metricsDirty = true;
	private TerminalFrameComposer? _frameComposer;
	private TextShaper? _textShaper;
	private static readonly ShapedRunCache SharedShapedRunCache = new();

	// GPU-plan Phase 2: env-gated quad glyph renderer. When enabled, the
	// composer draws glyphs through the A8 atlas + quad batch inside the
	// proven bitmap pipeline.
	private static readonly bool s_useQuadRender =
		!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTTY_QUAD_RENDER"));

	// GPU-plan Phase 3: render the frame through a custom draw operation that
	// leases the compositor's Skia canvas — no WriteableBitmap, no full-surface
	// upload. Requires a backend whose compositor surface exposes
	// ISkiaSharpApiLeaseFeature (GPU-composited sessions); on software backends
	// the probe falls back to the bitmap pipeline automatically.
	private static readonly bool s_useLeaseRender =
		true; // probe every frame: the lease feature is backend-dependent (Xvfb = software, live GPU sessions expose it)
	private GlyphAtlas? _quadAtlas;
	private QuadGlyphRenderer? _quadRenderer;

	// Global font resolution cache shared across all TerminalCanvas instances.
	// Key is "{FontFamily}|{TextSize:F1}".  Invalidated when font settings change.
	private static readonly ConcurrentDictionary<string, SKTypeface> CachedPrimaryTypeface = new();
	private static readonly ConcurrentDictionary<string, List<SKTypeface>> CachedFallbackTypefaces = new();
	private static string? s_lastFontCacheKey;

	private bool _lastBufferWasAlternate = false;
	private int _lastKnownBufferRows = -1;
	private int _lastKnownBufferColumns = -1;

	// Step C per-row dirty redraw state (IncrementalScrollRendering.md §4.5).
	// The dirty path patches the retained bitmap only when the scroll offset,
	// scrollback count, selection, preedit, and alt-screen state are all
	// unchanged, so every pixel outside the dirty rows is provably identical
	// to the last full render.
	private ulong[]? _lastRowGenerations;
	private double _lastRenderedOffsetY = double.NaN;
	private int _lastRenderedSbCount = -1;
	private TerminalSelectionRange _lastRenderedSelection = TerminalSelectionRange.Empty;
	private bool _lastRenderedPreeditActive;
	private bool _forceFullRender = true; // first render / bitmap recreation
	private int _renderDiag;
	private readonly List<int> _dirtyRowScratch = new();

	private double _renderScaling = 1.0;
	private TopLevel? _attachedTopLevel;
	private static readonly string[] MonospaceFallbackFamilies =
	{
		"JetBrains Mono",
		"JetBrainsMono Nerd Font Mono",
		"Cascadia Code",
		"Cascadia Mono",
		"Consolas",
		"Fira Code",
		"Noto Sans Mono",
		"Liberation Mono",
		"Courier New",
		"monospace"
	};

	private static readonly string[] EmojiFontFamilies =
	{
		"Noto Color Emoji",
		"Apple Color Emoji",
		"Segoe UI Emoji",
		"EmojiOne Color",
		"Twemoji Mozilla",
	};
	
	private WriteableBitmap? _bitmap;
	internal TerminalRenderTelemetry RenderTelemetry { get; set; } = new();

	/// <summary>
	/// Invoked when a render is skipped because the buffer lock could not be
	/// acquired within the bounded wait. The owner (TerminalView) schedules
	/// one more presentation frame so the skipped content is retried instead
	/// of being lost until the next mutation.
	/// </summary>
	internal Action? FrameRetryRequested;
	private SKPaint? _debugTextPaint;
	private SKFont? _debugFont;
	private SKPaint? _debugBgPaint;
	private SKPaint? _selectionPaint;

	public bool ShowDebugOverlay { get; set; }
	
	public SKPaint? SkPaint { get; private set; }
	public SKFont? SkFont { get; private set; }
	public double CellWidth
	{
		get
		{
			EnsureMetrics();
			return _cellWidth;
		}
	}

	public double CellHeight
	{
		get
		{
			EnsureMetrics();
			return _cellHeight;
		}
	}

	private bool _showCursor = true;
	public bool ShowCursor 
	{ 
		get => _showCursor; 
		set 
		{
			if (_showCursor != value)
			{
				_showCursor = value;
				InvalidateVisual();
			}
		} 
	}

	/// <summary>
	/// True when the cached content bitmap no longer matches the buffer and
	/// must be re-rasterized. Overlay-only changes (cursor blink, cursor shape)
	/// leave it false so the cached bitmap is reused across frames.
	/// </summary>
	private bool _contentDirty = true;

	// Theme brushes resolved on attach, settings, or theme change so Render
	// never touches the resource dictionary or converts colors per frame.
	private IBrush? _cachedBackgroundBrush;
	private SKColor _cachedBackgroundArgb = SKColors.Black;
	private IBrush? _cachedCursorBrush;

	// IME preedit state (active composition), rendered as an overlay at the
	// cursor cell.
	private string? _preeditText;
	private int _preeditCursor;
	private int _lastRenderedCursorCell = -1;

	/// <summary>
	/// Invoked when the rendered cursor cell changes so the IME candidate
	/// window can follow it. Set by the owning view.
	/// </summary>
	internal Action? CursorMovedCallback;

	/// <summary>
	/// Bounded visible viewport text for assistive technology. Computed lazily
	/// on query (AT-driven, never per frame); caps the returned length.
	/// </summary>
	internal string GetVisibleTextForAccessibility()
	{
		var buffer = Buffer;
		if (buffer == null)
		{
			return string.Empty;
		}

		// AT-driven, never per frame; read under SyncRoot so the extraction
		// observes a consistent buffer state.
		string result = string.Empty;
		try
		{
			buffer.WithSyncRoot(() => result = BuildVisibleTextForAccessibility(buffer));
		}
		catch (TimeoutException)
		{
			return string.Empty;
		}
		return result;
	}

	private string BuildVisibleTextForAccessibility(TerminalBuffer buffer)
	{
		var sb = new System.Text.StringBuilder(4096);
		int startRow = Math.Max(0, (int)Math.Floor(_offset.Y / _cellHeight) - buffer.ScrollbackCount);
		int endRow = Math.Min(buffer.Rows - 1, (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - buffer.ScrollbackCount);
		for (int r = startRow; r <= endRow && sb.Length < 16_384; r++)
		{
			if (r < 0)
			{
				// Scrollback: row -1 is the newest scrollback line.
				int sbIdx = -r - 1;
				if (sbIdx < buffer.ScrollbackCount)
					sb.Append(buffer.GetScrollbackLine(sbIdx).Text ?? string.Empty);
			}
			else
			{
				sb.Append(buffer.GetRowText(r));
			}
			sb.Append('\n');
		}

		return sb.ToString();
	}

	protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
		new Canvas.Rendering.TerminalCanvasAutomationPeer(this);

    // --- ILogicalScrollable implementation ---
    public bool CanHorizontallyScroll { get; set; } = false;
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;

    private Size _viewport;
    public Size Viewport => _viewport;

    private Vector _offset;
    public Vector Offset 
    { 
        get => _offset; 
        set
        {
            if (_offset != value)
            {
                _offset = value;
                _contentDirty = true; // scroll translate is baked into the bitmap
                ScrollInvalidated?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        } 
    }

    /// <summary>
    /// Returns true when the viewport is scrolled to the very bottom
    /// (within a sub-pixel tolerance). Used to decide whether new output
    /// should auto-scroll or preserve the user's scrollback position.
    /// </summary>
    public bool IsAtBottom
    {
        get
        {
            var extent = Extent;
            return Math.Abs(_offset.Y - Math.Max(0, extent.Height - _viewport.Height)) < 0.1;
        }
    }

    public Size Extent 
    {
        get
        {
            var buf = Buffer;
            if (buf == null) return _viewport;
            double height = (buf.Rows + buf.ScrollbackCount) * _cellHeight + ContentPadding.Top + ContentPadding.Bottom;
            double width = buf.Columns * _cellWidth + ContentPadding.Left + ContentPadding.Right;
            return new Size(width, height);
        }
    }

    public Size ScrollSize => new Size(16, _cellHeight);
    public Size PageScrollSize => new Size(16, _viewport.Height);

    public event EventHandler? ScrollInvalidated;
    
    public Action? InvalidateScroll { get; set; }

    public bool BringIntoView(Control target, Rect targetRect) => false;
    
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e)
    {
        ScrollInvalidated?.Invoke(this, e);
    }

    private Size _lastExtent;
    private Size _lastViewport;

    // Latest buffer geometry captured under SyncRoot at render start, applied
    // by a single coalesced posted delegate so the follow decision never races
    // a newer scrollback count (the pre-R2 code re-read live state in the
    // posted callback and ran the update twice per frame with mismatched data).
    private int _pendingExtentRows;
    private int _pendingExtentSbCount;
    private bool _extentUpdatePosted;

    internal Size ComputeExtent(int rows, int sbCount)
    {
        var buf = Buffer;
        if (buf == null) return _viewport;
        double height = (rows + sbCount) * _cellHeight + ContentPadding.Top + ContentPadding.Bottom;
        double width = buf.Columns * _cellWidth + ContentPadding.Left + ContentPadding.Right;
        return new Size(width, height);
    }

    /// <summary>
    /// Applies a new extent, keeping the viewport glued to the bottom when it
    /// already was (and the user has not scrolled away). User intent is read
    /// from the live offset at apply time: a wheel-up that lands before this
    /// runs breaks <c>wasAtBottom</c> and correctly cancels the follow.
    /// </summary>
    internal void ApplyExtent(Size extent)
    {
        bool changed = false;

        if (extent != _lastExtent || _viewport != _lastViewport)
        {
            changed = true;
            // if we were completely scrolled to bottom, track bottom
            bool wasAtBottom = Math.Abs(_offset.Y - Math.Max(0, _lastExtent.Height - _lastViewport.Height)) < 0.1;
            if (wasAtBottom && extent.Height > _lastExtent.Height)
            {
                _offset = _offset.WithY(Math.Max(0, extent.Height - _viewport.Height));
            }
        }

        if (_offset.Y > extent.Height - _viewport.Height)
        {
            var clamped = Math.Max(0, extent.Height - _viewport.Height);
            if (Math.Abs(_offset.Y - clamped) > 0.001)
            {
                _offset = _offset.WithY(clamped);
                changed = true;
            }
        }

        if (changed)
        {
            _lastExtent = extent;
            _lastViewport = _viewport;
            ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Live-state extent update; used only for viewport (size) changes, which
    /// happen on the UI thread and cannot race a frame capture.
    /// </summary>
    private void UpdateScrollState()
    {
        var buf = Buffer;
        ApplyExtent(buf == null ? _viewport : ComputeExtent(buf.Rows, buf.ScrollbackCount));
    }
    // -----------------------------------------

    public void ScrollToRow(int visibleRow)
    {
        var buf = Buffer;
        if (buf == null) return;

        int sbCount = buf.ScrollbackCount;
        float targetY = (float)((visibleRow + sbCount) * _cellHeight);
        targetY = Math.Clamp(targetY, 0, (float)Math.Max(0, Extent.Height - _viewport.Height));
        Offset = new Vector(0, targetY);
    }

    public void ScrollToPreviousPrompt()
    {
        var buf = Buffer;
        if (buf == null) return;

        int currentVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - buf.ScrollbackCount;
        var mark = buf.FindNearestPrompt(currentVisibleRow, searchForward: false);
        if (mark == null) return;

        int targetRow = buf.GetPromptVisibleRow(mark.Value);
        ScrollToRow(targetRow);
    }

    public void ScrollToNextPrompt()
    {
        var buf = Buffer;
        if (buf == null) return;

        int currentVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - buf.ScrollbackCount + (int)(_viewport.Height / _cellHeight) - 1;
        var mark = buf.FindNearestPrompt(currentVisibleRow, searchForward: true);
        if (mark == null) return;

        int targetRow = buf.GetPromptVisibleRow(mark.Value);
        ScrollToRow(targetRow);
    }

	protected override void OnSizeChanged(SizeChangedEventArgs e)
	{
		base.OnSizeChanged(e);
        _viewport = e.NewSize;
        _contentDirty = true;
        UpdateScrollState();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		EnsureMetrics();
        // Since we are ILogicalScrollable, we don't need to report the full combined extent as our desired size.
        // We report 0,0 or just the minimum we need so that ScrollViewer handles us correctly as a viewport.
        var buf = Buffer;
        if (buf == null) return base.MeasureOverride(availableSize);
        // But for terminal to take whatever space ScrollViewer gives it (often the full terminal height if short),
        // we can return bounded size or let Arrange handle the viewport.
        var padding = ContentPadding;
        return new Size(
             buf.Columns * _cellWidth + padding.Left + padding.Right,
             Math.Min(availableSize.Height, buf.Rows * _cellHeight + padding.Top + padding.Bottom)
        );
	}


	public override void Render(DrawingContext context)
	{
		var measurement = RenderTelemetry.BeginRender();
		try
		{
			base.Render(context);

			context.FillRectangle(ResolveCachedBackgroundBrush(), new Rect(Bounds.Size));

			if (!IsVisible) return;

			var buffer = Buffer;
			if (buffer == null) return;

			EnsureMetrics();

			// GPU-plan Phase 3: when the quad path is enabled, skip the
			// WriteableBitmap entirely — capture the frame snapshot under a
			// short SyncRoot hold and hand a custom draw operation the
			// compositor's Skia canvas (leased during the render pass). The
			// cursor overlay below still draws as an Avalonia primitive.
			if (s_useLeaseRender && s_useQuadRender && _quadRenderer != null && _quadAtlas != null)
			{
				bool leaseLockTaken = false;
				var snapshot = CaptureRenderSnapshotBounded(buffer, ref leaseLockTaken);
				if (snapshot != null)
				{
					float scale = (float)Math.Max(0.1, _renderScaling);
					float translateX = (float)ContentPadding.Left;
					float translateY = (float)((double)ContentPadding.Top + (double)buffer.ScrollbackCount * _cellHeight - _offset.Y);
					var op = new QuadGlyphDrawOperation(
						_frameComposer!,
						snapshot,
						_quadRenderer,
						new Rect(Bounds.Size),
						scale,
						(float)_cellWidth,
						(float)_cellHeight,
						translateX,
						translateY,
						_cachedBackgroundArgb);
					context.Custom(op);
					DrawCursorOverlay(context, buffer);

					int cursorCell = buffer.CursorRow * buffer.Columns + buffer.CursorCol;
					if (cursorCell != _lastRenderedCursorCell)
					{
						_lastRenderedCursorCell = cursorCell;
						CursorMovedCallback?.Invoke();
					}
					return;
				}
			}

			// Content is rasterized only when the buffer/geometry/colors changed.
			// Cursor blink and shape changes reuse the cached bitmap, so blink
			// never re-rasterizes terminal content.
			if (_contentDirty || _bitmap == null)
			{
				long contentStarted = RenderTelemetry.BeginContentRender();
				bool contentRendered = false;
				try
				{
					contentRendered = RenderToBitmap(buffer);
				}
				finally
				{
					RenderTelemetry.CompleteContentRender(contentStarted, contentRendered);
				}

				// A lock miss keeps the flag set so the retry frame re-rasterizes.
				if (contentRendered)
				{
					_contentDirty = false;
				}
			}

			// Draw cached bitmap to screen. A lock miss deliberately keeps the
			// previous complete frame visible.
			if (_bitmap != null)
			{
				context.DrawImage(_bitmap,
					new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height),
					new Rect(Bounds.Size));
			}

			DrawCursorOverlay(context, buffer);

			// Keep the IME candidate window anchored to the cursor cell.
			int cell = buffer.CursorRow * buffer.Columns + buffer.CursorCol;
			if (cell != _lastRenderedCursorCell)
			{
				_lastRenderedCursorCell = cell;
				CursorMovedCallback?.Invoke();
			}
		}
		finally
		{
			RenderTelemetry.CompleteRender(measurement);
		}
	}

	/// <summary>
	/// Updates the IME preedit overlay state. A non-empty preedit re-rasterizes
	/// the content (the preedit replaces the cell text at the cursor); changes
	/// are composition-paced, not per-frame.
	/// </summary>
	internal void SetPreedit(string? text, int? cursor)
	{
		_preeditText = string.IsNullOrEmpty(text) ? null : text;
		_preeditCursor = cursor ?? -1;
		_contentDirty = true;
		InvalidateVisual();
	}

	/// <summary>
	/// The terminal cursor cell rectangle in canvas-local DIPs (padding +
	/// scroll translate), used by the IME client for the candidate window.
	/// </summary>
	internal Rect GetCursorScreenRect()
	{
		var buffer = Buffer;
		if (buffer == null)
		{
			return new Rect(0, 0, _cellWidth, _cellHeight);
		}

		double x = ContentPadding.Left + buffer.CursorCol * _cellWidth;
		double y = ContentPadding.Top + (buffer.CursorRow + buffer.ScrollbackCount) * _cellHeight - _offset.Y;
		return new Rect(x, y, _cellWidth, _cellHeight);
	}

	/// <summary>
	/// 0-based cursor cell offset; with surrounding text disabled this is only
	/// a stable anchor for the platform's selection bookkeeping.
	/// </summary>
	internal int GetCursorCellOffset()
	{
		var buffer = Buffer;
		return buffer == null ? 0 : buffer.CursorRow * buffer.Columns + buffer.CursorCol;
	}

	/// <summary>
	/// Draws the terminal cursor as a lightweight Avalonia primitive on top of the
	/// cached content bitmap. Same logical geometry as the raster path
	/// (padding + scroll translate), snapped to device pixels.
	/// </summary>
	private void DrawCursorOverlay(DrawingContext context, TerminalBuffer buffer)
	{
		if (!_showCursor) return;

		int curRow = buffer.CursorRow;
		int curCol = buffer.CursorCol;
		if (curRow < 0 || curRow >= buffer.Rows || curCol < 0 || curCol >= buffer.Columns) return;

		double scale = Math.Max(0.1, _renderScaling);
		double leftDip = ContentPadding.Left + curCol * _cellWidth;
		double topDip = ContentPadding.Top + (curRow + buffer.ScrollbackCount) * _cellHeight - _offset.Y;
		double cellWDip = _cellWidth;
		double cellHDip = _cellHeight;

		double left = Math.Round(leftDip * scale) / scale;
		double top = Math.Round(topDip * scale) / scale;
		double right = Math.Round((leftDip + cellWDip) * scale) / scale;
		double bottom = Math.Round((topDip + cellHDip) * scale) / scale;
		double width = Math.Max(0, right - left);
		double height = Math.Max(0, bottom - top);

		var brush = ResolveCachedCursorBrush();
		switch (CursorShape)
		{
			case TerminalCursorShape.Block:
				context.FillRectangle(brush, new Rect(left, top, width, height));
				break;
			case TerminalCursorShape.Beam:
				double beamW = Math.Max(1.0 / scale, Math.Round(cellWDip * 0.08 * scale) / scale);
				context.FillRectangle(brush, new Rect(left, top, beamW, height));
				break;
			case TerminalCursorShape.Underline:
				double ulH = Math.Max(1.0 / scale, Math.Round(cellHDip * 0.08 * scale) / scale);
				context.FillRectangle(brush, new Rect(left, bottom - ulH, width, ulH));
				break;
		}
	}

	private bool RenderToBitmap(TerminalBuffer buffer)
	{
		// B-lite (docs/architecture/AvaloniaOptimizationPlan.md §10.7): hold
		// SyncRoot only for a bounded memcpy snapshot of the render state, then
		// rasterize from the immutable snapshot without the lock. The UI thread
		// never blocks the PTY writer for the whole raster (~2.8 ms), only for
		// the copy (~1 ms), and the raster can never race a partial parse.
		bool lockTaken = false;
		using var snapshot = CaptureRenderSnapshotBounded(buffer, ref lockTaken);
		if (snapshot == null)
		{
			return false;
		}

		// Backing surface is physical pixels: round(Bounds * RenderScaling).
		// Bounds stay DIPs for all layout/scroll/hit-test math; the single
		// canvas.Scale below maps logical geometry onto this surface.
		double scale = Math.Max(0.1, _renderScaling);
		int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
		int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

		if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
		{
			_bitmap?.Dispose();
			_bitmap = new WriteableBitmap(
				new PixelSize(w, h),
				new Vector(96.0 * scale, 96.0 * scale),
				PixelFormat.Bgra8888);
			RenderTelemetry.RecordBitmapRecreation();
			_forceFullRender = true;
		}
		RenderTelemetry.RecordBufferState(
			buffer.Generation,
			_renderScaling,
			w,
			h);

		using var locked = _bitmap.Lock();
		var info = new SKImageInfo(locked.Size.Width, locked.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
		using var surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
		DrawContentToSkiaCanvas(surface.Canvas, snapshot, scale);
		return true;
	}


	/// <summary>
	/// Acquires the bounded SyncRoot wait, runs MarkRender, and captures the
	/// render snapshot (cell arenas + styles + generations + visible scrollback
	/// text). On a lock miss returns null (the presentation gate retries).
	/// </summary>
	private RenderSnapshot? CaptureRenderSnapshotBounded(TerminalBuffer buffer, ref bool lockTaken)
	{
		try
		{
			// Never block the UI thread indefinitely on this lock: under a
			// sustained output firehose (e.g. `yes`), the PTY-write thread
			// re-acquires the same lock immediately after releasing it (there's
			// always a next chunk ready), and Monitor's lock isn't FIFO-fair —
			// the writer can starve this thread for as long as the burst lasts,
			// freezing the entire UI (input, resize, everything runs on this
			// thread). Bound the wait and skip this frame (the caller redraws
			// the last cached bitmap) if the buffer is busy; the presentation
			// gate retries on the next tick.
			//
			// The handshake flag lets the writer yield between sub-chunks so
			// this bounded wait actually wins the lock during a burst instead
			// of timing out on every attempt.
			buffer.ReaderWaiting = true;
			System.Threading.Monitor.TryEnter(buffer.SyncRoot, 4, ref lockTaken);
			if (!lockTaken)
			{
				RenderTelemetry.RecordBufferLockMiss();
				// Explicit reschedule: the owner requests one more animation
				// frame so this skipped content is presented on the next tick.
				FrameRetryRequested?.Invoke();
				return null;
			}

			try { buffer.MarkRender(); } catch { }

			int sbCount = buffer.ScrollbackCount;
			int startVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - sbCount;
			int endVisibleRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - sbCount;
			int sbStart = Math.Max(-sbCount, startVisibleRow);
			int sbEnd = Math.Min(-1, endVisibleRow);
			return buffer.CaptureRenderSnapshotVisible(sbStart, sbEnd);
		}
		finally
		{
			if (lockTaken)
				System.Threading.Monitor.Exit(buffer.SyncRoot);
			buffer.ReaderWaiting = false;
		}
	}

	/// <summary>
	/// Rasterizes the terminal content into an arbitrary Skia canvas from an
	/// <see cref="IRenderSource"/> — the live <see cref="TerminalBuffer"/>
	/// (renderer holds SyncRoot) or, on the shipped B-lite path, a
	/// <see cref="RenderSnapshot"/> captured under a short lock. The caller
	/// supplies the logical-to-physical scale; all geometry stays in DIPs.
	/// </summary>
	private void DrawContentToSkiaCanvas(SKCanvas canvas, IRenderSource buffer, double scale)
	{
		bool altChanged = buffer.IsAlternateScreenActive != _lastBufferWasAlternate;
		if (_frameComposer != null && altChanged)
		{
			_frameComposer.ResetCaches();
			_lastBufferWasAlternate = buffer.IsAlternateScreenActive;
		}

		int sbCount = buffer.ScrollbackCount;
		// Posted, not synchronous: ApplyExtent can invalidate a visual
		// (via ScrollInvalidated -> ScrollContentPresenter -> InvalidateMeasure),
		// and Avalonia throws if a visual is invalidated while a render pass is
		// in progress (this method runs inside one). Must defer to after this
		// pass completes. But Background is the *lowest* active dispatcher
		// priority and Render (the compositor's own pass, scheduled every
		// frame) is the *highest* - under continuous rendering a Background
		// post is starved indefinitely, so the "follow to bottom" offset
		// adjustment never ran and new output never scrolled into view. Render
		// priority gets a fair turn alongside the render work instead.
		// Geometry is captured here under SyncRoot; the delegate applies the
		// latest captured values, coalesced to at most one update per frame.
		_pendingExtentRows = buffer.Rows;
		_pendingExtentSbCount = sbCount;
		if (!_extentUpdatePosted)
		{
			_extentUpdatePosted = true;
			Dispatcher.UIThread.Post(() =>
			{
				_extentUpdatePosted = false;
				ApplyExtent(ComputeExtent(_pendingExtentRows, _pendingExtentSbCount));
			}, DispatcherPriority.Render);
		}

		// One logical-to-physical transform: everything below (padding,
		// scroll translate, cell geometry, selection) stays in DIPs.
		if (_frameComposer != null)
		{
			_frameComposer.DeviceScale = (float)scale;
		}
		canvas.SetMatrix(SKMatrix.CreateScale((float)scale, (float)scale));

		if (ContentPadding.Left != 0 || ContentPadding.Top != 0)
			canvas.Translate((float)ContentPadding.Left, (float)ContentPadding.Top);

		canvas.Translate(0, (float)(sbCount * _cellHeight - _offset.Y));

		// Step C (IncrementalScrollRendering.md §4.5): when nothing about the
		// viewport moved, patch only the rows whose identity generation
		// changed instead of clearing and re-rendering the whole surface. The
		// gate is strict: offset, scrollback count, selection, preedit, and
		// alt-screen state must all match the last full render, so the pixels
		// outside the dirty rows are provably identical. Anything else falls
		// back to the full path (the pre-incremental behavior).
		bool dirtyPath = false;
		if (_frameComposer != null)
		{
			int startVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - sbCount;
			int endVisibleRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - sbCount;
			startVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, startVisibleRow));
			endVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, endVisibleRow));

			int composerStart = Math.Max(0, startVisibleRow);
			int composerEnd = Math.Max(0, Math.Min(buffer.Rows - 1, endVisibleRow));

			if (++_renderDiag % 25 == 1)
				Console.WriteLine("[quad-diag] n=" + _renderDiag + " rows=" + buffer.Rows + " cols=" + buffer.Columns + " useQuad=" + _frameComposer.UseQuadGlyphs);
			if (composerStart <= composerEnd && SkPaint != null && SkFont != null)
				dirtyPath = TryRenderDirtyPath(canvas, buffer, sbCount, altChanged, composerStart, composerEnd);

			if (!dirtyPath)
			{
				// Full render: clear the bitmap and re-render everything. An
				// earlier incremental path here (viewport-shift memmove,
				// buffer-scroll replay, dirty-row culling) traded this full
				// redraw for partial updates, but proved unsafe in practice -
				// it corrupted glyphs on manual scroll and, separately, its
				// offset-tracking starved the "follow new output to bottom"
				// update. Full render is ~11.7ms at 73x136 (bench-verified),
				// well within a frame budget. The incremental primitives were
				// removed (see StateCoordinationPlan R3); Step C above rebuilds
				// the safe subset: per-row culling with a strict no-motion gate.
				canvas.Clear(_cachedBackgroundArgb);

				if (composerStart <= composerEnd && SkPaint != null && SkFont != null)
					_frameComposer.RenderTo(canvas, buffer, SkPaint, SkFont, (float)_cellWidth, (float)_cellHeight, composerStart, composerEnd);

				int sbStart = Math.Max(-sbCount, startVisibleRow);
				int sbEnd = Math.Min(-1, endVisibleRow);

				if (sbStart <= sbEnd && SkPaint != null && SkFont != null)
				{
					var font = SkFont;
					var fm = font.Metrics;
					float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
					float baselineOffset = (float)(_cellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);

					for (int r = sbStart; r <= sbEnd; r++)
					{
						int idx = r + sbCount;
						idx = Math.Max(0, Math.Min(sbCount - 1, idx));
						var text = buffer.GetScrollbackLineText(idx);
						if (string.IsNullOrEmpty(text)) continue;
						float y = (float)(r * _cellHeight + baselineOffset);
						canvas.DrawText(SKTextBlob.Create(text, font), 0, y, SkPaint);
					}
				}
			}

			_forceFullRender = false;
			RecordLastRenderedState(buffer, sbCount);
		}
		else
		{
			canvas.Clear(_cachedBackgroundArgb);
		}

		// IME preedit overlay: draws the active composition at the cursor cell,
		// replacing the underlying cell text, with an underline marking the
		// composed region. Skipped on the dirty path (the gate rejects preedit).
		if (!dirtyPath && !string.IsNullOrEmpty(_preeditText) && SkPaint != null && SkFont != null && buffer != null)
		{
			int curRow = buffer.CursorRow;
			int curCol = buffer.CursorCol;
			if (curRow >= 0 && curRow < buffer.Rows && curCol >= 0 && curCol < buffer.Columns)
			{
				float cellW = (float)_cellWidth;
				float cellH = (float)_cellHeight;
				float x = curCol * cellW;
				float y = curRow * cellH;

				canvas.Save();
				canvas.ClipRect(SKRect.Create(0, y, buffer.Columns * cellW, cellH));

				var font = SkFont;
				var fm = font.Metrics;
				float baseline = y + Math.Abs(fm.Ascent);

				var prevColor = SkPaint.Color;
				SkPaint.Color = SKColors.White.WithAlpha(230);
				canvas.DrawText(SKTextBlob.Create(_preeditText, font), x, baseline, SkPaint);
				SkPaint.Color = prevColor;

				// Composition underline.
				using var underline = new SKPaint
				{
					IsAntialias = false,
					Style = SKPaintStyle.Fill,
					Color = SKColors.White.WithAlpha(200),
				};
				float ulY = y + cellH - Math.Max(1f, cellH * 0.08f);
				float textW = Math.Max(cellW, font.MeasureText(_preeditText));
				canvas.DrawRect(new SKRect(x, ulY, Math.Min(x + textW, (curCol + 1) * cellW), ulY + Math.Max(1f, cellH * 0.06f)), underline);
				canvas.Restore();
			}
		}

		// Draw selection overlay (drawn into the content; split from content
		// is deferred until the cursor overlay passes pixel tests). Skipped on
		// the dirty path (the gate rejects selection changes).
		if (!dirtyPath && !_selectionRange.IsEmpty)
		{
			int visStart = (int)Math.Floor(_offset.Y / _cellHeight) - sbCount;
			int visEnd = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - sbCount;
			visStart = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, visStart));
			visEnd = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, visEnd));

			int drawStart = Math.Max(_selectionRange.StartRow, visStart);
			int drawEnd = Math.Min(_selectionRange.EndRow, visEnd);

			if (drawStart <= drawEnd)
			{
				if (_selectionPaint == null)
				{
					var selColor = SKColors.White.WithAlpha(95);
					if (SelectionBrush is ISolidColorBrush scb)
						selColor = new SKColor(scb.Color.R, scb.Color.G, scb.Color.B, scb.Color.A);
					_selectionPaint = new SKPaint
					{
						Color = selColor,
						Style = SKPaintStyle.Fill,
						IsAntialias = false
					};
				}

				float cellH = (float)_cellHeight;
				float cellW = (float)_cellWidth;
				int cols = buffer.Columns;

				for (int r = drawStart; r <= drawEnd; r++)
				{
					int sCol = r == _selectionRange.StartRow ? _selectionRange.StartColumn : 0;
					int eCol = r == _selectionRange.EndRow ? _selectionRange.EndColumn : cols - 1;
					float x = sCol * cellW;
					float y = r * cellH;
					float rectW = (eCol - sCol + 1) * cellW;
					canvas.DrawRect(SnapRectToDevice(new SKRect(x, y, x + rectW, y + cellH), scale), _selectionPaint);
				}
			}
		}

		// Cursor is drawn as an Avalonia overlay after the content (see
		// DrawCursorOverlay) so blink never re-rasterizes content.

		// Debug overlay (full path only; diagnostic, not content)
		if (!dirtyPath && ShowDebugOverlay && SkPaint != null)
		{
			canvas.Save();
			if (_debugTextPaint == null || _debugBgPaint == null || _debugFont == null)
			{
				_debugTextPaint = new SKPaint
				{
					Color = SKColors.Lime,
					IsAntialias = true,
				};
				_debugFont = new SKFont(SKTypeface.Default, 13f);
				_debugBgPaint = new SKPaint
				{
					Style = SKPaintStyle.Fill,
					Color = new SKColor(0, 0, 0, 200),
				};
			}

			var debugFont = _debugFont!;
			var debugTextPaint = _debugTextPaint!;
			var debugBgPaint = _debugBgPaint!;
			var debugInfo = buffer.GetDebugInfo();
			float y = 4f;
			canvas.DrawRect(0, 0, canvas.DeviceClipBounds.Width / (float)scale, 20, debugBgPaint);
			canvas.DrawText(SKTextBlob.Create(debugInfo, debugFont), 4, y + 14, debugTextPaint);
			canvas.Restore();
		}

		canvas.Flush();
	}

	/// <summary>
	/// Attempts the Step C per-row dirty redraw. Returns true when the dirty
	/// path was taken (or nothing changed and the retained bitmap is already
	/// current); false falls back to the full render. Strict gate:
	/// offset, scrollback count, selection, preedit, alt-screen state, and a
	/// matching generation array must all be unchanged since the last raster.
	/// </summary>
	private bool TryRenderDirtyPath(SKCanvas canvas, IRenderSource buffer, int sbCount, bool altChanged, int composerStart, int composerEnd)
	{
		if (_forceFullRender) return false;
		if (altChanged) return false;
		if (_selectionRange != _lastRenderedSelection) return false;
		if (_preeditText != null || _lastRenderedPreeditActive) return false;
		if (Math.Abs(_offset.Y - _lastRenderedOffsetY) > 0.001) return false;
		if (sbCount != _lastRenderedSbCount) return false;

		var gens = buffer.RowGenerations;
		if (gens.IsEmpty) return false;
		if (_lastRowGenerations == null || _lastRowGenerations.Length != gens.Length) return false;

		_dirtyRowScratch.Clear();
		for (int r = composerStart; r <= composerEnd; r++)
		{
			if (gens[r] != _lastRowGenerations[r])
				_dirtyRowScratch.Add(r);
		}

		int visibleRowCount = composerEnd - composerStart + 1;
		if (_dirtyRowScratch.Count == 0) return true; // bitmap already current
		if (_dirtyRowScratch.Count >= visibleRowCount) return false; // whole screen -> full render

		_dirtyRowScratch.Sort();
		_frameComposer!.RenderDirty(
			canvas,
			buffer,
			SkPaint!,
			SkFont!,
			(float)_cellWidth,
			(float)_cellHeight,
			_cachedBackgroundArgb,
			composerStart,
			composerEnd,
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dirtyRowScratch));
		return true;
	}

	/// <summary>
	/// Captures the raster-time viewport state the dirty path gates on.
	/// Called after every successful content raster (full or dirty).
	/// </summary>
	private void RecordLastRenderedState(IRenderSource buffer, int sbCount)
	{
		_lastRenderedOffsetY = _offset.Y;
		_lastRenderedSbCount = sbCount;
		_lastRenderedSelection = _selectionRange;
		_lastRenderedPreeditActive = _preeditText != null;

		var gens = buffer.RowGenerations;
		if (_lastRowGenerations == null || _lastRowGenerations.Length != gens.Length)
		{
			_lastRowGenerations = gens.ToArray();
		}
		else
		{
			gens.CopyTo(_lastRowGenerations);
		}
	}

	public void OnBufferUpdated(TerminalBuffer buffer)
	{
		if (buffer == null) return;
		_contentDirty = true;
		HandleBufferGeometryChange(buffer);
		InvalidateVisual();
	}

	private void HandleBufferGeometryChange(TerminalBuffer buffer)
	{
		var geometryChanged = buffer.Rows != _lastKnownBufferRows ||
			buffer.Columns != _lastKnownBufferColumns;

		_lastKnownBufferRows = buffer.Rows;
		_lastKnownBufferColumns = buffer.Columns;

		if (geometryChanged)
		{
			InvalidateMeasure();
			InvalidateArrange();
		}

		// No extent update here: the frame render captures geometry under
		// SyncRoot and applies it via one coalesced posted update (see
		// RenderToBitmap). The pre-R2 synchronous call here raced the posted
		// one with a live re-read of ScrollbackCount.
	}

	public void RequestFrame()
	{
		if (!IsVisible) return;
		RenderTelemetry.RecordFrameRequest();
		InvalidateVisual();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attachedTopLevel = TopLevel.GetTopLevel(this);
		if (_attachedTopLevel != null)
		{
			_attachedTopLevel.ScalingChanged += OnTopLevelScalingChanged;
		}
		RuntimeSettings.Changed += OnRuntimeSettingsChanged;
		App.ThemeUpdated += OnAppThemeChanged;
		RefreshCachedBrushes();
		OnRuntimeSettingsChanged(null, EventArgs.Empty); // apply current runtime settings
		InvalidateVisual();
	}

	/// <summary>
	/// A display-scale transition changes the physical backing dimensions even
	/// when the DIP bounds stay the same, so the bitmap and metrics must be
	/// rebuilt. The invalidation happens once; the render pass recreates the
	/// backing surface on demand when its physical size no longer matches.
	/// </summary>
	private void OnTopLevelScalingChanged(object? sender, EventArgs e)
	{
		if (!IsVisible) return;
		_metricsDirty = true;
		_contentDirty = true;
		InvalidateMeasure();
		InvalidateVisual();
		RequestFrame();
	}

	private void OnRuntimeSettingsChanged(object? sender, EventArgs e)
	{
		if (!IsVisible) return;
		var rs = RuntimeSettings.Current;

		if (rs.FontFamily != null)
		{
			FontFamily = new FontFamily(rs.FontFamily);
			CachedPrimaryTypeface.Clear();
			CachedFallbackTypefaces.Clear();
		}
		if (rs.FontSize.HasValue)
		{
			FontSize = rs.FontSize.Value;
			CachedPrimaryTypeface.Clear();
			CachedFallbackTypefaces.Clear();
		}
		if (rs.CellPadding.HasValue)
			CellPadding = rs.CellPadding.Value;
		if (rs.ContentPaddingLeft.HasValue || rs.ContentPaddingTop.HasValue ||
			rs.ContentPaddingRight.HasValue || rs.ContentPaddingBottom.HasValue)
		{
			ContentPadding = new Thickness(
				rs.ContentPaddingLeft ?? Generated.Config.ContentPaddingLeft,
				rs.ContentPaddingTop ?? Generated.Config.ContentPaddingTop,
				rs.ContentPaddingRight ?? Generated.Config.ContentPaddingRight,
				rs.ContentPaddingBottom ?? Generated.Config.ContentPaddingBottom);
		}

		// Update default text color from runtime foreground
		if (rs.Foreground != null && SkPaint != null)
		{
			ParseHexColor(rs.Foreground, out var fg);
			SkPaint.Color = fg;
		}

		// Update selection brush color
		if (rs.SelectionColor != null)
		{
			ParseHexColor(rs.SelectionColor, out var sel);
			SelectionBrush = new SolidColorBrush(
				global::Avalonia.Media.Color.FromArgb(sel.Alpha, sel.Red, sel.Green, sel.Blue));
			_selectionPaint = null;
		}

		_metricsDirty = true;
		RefreshCachedBrushes();
		_contentDirty = true;
		InvalidateMeasure();
		InvalidateVisual();
	}

	private void EnsureMetrics()
	{
		var scaling = GetRenderScaling();
		if (Math.Abs(scaling - _renderScaling) > 0.001)
		{
			_renderScaling = scaling;
			_metricsDirty = true;
		}

		if (!_metricsDirty && SkPaint != null) return;

		// Let the GC clean up the old SKPaint, because the render thread might still be drawing with it.
		// Disposing it here can cause a segfault (access violation) if the render thread is mid-draw.
		var fontSize = double.IsNaN(FontSize) || FontSize <= 0 ? 13.0 : FontSize;
		var scale = Math.Max(0.1, _renderScaling);
		var scaledFontSize = Math.Max(1f, (float)(fontSize * scale));
		var typeface = ResolveTerminalTypeface();
		var defaultFg = SKColors.White;
		var fgHex = RuntimeSettings.Current.Foreground;
		if (fgHex != null) ParseHexColor(fgHex, out defaultFg);

		SkPaint = new SKPaint
		{
			IsAntialias = true,
			Color = defaultFg,
		};

		SkFont?.Dispose();
		SkFont = new SKFont(typeface, scaledFontSize)
		{
			Subpixel = true,
			Hinting = SKFontHinting.Full,
			Edging = SKFontEdging.SubpixelAntialias,
		};

		var fm = SkFont.Metrics;
		float glyphHeight = Math.Max(scaledFontSize, Math.Abs(fm.Descent) + Math.Abs(fm.Ascent));
		float glyphAdvance = Math.Max(0.5f, fm.AverageCharacterWidth);
		var measuredW = Math.Max(1f, SkFont.MeasureText("W"));
		glyphAdvance = Math.Max(glyphAdvance, measuredW);

		var padding = Math.Max(0.0, CellPadding);
		_cellWidth = (float)Math.Round(Math.Max(4, glyphAdvance / (float)scale + (float)(padding * 2.0)));
		_cellHeight = (float)Math.Round(Math.Max((float)fontSize, glyphHeight / (float)scale + (float)(padding * 2.0)));

		// Resolve fallback typefaces and set on composer
		var fallbackTypefaces = ResolveAllTypefaces(scaledFontSize);
		if (_frameComposer != null)
			_frameComposer.FallbackTypefaces = fallbackTypefaces;

		if (s_useQuadRender)
		{
			EnsureQuadRenderer(typeface, scaledFontSize);
		}

		_contentDirty = true;

		_metricsDirty = false;

		// Font/typeface changes invalidate the composer's per-row classification
		// cache (TypefaceIndex resolution depends on the current font list).
		try { _frameComposer?.ResetCaches(); } catch { }
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == IsVisibleProperty)
		{
			if (IsVisible)
			{
				InvalidateVisual();
				RequestFrame();
			}
		}

		if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
		{
			_metricsDirty = true;
			InvalidateMeasure();
			InvalidateVisual();
		}

		if (change.Property == BufferProperty)
		{
			var buf = Buffer;
			if (buf != null)
			{
				EnsureMetrics();
				HandleBufferGeometryChange(buf);
				// Ensure we have a composer. If one already exists, reset its caches
				// for the new buffer (cheaper than recreating the object). Track
				// alternate-screen state for later detection in Render.
				if (_frameComposer == null)
				{
					_frameComposer = new TerminalFrameComposer();
					_textShaper = new TextShaper();
					_frameComposer.TextShaper = _textShaper;
					_frameComposer.ShapedRunCache = SharedShapedRunCache;
				}
				else
				{
					_frameComposer.ResetCaches();
				}

				// Re-apply the quad renderer to the new composer instance: the
				// BufferProperty handler can run after EnsureMetrics created it.
				if (_quadAtlas != null && _quadRenderer != null)
				{
					_frameComposer.GlyphAtlas = _quadAtlas;
					_frameComposer.QuadRenderer = _quadRenderer;
					_frameComposer.UseQuadGlyphs = true;
				}

				_lastBufferWasAlternate = buf.IsAlternateScreenActive;
				
				// Force re-render with new buffer
				_contentDirty = true;
				InvalidateVisual();
				RequestFrame();
			}
			else
			{
				_lastKnownBufferRows = -1;
				_lastKnownBufferColumns = -1;
				// _frameComposer?.Dispose(); removed for safety
				_frameComposer = null;
			}
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		if (_attachedTopLevel != null)
		{
			_attachedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
			_attachedTopLevel = null;
		}
		RuntimeSettings.Changed -= OnRuntimeSettingsChanged;
		App.ThemeUpdated -= OnAppThemeChanged;
		
        // Release per-view render state now that this canvas is leaving the tree.
        try { _frameComposer?.Dispose(); } catch { }
        _frameComposer = null;
        _textShaper?.Dispose();
        _textShaper = null;
		ReleaseQuadRenderer();
		
		// Release Skia paint resources
		if (SkPaint != null)
		{
			try { SkPaint.Dispose(); } catch { }
			SkPaint = null;
		}
		if (SkFont != null)
		{
			try { SkFont.Dispose(); } catch { }
			SkFont = null;
		}
		
		// Dispose debug overlay paints
		_debugTextPaint?.Dispose();
		_debugTextPaint = null;
		_debugBgPaint?.Dispose();
		_debugBgPaint = null;
		_debugFont?.Dispose();
		_debugFont = null;

		// Dispose bitmap
		_bitmap?.Dispose();
		_bitmap = null;
		
		// Reset metrics to ensure fresh calculation on reattach
		_metricsDirty = true;
		_cellWidth = 8;
		_cellHeight = 16;
		_contentDirty = true;
		_cachedBackgroundBrush = null;
		_cachedCursorBrush = null;
	}

	/// <summary>
	/// (Re)acquires the A8 atlas + quad renderer for the current font metrics
	/// and switches the composer to the quad glyph path.
	/// </summary>
	private void EnsureQuadRenderer(SKTypeface typeface, float scaledFontSize)
	{
		if (_quadAtlas != null && ReferenceEquals(_quadAtlas.Typeface, typeface) && Math.Abs(_quadAtlas.TextSize - scaledFontSize) < 0.01f)
			return;

		ReleaseQuadRenderer();
		var atlas = GlyphAtlasService.GetOrCreateAtlas(typeface, scaledFontSize);
		GlyphAtlasService.AcquireAtlas(atlas);
		_quadAtlas = atlas;
		_quadRenderer = new QuadGlyphRenderer(atlas);
		if (_frameComposer != null)
		{
			_frameComposer.GlyphAtlas = atlas;
			_frameComposer.QuadRenderer = _quadRenderer;
			_frameComposer.UseQuadGlyphs = true;
		}
	}

	private void ReleaseQuadRenderer()
	{
		if (_frameComposer != null)
		{
			_frameComposer.GlyphAtlas = null;
			_frameComposer.QuadRenderer = null;
			_frameComposer.UseQuadGlyphs = false;
		}
		_quadRenderer?.Dispose();
		_quadRenderer = null;
		if (_quadAtlas != null)
		{
			GlyphAtlasService.ReleaseAtlas(_quadAtlas);
			_quadAtlas = null;
		}
	}

	private IBrush ResolveResourceBrush(IResourceDictionary? resources, string key, IBrush fallback)
	{
		if (resources != null && resources.TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
		{
			return brush;
		}

		return fallback;
	}

	private IBrush ResolveCachedBackgroundBrush()
	{
		if (_cachedBackgroundBrush == null)
		{
			RefreshCachedBrushes();
		}
		return _cachedBackgroundBrush!;
	}

	private IBrush ResolveCachedCursorBrush()
	{
		if (_cachedCursorBrush == null)
		{
			RefreshCachedBrushes();
		}
		return _cachedCursorBrush!;
	}

	/// <summary>
	/// Re-resolves theme brushes. Called on attach, runtime-settings changes,
	/// and theme changes — never during Render.
	/// </summary>
	private void RefreshCachedBrushes()
	{
		var resources = Application.Current?.Resources;
		var bg = ResolveResourceBrush(resources, "TerminalBackground", Brushes.Black);
		_cachedBackgroundBrush = bg;
		_cachedBackgroundArgb = bg is ISolidColorBrush solid
			? new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, solid.Color.A)
			: SKColors.Black;

		// Theme-aware cursor: the theme foreground at the same translucency as
		// the previous hard-coded white, so contrast follows the palette.
		var fg = ResolveResourceBrush(resources, "TerminalForeground", Brushes.White);
		if (fg is ISolidColorBrush fgSolid)
		{
			var c = fgSolid.Color;
			_cachedCursorBrush = new SolidColorBrush(new Avalonia.Media.Color(180, c.R, c.G, c.B));
		}
		else
		{
			_cachedCursorBrush = Brushes.White;
		}
	}

	private void OnAppThemeChanged()
	{
		if (!IsVisible) return;
		RefreshCachedBrushes();
		_contentDirty = true;
		InvalidateVisual();
	}

	private static string BuildFontCacheKey()
	{
		var fontFamily = RuntimeSettings.Current.FontFamily;
		var size = RuntimeSettings.Current.FontSize ?? double.NaN;
		return $"{fontFamily ?? "default"}|{size:F1}";
	}

	private SKTypeface ResolveTerminalTypeface()
	{
		var key = BuildFontCacheKey();
		if (CachedPrimaryTypeface.TryGetValue(key, out var cached))
			return cached;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		SKTypeface? result = null;

		foreach (var candidate in EnumerateFontCandidates())
		{
			if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
				continue;

			if (!TryResolveTypeface(candidate, out var typeface))
				continue;

			if (FontHelpers.IsLikelySymbolFontName(candidate) || FontHelpers.IsLikelySymbolFontName(typeface.FamilyName))
			{
				typeface.Dispose();
				continue;
			}

			result = typeface;
			break;
		}

		result ??= SKTypeface.Default;
		CachedPrimaryTypeface[key] = result;
		s_lastFontCacheKey = key;
		return result;
	}

	private List<SKTypeface> ResolveAllTypefaces(float textSize)
	{
		var key = BuildFontCacheKey();
		if (CachedFallbackTypefaces.TryGetValue(key, out var cached))
			return cached;

		var result = new List<SKTypeface>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var candidate in EnumerateFontCandidates())
		{
			if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
				continue;
			if (!TryResolveTypeface(candidate, out var typeface))
				continue;
			if (FontHelpers.IsLikelySymbolFontName(candidate) || FontHelpers.IsLikelySymbolFontName(typeface.FamilyName))
			{
				typeface.Dispose();
				continue;
			}
			result.Add(typeface);
		}

		foreach (var emojiName in EmojiFontFamilies)
		{
			if (!seen.Add(emojiName))
				continue;
			if (TryResolveTypeface(emojiName, out var typeface))
				result.Add(typeface);
		}

		CachedFallbackTypefaces[key] = result;
		s_lastFontCacheKey = key;
		return result;
	}

	private IEnumerable<string> EnumerateFontCandidates()
	{
		if (!string.IsNullOrWhiteSpace(FontFamily?.Name))
		{
			yield return FontFamily!.Name;
		}

		var configured = Generated.Config.FontFamily;
		if (!string.IsNullOrWhiteSpace(configured))
		{
			var configuredCandidates = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < configuredCandidates.Length; i++)
			{
				yield return configuredCandidates[i];
			}
		}

		for (int i = 0; i < MonospaceFallbackFamilies.Length; i++)
		{
			yield return MonospaceFallbackFamilies[i];
		}
	}

	private static bool TryResolveTypeface(string familyName, out SKTypeface typeface)
	{
		typeface = null!;

		try
		{
			var matched = SKFontManager.Default.MatchFamily(familyName);
			if (matched == null)
			{
				return false;
			}

			typeface = matched;
			return true;
		}
		catch
		{
			return false;
		}
	}


	private double GetRenderScaling()
	{
		return TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
	}

	/// <summary>
	/// Snaps a DIP coordinate to the nearest device pixel and returns it in
	/// DIPs, so geometry drawn under the canvas scale transform lands on
	/// whole device pixels at fractional display scales (1.25x, 1.5x).
	/// </summary>
	private static float SnapDipToDevice(float dip, double scale)
	{
		return (float)(Math.Round(dip * scale) / Math.Max(0.1, scale));
	}

	private static SKRect SnapRectToDevice(SKRect rect, double scale)
	{
		float left = SnapDipToDevice(rect.Left, scale);
		float top = SnapDipToDevice(rect.Top, scale);
		float right = SnapDipToDevice(rect.Right, scale);
		float bottom = SnapDipToDevice(rect.Bottom, scale);
		return SKRect.Create(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
	}

	private static bool ParseHexColor(string hex, out SKColor color)
	{
		color = SKColors.White;
		try
		{
			hex = hex.TrimStart('#');
			if (hex.Length == 6) hex = "FF" + hex;
			if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var argb))
			{
				color = new SKColor(
					(byte)((argb >> 16) & 0xFF),
					(byte)((argb >> 8) & 0xFF),
					(byte)(argb & 0xFF),
					(byte)((argb >> 24) & 0xFF));
				return true;
			}
		}
		catch { }
		return false;
	}
}
