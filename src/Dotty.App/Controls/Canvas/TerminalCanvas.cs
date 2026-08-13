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
using Dotty.App.Discovery;
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
	private GlyphAtlas? _glyphAtlas;
	private GlyphDiscovery? _glyphDiscovery;
	private TerminalFrameComposer? _frameComposer;
	private TextShaper? _textShaper;
	private static readonly ShapedRunCache SharedShapedRunCache = new();

	// Global font resolution cache shared across all TerminalCanvas instances.
	// Key is "{FontFamily}|{TextSize:F1}".  Invalidated when font settings change.
	private static readonly ConcurrentDictionary<string, SKTypeface> CachedPrimaryTypeface = new();
	private static readonly ConcurrentDictionary<string, List<SKTypeface>> CachedFallbackTypefaces = new();
	private static string? s_lastFontCacheKey;

	private bool _lastBufferWasAlternate = false;
	private int _lastKnownBufferRows = -1;
	private int _lastKnownBufferColumns = -1;
	private ulong[]? _lastRowGenerations;

	private double _renderScaling = 1.0;
	private TopLevel? _attachedTopLevel;
	private GlyphRasterizationOptions _glyphRasterizationOptions = new();
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

		var sb = new System.Text.StringBuilder(4096);
		int startRow = Math.Max(0, (int)Math.Floor(_offset.Y / _cellHeight) - buffer.ScrollbackCount);
		int endRow = Math.Min(buffer.Rows - 1, (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - buffer.ScrollbackCount);
		for (int r = startRow; r <= endRow && sb.Length < 16_384; r++)
		{
			sb.Append(buffer.GetRowText(r));
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
		bool lockTaken = false;
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
			System.Threading.Monitor.TryEnter(buffer.SyncRoot, 4, ref lockTaken);
			if (!lockTaken)
			{
				RenderTelemetry.RecordBufferLockMiss();
				// Explicit reschedule: the owner requests one more animation
				// frame so this skipped content is presented on the next tick.
				FrameRetryRequested?.Invoke();
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
			}
			RenderTelemetry.RecordBufferState(
				buffer.Generation,
				_renderScaling,
				w,
				h);

			using var locked = _bitmap.Lock();
			var info = new SKImageInfo(locked.Size.Width, locked.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
			DrawContentToSkiaCanvas(surface.Canvas, buffer, scale);
			return true;
		}
		finally
		{
			if (lockTaken)
				System.Threading.Monitor.Exit(buffer.SyncRoot);
		}
	}

	/// <summary>
	/// Rasterizes the terminal content into an arbitrary Skia canvas — either
	/// the backing bitmap surface or, for the Experiment A prototype, the
	/// compositor's lease canvas. The caller holds buffer.SyncRoot and supplies
	/// the logical-to-physical scale; all geometry stays in DIPs.
	/// </summary>
	private void DrawContentToSkiaCanvas(SKCanvas canvas, TerminalBuffer buffer, double scale)
	{
		if (_frameComposer != null && buffer.IsAlternateScreenActive != _lastBufferWasAlternate)
		{
			_frameComposer.ResetCaches();
			_lastBufferWasAlternate = buffer.IsAlternateScreenActive;
		}

		try
		{
			buffer.MarkRender();
		}
		catch { }

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

		// Always full render: clear the bitmap and re-render everything. An
		// earlier incremental path here (viewport-shift memmove, buffer-scroll
		// replay, dirty-row culling) traded this full redraw for partial
		// updates, but proved unsafe in practice - it corrupted glyphs on
		// manual scroll and, separately, its offset-tracking starved the
		// "follow new output to bottom" update. Full render is ~11.7ms at
		// 73x136 (bench-verified), well within a frame budget. The incremental
		// primitives were removed (see StateCoordinationPlan R3); the design
		// doc IncrementalScrollRendering.md records how to rebuild them with
		// the pixel-diff harness if a future attempt is made.
		canvas.Clear(_cachedBackgroundArgb);

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

		if (_frameComposer != null)
		{
			int startVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - sbCount;
			int endVisibleRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - sbCount;
			startVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, startVisibleRow));
			endVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, endVisibleRow));

			int composerStart = Math.Max(0, startVisibleRow);
			int composerEnd = Math.Max(0, Math.Min(buffer.Rows - 1, endVisibleRow));

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
					var line = buffer.GetScrollbackLine(idx);
					if (line.Length <= 0) continue;
					float y = (float)(r * _cellHeight + baselineOffset);
					var text = line.Text ?? string.Empty;
					canvas.DrawText(SKTextBlob.Create(text, font), 0, y, SkPaint);
				}
			}
		}

		// IME preedit overlay: draws the active composition at the cursor cell,
		// replacing the underlying cell text, with an underline marking the
		// composed region.
		if (!string.IsNullOrEmpty(_preeditText) && SkPaint != null && SkFont != null && buffer != null)
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
		// is deferred until the cursor overlay passes pixel tests)
		if (!_selectionRange.IsEmpty)
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

		// Debug overlay
		if (ShowDebugOverlay && SkPaint != null)
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

	public void OnBufferUpdated(TerminalBuffer buffer)
	{
		if (buffer == null) return;
		_contentDirty = true;
		HandleBufferGeometryChange(buffer);
		if (_glyphDiscovery == null) return;
		_glyphDiscovery.EnsureSize(buffer.Rows);

		var gens = buffer.RowGenerations;
		if (!gens.IsEmpty)
		{
			if (_lastRowGenerations == null || _lastRowGenerations.Length != gens.Length)
			{
				_lastRowGenerations = gens.ToArray();
				for (int r = 0; r < gens.Length; r++)
					_glyphDiscovery.EnqueueRow(r);
			}
			else
			{
				for (int r = 0; r < gens.Length; r++)
				{
					if (gens[r] != _lastRowGenerations[r])
					{
						_lastRowGenerations[r] = gens[r];
						_glyphDiscovery.EnqueueRow(r);
					}
				}
			}
		}
		else
		{
			_lastRowGenerations = null;
			for (int r = 0; r < buffer.Rows; r++)
				_glyphDiscovery.EnqueueRow(r);
		}

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
		ProcessGlyphDiscoverySlice();
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

	private void ProcessGlyphDiscoverySlice()
	{
		if (_glyphDiscovery == null) return;
		try
		{
			var disable = !string.IsNullOrEmpty(Dotty.Env.GetEnvironmentVariable("DOTTY_DISABLE_GLYPH_DISCOVERY"));
			if (disable) return;
			var buf = Buffer;
			if (buf != null)
			{
				try { _glyphDiscovery.Process(buf, 5); } catch { }
			}
		}
		catch { }
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

		// Recreate glyph atlas when metrics change (font family/size)
		// Use shared atlas service to reduce memory across tabs
		_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
		
		// Get or create a shared atlas for this font configuration
		// Multiple tabs with same font will share the same atlas
		var newAtlas = GlyphAtlasService.GetOrCreateAtlas(SkFont!.Typeface, SkFont.Size, _glyphRasterizationOptions);
		
		// Only update our reference if it's a different atlas
		if (_glyphAtlas != newAtlas)
		{
			if (_glyphAtlas != null)
			{
				GlyphAtlasService.ReleaseAtlas(_glyphAtlas);
			}
			_glyphAtlas = newAtlas;
			GlyphAtlasService.AcquireAtlas(newAtlas);
		}
		
		_contentDirty = true;
		
		if (Buffer != null)
		{
			_glyphDiscovery = new GlyphDiscovery(Buffer.Rows, _glyphAtlas);
		}

		_metricsDirty = false;

		// Font/typeface changes invalidate the composer's per-row classification
		// cache (TypefaceIndex resolution depends on the current font list).
		try { _frameComposer?.ResetCaches(); } catch { }

		// Optionally disable glyph discovery (atlas population) to avoid heavy
		// UI-thread work on resource-constrained systems. Set env var
		// DOTTY_DISABLE_GLYPH_DISCOVERY=1 to disable.
		var disableDiscovery = !string.IsNullOrEmpty(Dotty.Env.GetEnvironmentVariable("DOTTY_DISABLE_GLYPH_DISCOVERY"));
		if (disableDiscovery)
		{
			_glyphDiscovery = null;
		}
		else
		{
			_glyphDiscovery = new GlyphDiscovery(Buffer?.Rows ?? 24, _glyphAtlas);
		}
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
				// Ensure glyph atlas exists for current metrics using shared service
				if (_glyphAtlas == null)
				{
					_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
					var newAtlas = GlyphAtlasService.GetOrCreateAtlas(SkFont?.Typeface ?? SKTypeface.Default, SkFont?.Size ?? 12f, _glyphRasterizationOptions);
					_glyphAtlas = newAtlas;
					GlyphAtlasService.AcquireAtlas(newAtlas);
				}
				// Ensure discovery and composer are created only once so we preserve
				// front-buffer and row caches across buffer swaps. If sizes differ,
				// ensure the discovery knows about the row count.
				if (_glyphDiscovery == null)
				{
					_glyphDiscovery = new GlyphDiscovery(buf.Rows, _glyphAtlas);
				}
				else
				{
					_glyphDiscovery.EnsureSize(buf.Rows);
				}
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
				_frameComposer.GlyphAtlas = _glyphAtlas;
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
				_glyphDiscovery = null;
				// _glyphAtlas?.Dispose(); removed for safety
				_glyphAtlas = null;
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
		
		_glyphDiscovery = null;
		
        // Release per-view render state now that this canvas is leaving the tree.
        try { _frameComposer?.Dispose(); } catch { }
        _frameComposer = null;
        _textShaper?.Dispose();
        _textShaper = null;
		
		// Release the shared atlas reference (the service owns eviction)
		if (_glyphAtlas != null)
		{
			GlyphAtlasService.ReleaseAtlas(_glyphAtlas);
			_glyphAtlas = null;
		}
		
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

	private static GlyphRasterizationOptions CreateRasterizationOptions(SKPaint? paint)
	{
		return new GlyphRasterizationOptions
		{
			IsAntialias = false,
			IsLinearText = false,
			SubpixelText = false,
			IsAutohinted = false,
			LcdRenderText = false,
		};
	}
}
