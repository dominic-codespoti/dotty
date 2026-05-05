using System;
using System.Collections.Generic;
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
			_bitmapDirty = true;
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
			_bitmapDirty = true;
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
	private bool _lastBufferWasAlternate = false;
	private int _lastKnownBufferRows = -1;
	private int _lastKnownBufferColumns = -1;
	private int _lastKnownScrollbackCount = -1;
	private ulong[]? _lastRowGenerations;
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
	
	private WriteableBitmap? _bitmap;
	private bool _bitmapDirty = true;
	
	public SKPaint? SkPaint { get; private set; }
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
				_bitmapDirty = true;
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
			_bitmapDirty = true;
			InvalidateVisual();
            }
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

		// Render to WriteableBitmap if content changed
		if (_bitmapDirty)
		{
			EnsureMetrics();
			RenderToBitmap(buffer);
			_bitmapDirty = false;
		}

		// Draw cached bitmap to screen
		if (_bitmap != null)
		{
			context.DrawImage(_bitmap,
				new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height),
				new Rect(Bounds.Size));
		}
	}

	private void RenderToBitmap(TerminalBuffer buffer)
	{
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

		canvas.Clear(bgColor);

		var m = SKMatrix.Identity;
		canvas.SetMatrix(m);

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

			if (composerStart <= composerEnd && SkPaint != null)
				_frameComposer.RenderTo(canvas, buffer, SkPaint, (float)_cellWidth, (float)_cellHeight, composerStart, composerEnd);

			int sbStart = Math.Max(-sbCount, startVisibleRow);
			int sbEnd = Math.Min(-1, endVisibleRow);

			if (sbStart <= sbEnd && SkPaint != null)
			{
				var paint = SkPaint;
				var fm = paint.FontMetrics;
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
					canvas.DrawText(text, 0, y, paint);
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

		canvas.Flush();
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

		_bitmapDirty = true;
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
		_bitmapDirty = true;
		InvalidateVisual();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		RuntimeSettings.Changed += OnRuntimeSettingsChanged;
		OnRuntimeSettingsChanged(null, EventArgs.Empty); // apply current runtime settings
		_bitmapDirty = true;
		InvalidateVisual();
	}

	private void OnRuntimeSettingsChanged(object? sender, EventArgs e)
	{
		if (!IsVisible) return;
		var rs = RuntimeSettings.Current;

		if (rs.FontFamily != null)
			FontFamily = new FontFamily(rs.FontFamily);
		if (rs.FontSize.HasValue)
			FontSize = rs.FontSize.Value;
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

		_metricsDirty = true;
		_bitmapDirty = true;
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
			Typeface = typeface,
			TextSize = scaledFontSize,
			IsAntialias = true,
			IsLinearText = true,
			SubpixelText = true,
			IsAutohinted = true,
			LcdRenderText = true,
			Color = defaultFg,
		};

		var fm = SkPaint.FontMetrics;
		float glyphHeight = Math.Max(scaledFontSize, Math.Abs(fm.Descent) + Math.Abs(fm.Ascent));
		float glyphAdvance;
		using (var font = new SKFont(SkPaint.Typeface, SkPaint.TextSize))
		{
			var fontMetrics = font.Metrics;
			glyphAdvance = Math.Max(0.5f, fontMetrics.AverageCharacterWidth);
			var measuredW = Math.Max(1f, SkPaint.MeasureText("W"));
			glyphAdvance = Math.Max(glyphAdvance, measuredW);
		}

		var padding = Math.Max(0.0, CellPadding);
		_cellWidth = (float)Math.Round(Math.Max(4, glyphAdvance / (float)scale + (float)(padding * 2.0)));
		_cellHeight = (float)Math.Round(Math.Max((float)fontSize, glyphHeight / (float)scale + (float)(padding * 2.0)));

		// Recreate glyph atlas when metrics change (font family/size)
		// Use shared atlas service to reduce memory across tabs
		_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
		
		// Get or create a shared atlas for this font configuration
		// Multiple tabs with same font will share the same atlas
		var newAtlas = GlyphAtlasService.GetOrCreateAtlas(SkPaint.Typeface, SkPaint.TextSize, _glyphRasterizationOptions);
		
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
			_bitmapDirty = true;
			InvalidateVisual();
		}

		if (change.Property == BufferProperty)
		{
			var buf = Buffer;
			if (buf != null)
			{
				HandleBufferGeometryChange(buf);
				EnsureMetrics();
				// Ensure glyph atlas exists for current metrics using shared service
				if (_glyphAtlas == null)
				{
					_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
					_glyphAtlas = GlyphAtlasService.GetOrCreateAtlas(SkPaint?.Typeface ?? SKTypeface.Default, SkPaint?.TextSize ?? 12f, _glyphRasterizationOptions);
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
		
		// Clear glyph atlas reference (atlas itself is shared via service)
		_glyphAtlas = null;
		
		// Release Skia paint resources
		if (SkPaint != null)
		{
			try { SkPaint.Dispose(); } catch { }
			SkPaint = null;
		}
		
		// Dispose bitmap
		_bitmap?.Dispose();
		_bitmap = null;
		
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

	private SKTypeface ResolveTerminalTypeface()
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var candidate in EnumerateFontCandidates())
		{
			if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
			{
				continue;
			}

			if (!TryResolveTypeface(candidate, out var typeface))
			{
				continue;
			}

			if (FontHelpers.IsLikelySymbolFontName(candidate) || FontHelpers.IsLikelySymbolFontName(typeface.FamilyName))
			{
				typeface.Dispose();
				continue;
			}

			return typeface;
		}

		return SKTypeface.Default;
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
		return VisualRoot?.RenderScaling ?? 1.0;
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
