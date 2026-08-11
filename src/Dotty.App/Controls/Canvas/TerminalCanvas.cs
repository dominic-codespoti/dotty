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

	// Incremental render tracking: avoids full redraw when only the scroll
	// offset changed (user scrolling).  Instead, the existing bitmap content
	// is shifted with memmove and only newly-exposed rows are re-rendered.
	private double _lastRenderOffsetY = double.NaN;
	private int _lastRenderSbCount = -1;

	// Global font resolution cache shared across all TerminalCanvas instances.
	// Key is "{FontFamily}|{TextSize:F1}".  Invalidated when font settings change.
	private static readonly ConcurrentDictionary<string, SKTypeface> CachedPrimaryTypeface = new();
	private static readonly ConcurrentDictionary<string, List<SKTypeface>> CachedFallbackTypefaces = new();
	private static string? s_lastFontCacheKey;

	private bool _lastBufferWasAlternate = false;
	private int _lastKnownBufferRows = -1;
	private int _lastKnownBufferColumns = -1;
	private int _lastKnownScrollbackCount = -1;
	private ulong[]? _lastRowGenerations;

	// Mirror of buffer.RowScrollEpochs as last rendered. Scroll replays rotate
	// this identically to the buffer so moved rows keep bufferEpoch == mirror
	// (pixels were memmoved) while exposed/written rows differ and are
	// re-rendered by RenderDirty.
	private ulong[]? _renderEpochs;
	private readonly List<int> _dirtyRowScratch = new();
	private SKPaint? _exposedBgPaint;
	private double _renderScaling = 1.0;
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

    private void UpdateScrollState(int? explicitScrollbackCount = null)
    {
        Size extent;
        var buf = Buffer;
        if (buf == null) extent = _viewport;
        else 
        {
            int sb = explicitScrollbackCount ?? buf.ScrollbackCount;
            double height = (buf.Rows + sb) * _cellHeight + ContentPadding.Top + ContentPadding.Bottom;
            double width = buf.Columns * _cellWidth + ContentPadding.Left + ContentPadding.Right;
            extent = new Size(width, height);
        }

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
		base.Render(context);

		var bg = ResolveResourceBrush(Application.Current?.Resources, "TerminalBackground", Brushes.Black);
		context.FillRectangle(bg, new Rect(Bounds.Size));

		if (!IsVisible) return;

		var buffer = Buffer;
		if (buffer == null) return;

		// Always render fresh from buffer — no bitmap caching.
		// This eliminates stale-frame artifacts from inter-chunk render timing.
		EnsureMetrics();
		RenderToBitmap(buffer);

		// Draw cached bitmap to screen
		if (_bitmap != null)
		{
			context.DrawImage(_bitmap,
				new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height),
				new Rect(Bounds.Size));
		}
	}

	/// <summary>
	/// Computes the buffer rows newly exposed by a pure-scroll frame, i.e. the rows
	/// that the memmove cleared and that therefore must be re-rendered. The cleared
	/// band is the edge the content moved away from: the top when scrolling back
	/// (content shifts down), the bottom when scrolling forward (content shifts up).
	/// Rows are in buffer coordinates (negative = scrollback), not yet clamped to the
	/// visible range.
	/// </summary>
	internal static void ComputeExposedRows(
		double oldTop,
		double newTop,
		double viewportHeight,
		double cellHeight,
		int scrollbackCount,
		out int exposeStartRow,
		out int exposeEndRow)
	{
		double deltaY = oldTop - newTop;
		if (deltaY > 0)
		{
			// Content moved down: cleared band is the top |deltaY| px of the viewport.
			exposeStartRow = (int)Math.Floor(newTop / cellHeight) - scrollbackCount;
			exposeEndRow = (int)Math.Ceiling(oldTop / cellHeight) - 1 - scrollbackCount;
		}
		else
		{
			// Content moved up: cleared band is the bottom |deltaY| px of the viewport.
			double clearTop = newTop + viewportHeight - Math.Abs(deltaY);
			exposeStartRow = (int)Math.Floor(clearTop / cellHeight) - scrollbackCount;
			exposeEndRow = (int)Math.Ceiling((newTop + viewportHeight) / cellHeight) - 1 - scrollbackCount;
		}
	}

	/// <summary>
	/// Shifts the entire cached bitmap by pixelDelta (positive = content moved
	/// down), clearing the newly-exposed band. Mirrors the viewport pureScroll.
	/// </summary>
	internal static unsafe void MemmoveWholeFrame(byte* pixels, int stride, int height, int pixelDelta)
	{
		if (pixelDelta > 0)
		{
			// Content moved down: copy pixels down, clear top band.
			System.Buffer.MemoryCopy(pixels, pixels + pixelDelta * stride, (ulong)((height - pixelDelta) * stride), (ulong)((height - pixelDelta) * stride));
			new Span<byte>(pixels, pixelDelta * stride).Clear();
		}
		else
		{
			// Content moved up: copy pixels up, clear bottom band.
			int shiftUp = -pixelDelta;
			System.Buffer.MemoryCopy(pixels + shiftUp * stride, pixels, (ulong)((height - shiftUp) * stride), (ulong)((height - shiftUp) * stride));
			new Span<byte>(pixels + (height - shiftUp) * stride, shiftUp * stride).Clear();
		}
	}

	/// <summary>
	/// Shifts the pixel band of logical rows [topRow..bottomRow] by
	/// delta * cellHeight (positive = content moved down) in the cached bitmap.
	/// The exposed band (the edge the content moved away from) is left with its
	/// stale pixels — the caller sentinels those rows and RenderDirty repaints
	/// them (base background + regions + glyphs). Callers must ensure the region
	/// is fully visible; otherwise rows entering from outside the bitmap cannot
	/// be memmoved and must be re-rendered instead.
	/// </summary>
	internal static unsafe void MemmoveRegionRows(byte* pixels, int stride, int height, float translate, int topRow, int bottomRow, int delta, float cellH)
	{
		int y0 = (int)Math.Round(topRow * cellH + translate);
		int y1 = (int)Math.Round((bottomRow + 1) * cellH + translate);
		int rowPx = (int)Math.Round(delta * cellH);
		if (rowPx == 0) return;
		y0 = Math.Max(0, Math.Min(height, y0));
		y1 = Math.Max(0, Math.Min(height, y1));
		int band = y1 - y0;
		if (band <= 0) return;

		if (rowPx > 0)
		{
			// Content down: copy [y0, y1-rowPx) -> [y0+rowPx, y1).
			int copyLen = band - rowPx;
			if (copyLen > 0)
				System.Buffer.MemoryCopy(pixels + y0 * stride, pixels + (y0 + rowPx) * stride, (ulong)(copyLen * stride), (ulong)(copyLen * stride));
		}
		else
		{
			// Content up: copy [y0-rowPx, y1) -> [y0, y1+rowPx).
			int copyLen = band + rowPx;
			if (copyLen > 0)
				System.Buffer.MemoryCopy(pixels + (y0 - rowPx) * stride, pixels + y0 * stride, (ulong)(copyLen * stride), (ulong)(copyLen * stride));
		}
	}

	/// <summary>
	/// Applies one recorded buffer scroll to the renderer's epoch mirror.
	/// Fully-visible regions rotate the mirror identically to the buffer and
	/// sentinel the exposed band (the caller memmoves the pixels). Partially or
	/// non-visible regions cannot be memmoved (content entered from outside the
	/// bitmap), so every visible row of the region is sentineled instead.
	/// </summary>
	internal static void ApplyScrollToMirror(ulong[] mirror, int top, int bottom, int delta, int visStart, int visEnd, out bool memmoved)
	{
		if (top >= visStart && bottom <= visEnd)
		{
			ScrollEpochMath.RotateRange(mirror, top, bottom, delta);
			ScrollEpochMath.MarkExposedRows(mirror, top, bottom, delta);
			memmoved = true;
		}
		else
		{
			for (int r = Math.Max(top, visStart); r <= Math.Min(bottom, visEnd); r++)
				mirror[r] = ulong.MaxValue;
			memmoved = false;
		}
	}

	/// <summary>
	/// Rows whose pixels were not moved into place: exposed bands (sentineled
	/// in the mirror) and rows written since the last render (epoch mismatch).
	/// </summary>
	internal static void ComputeDirtyRows(ReadOnlySpan<ulong> bufferEpochs, ReadOnlySpan<ulong> mirror, int visStart, int visEnd, List<int> dirty)
	{
		dirty.Clear();
		int end = Math.Min(visEnd, Math.Min(bufferEpochs.Length, mirror.Length) - 1);
		for (int r = visStart; r <= end; r++)
		{
			if (bufferEpochs[r] != mirror[r])
				dirty.Add(r);
		}
	}

	private void RenderToBitmap(TerminalBuffer buffer)
	{
		bool lockTaken = false;
		try
		{
			System.Threading.Monitor.Enter(buffer.SyncRoot, ref lockTaken);

			if (_frameComposer != null && buffer.IsAlternateScreenActive != _lastBufferWasAlternate)
			{
				_frameComposer.ResetCaches();
				_lastBufferWasAlternate = buffer.IsAlternateScreenActive;
			}

			var bgBrush = ResolveResourceBrush(Application.Current?.Resources, "TerminalBackground", Brushes.Black);
			var bgColor = SKColors.Black;
			if (bgBrush is ISolidColorBrush solid)
				bgColor = new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, solid.Color.A);

			int w = Math.Max(1, (int)Bounds.Width);
			int h = Math.Max(1, (int)Bounds.Height);

			if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
			{
				_bitmap?.Dispose();
				_bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888);
				_lastRenderOffsetY = double.NaN; // force full render on new bitmap
			}

			try
			{
				buffer.MarkRender();
			}
			catch { }

			int sbCount = buffer.ScrollbackCount;
		Dispatcher.UIThread.Post(() => UpdateScrollState(sbCount), DispatcherPriority.Background);

		using var locked = _bitmap.Lock();
		var info = new SKImageInfo(locked.Size.Width, locked.Size.Height);
		using var surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
		var canvas = surface.Canvas;

		float newScrollTranslate = (float)(sbCount * _cellHeight - _offset.Y);

		// ---- Incremental path decision -------------------------------------
		// The cached bitmap can be updated without a full re-render when the
		// frame is a pure viewport shift (pureScroll), a buffer scroll replay,
		// and/or a small set of written rows. Full render whenever the cached
		// state can't be reconciled: bitmap recreated, grid geometry changed,
		// scrollback changed (main-screen output flow), selection active
		// (conservative), or a viewport shift AND buffer scrolls arrived in the
		// same frame (ordering would be ambiguous).
		bool offsetChanged = !double.IsNaN(_lastRenderOffsetY) &&
		                     Math.Abs(_offset.Y - _lastRenderOffsetY) > 0.5;
		bool sbChanged = _lastRenderSbCount != sbCount;
		bool selectionActive = !_selectionRange.IsEmpty;
		bool hasPending = buffer.PendingScrollCount > 0;
		bool mirrorInvalid = _renderEpochs == null || _renderEpochs.Length != buffer.Rows;

		bool doFull = double.IsNaN(_lastRenderOffsetY) ||
		              mirrorInvalid ||
		              sbChanged ||
		              selectionActive ||
		              (offsetChanged && hasPending);

		if (!doFull && _frameComposer != null && SkPaint != null && SkFont != null)
		{
			int startVisibleRow = (int)Math.Floor(_offset.Y / _cellHeight) - sbCount;
			int endVisibleRow = (int)Math.Ceiling((_offset.Y + _viewport.Height) / _cellHeight) - sbCount;
			startVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, startVisibleRow));
			endVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, endVisibleRow));
			int visStart = Math.Max(0, startVisibleRow);
			int visEnd = Math.Min(buffer.Rows - 1, endVisibleRow);
			var mirror = _renderEpochs!;

			// Set up the canvas translate so subsequent rendering goes to the right place.
			var m = SKMatrix.Identity;
			canvas.SetMatrix(m);
			if (ContentPadding.Left != 0 || ContentPadding.Top != 0)
				canvas.Translate((float)ContentPadding.Left, (float)ContentPadding.Top);
			canvas.Translate(0, newScrollTranslate);

			// 1) Replay pending buffer scrolls as region memmoves. Moved rows'
			//    pixels are shifted in place and their mirror epochs travel with
			//    them; the exposed band is sentineled so the dirty pass repaints it.
			//    Replay is cheap only while the total memmove volume stays small:
			//    a burst like `yes` queues thousands of scrolls per chunk (each LF
			//    enqueues one), and replaying all of them is gigabytes of copying.
			//    Cap the total region height and fall back to a full render (with
			//    the queue drained) when exceeded — the pre-incremental behavior.
			bool replayOverflow = false;
			if (hasPending)
			{
				unsafe
				{
					byte* pixels = (byte*)locked.Address;
					int stride = locked.RowBytes;
					long replayCost = 0;
					long maxReplayCost = 8L * Math.Max(1, buffer.Rows);
					while (buffer.TryDequeuePendingScroll(out var s))
					{
						int regionHeight = s.Bottom - s.Top + 1;
						replayCost += regionHeight;
						if (replayCost > maxReplayCost)
						{
							while (buffer.TryDequeuePendingScroll(out _)) { }
							replayOverflow = true;
							break;
						}
						ApplyScrollToMirror(mirror, s.Top, s.Bottom, s.Delta, visStart, visEnd, out bool memmoved);
						if (memmoved)
							MemmoveRegionRows(pixels, stride, h, newScrollTranslate, s.Top, s.Bottom, s.Delta, (float)_cellHeight);
					}
				}
			}

			if (replayOverflow)
				goto FullRenderPath;

			// 2) Pure-scroll viewport shift (no pending buffer scrolls). Shift
			//    the whole frame and sentinel the exposed band so the dirty pass
			//    re-renders it (RenderDirty re-applies the base background, so
			//    the memmove's zero-cleared band is fully overwritten).
			if (offsetChanged && !hasPending)
			{
				double deltaY = _lastRenderOffsetY - _offset.Y;
				int pixelDelta = (int)Math.Round(deltaY);
				if (pixelDelta != 0 && Math.Abs(pixelDelta) < h)
				{
					unsafe
					{
						MemmoveWholeFrame((byte*)locked.Address, locked.RowBytes, h, pixelDelta);
					}

					// Determine which rows are newly exposed: the memmove cleared the
					// band at the edge the content moved away from — the top when
					// scrolling back (content shifts down), the bottom when scrolling
					// forward (content shifts up). Re-render exactly those rows; the
					// rest of the frame is already correct from the pixel shift and
					// must not be redrawn (redrawing over shifted pixels doubles
					// antialiased edges and clobbers the wrong rows).
					ComputeExposedRows(oldTop: _lastRenderOffsetY, newTop: _offset.Y,
						viewportHeight: h, cellHeight: _cellHeight, scrollbackCount: sbCount,
						out int exposeStartRow, out int exposeEndRow);
					exposeStartRow = Math.Max(startVisibleRow, exposeStartRow);
					exposeEndRow = Math.Min(endVisibleRow, exposeEndRow);

					for (int r = Math.Max(exposeStartRow, visStart); r <= Math.Min(exposeEndRow, visEnd); r++)
						mirror[r] = ulong.MaxValue;

					// Exposed scrollback rows (negative): text over the shifted
					// pixels, with the base background re-applied first (the
					// memmove cleared them to zeros).
					int sbStart = Math.Max(-sbCount, exposeStartRow);
					int sbEnd = Math.Min(-1, exposeEndRow);
					if (sbStart <= sbEnd)
					{
						_exposedBgPaint ??= new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
						_exposedBgPaint.Color = bgColor;
						// Full clip width: the content-padding gutters are part
						// of the base background (canvas.Clear in the full path).
						var clip = canvas.LocalClipBounds;
						canvas.DrawRect(SKRect.Create(clip.Left, sbStart * (float)_cellHeight, clip.Width, (sbEnd - sbStart + 1) * (float)_cellHeight), _exposedBgPaint);

						var font = SkFont;
						var fm = font.Metrics;
						float glyphH = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
						float blOffset = (float)(_cellHeight * 0.5f) + (glyphH * 0.5f) - Math.Abs(fm.Descent);
						for (int r = sbStart; r <= sbEnd; r++)
						{
							int idx = r + sbCount;
							idx = Math.Max(0, Math.Min(sbCount - 1, idx));
							var line = buffer.GetScrollbackLine(idx);
							if (line.Length <= 0) continue;
							float y = (float)(r * _cellHeight + blOffset);
							canvas.DrawText(SKTextBlob.Create(line.Text ?? string.Empty, font), 0, y, SkPaint);
						}
					}
				}
			}

			// 3) Dirty rows: everything whose pixels were not moved into place —
			//    exposed bands (sentineled) and rows written since the last render.
			ComputeDirtyRows(buffer.RowScrollEpochs, mirror, visStart, visEnd, _dirtyRowScratch);

			if (_dirtyRowScratch.Count > 0)
				_frameComposer.RenderDirty(canvas, buffer, SkPaint, SkFont, (float)_cellWidth, (float)_cellHeight, bgColor, visStart, visEnd, CollectionsMarshal.AsSpan(_dirtyRowScratch));

			_renderEpochs = buffer.RowScrollEpochs.ToArray();
			goto renderOverlays;
		}

	FullRenderPath:
		// Full render path: clear bitmap and re-render everything.
		canvas.Clear(bgColor);

		var mFull = SKMatrix.Identity;
		canvas.SetMatrix(mFull);

		if (ContentPadding.Left != 0 || ContentPadding.Top != 0)
			canvas.Translate((float)ContentPadding.Left, (float)ContentPadding.Top);

		canvas.Translate(0, newScrollTranslate);

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

		// The full pass re-rendered everything: the mirror now equals the buffer
		// epochs and any queued scrolls are obsolete (their content was painted).
		_renderEpochs = buffer.RowScrollEpochs.ToArray();
		while (buffer.TryDequeuePendingScroll(out _)) { }

	renderOverlays:
		_lastRenderOffsetY = _offset.Y;
		_lastRenderSbCount = sbCount;
		// Draw selection overlay
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
					canvas.DrawRect(new SKRect(x, y, x + rectW, y + cellH), _selectionPaint);
				}
			}
		}

		// Draw cursor
		if (_showCursor && buffer != null)
		{
			int curRow = buffer.CursorRow;
			int curCol = buffer.CursorCol;
			if (curRow >= 0 && curRow < buffer.Rows && curCol >= 0 && curCol < buffer.Columns)
			{
				float cx = curCol * (float)_cellWidth;
				float cy = curRow * (float)_cellHeight;
				float cw = (float)_cellWidth;
				float ch = (float)_cellHeight;

				using var cursorPaint = new SKPaint
				{
					Color = new SKColor(0xFF, 0xFF, 0xFF, 180),
					Style = SKPaintStyle.Fill,
					IsAntialias = false
				};

				switch (CursorShape)
				{
					case TerminalCursorShape.Block:
						canvas.DrawRect(new SKRect(cx, cy, cx + cw, cy + ch), cursorPaint);
						break;
					case TerminalCursorShape.Beam:
						float beamW = Math.Max(1f, cw * 0.08f);
						canvas.DrawRect(new SKRect(cx, cy, cx + beamW, cy + ch), cursorPaint);
						break;
					case TerminalCursorShape.Underline:
						float ulH = Math.Max(1f, ch * 0.08f);
						canvas.DrawRect(new SKRect(cx, cy + ch - ulH, cx + cw, cy + ch), cursorPaint);
						break;
				}
			}
		}

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
			canvas.DrawRect(0, 0, canvas.DeviceClipBounds.Width, 20, debugBgPaint);
			canvas.DrawText(SKTextBlob.Create(debugInfo, debugFont), 4, y + 14, debugTextPaint);
			canvas.Restore();
		}

		canvas.Flush();
		}
		finally
		{
			if (lockTaken)
				System.Threading.Monitor.Exit(buffer.SyncRoot);
		}
	}

	public void OnBufferUpdated(TerminalBuffer buffer)
	{
		if (buffer == null) return;
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
		var scrollChanged = buffer.ScrollbackCount != _lastKnownScrollbackCount;

		_lastKnownBufferRows = buffer.Rows;
		_lastKnownBufferColumns = buffer.Columns;
		_lastKnownScrollbackCount = buffer.ScrollbackCount;

		if (geometryChanged)
		{
			InvalidateMeasure();
			InvalidateArrange();
		}

		if (geometryChanged || scrollChanged)
			UpdateScrollState(buffer.ScrollbackCount);
	}

	public void RequestFrame()
	{
		if (!IsVisible) return;
		ProcessGlyphDiscoverySlice();
		InvalidateVisual();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		RuntimeSettings.Changed += OnRuntimeSettingsChanged;
		OnRuntimeSettingsChanged(null, EventArgs.Empty); // apply current runtime settings
		InvalidateVisual();
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
			_glyphAtlas = newAtlas;
		}
		
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
					_glyphAtlas = GlyphAtlasService.GetOrCreateAtlas(SkFont?.Typeface ?? SKTypeface.Default, SkFont?.Size ?? 12f, _glyphRasterizationOptions);
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
				InvalidateVisual();
				RequestFrame();
			}
			else
			{
				_lastKnownBufferRows = -1;
				_lastKnownBufferColumns = -1;
				_lastKnownScrollbackCount = -1;
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
		RuntimeSettings.Changed -= OnRuntimeSettingsChanged;
		
		_glyphDiscovery = null;
		
        // Release per-view render state now that this canvas is leaving the tree.
        try { _frameComposer?.Dispose(); } catch { }
        _frameComposer = null;
        _textShaper?.Dispose();
        _textShaper = null;
		
		// Clear glyph atlas reference (atlas itself is shared via service)
		_glyphAtlas = null;
		
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
		_exposedBgPaint?.Dispose();
		_exposedBgPaint = null;
		
		// Reset metrics to ensure fresh calculation on reattach
		_metricsDirty = true;
		_cellWidth = 8;
		_cellHeight = 16;
	}

	private IBrush ResolveResourceBrush(IResourceDictionary? resources, string key, IBrush fallback)
	{
		if (resources != null && resources.TryGetResource(key, ThemeVariant.Default, out var value) && value is IBrush brush)
		{
			return brush;
		}

		return fallback;
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
