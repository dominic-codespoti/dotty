using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Dotty.App.Controls.Canvas;
using Avalonia.Threading;
using Dotty.Terminal.Adapter;
using Dotty.App.Rendering;
using Dotty.App.Discovery;
using Dotty.App.Services;
using SkiaSharp;

namespace Dotty.App.Controls;

public enum TerminalCursorShape
{
	Block,
	Beam,
	Underline
}

public class TerminalCanvas : Control
{
	public static readonly StyledProperty<TerminalBuffer?> BufferProperty =
		AvaloniaProperty.Register<TerminalCanvas, TerminalBuffer?>(nameof(Buffer));

	public static readonly StyledProperty<FontFamily> FontFamilyProperty =
		AvaloniaProperty.Register<TerminalCanvas, FontFamily>(nameof(FontFamily), new FontFamily("monospace"));

	public static readonly StyledProperty<double> FontSizeProperty =
		AvaloniaProperty.Register<TerminalCanvas, double>(nameof(FontSize), 14d);

	public static readonly StyledProperty<double> CellPaddingProperty =
		AvaloniaProperty.Register<TerminalCanvas, double>(nameof(CellPadding), 1.5d);

	public TerminalBuffer? Buffer
	{
		get => GetValue(BufferProperty);
		set => SetValue(BufferProperty, value);
	}

	public static readonly StyledProperty<Thickness> ContentPaddingProperty =
		AvaloniaProperty.Register<TerminalCanvas, Thickness>(nameof(ContentPadding), new Thickness(0.0));

	public Thickness ContentPadding
	{
		get => GetValue(ContentPaddingProperty);
		set => SetValue(ContentPaddingProperty, value);
	}

	public static readonly StyledProperty<TerminalCursorShape> CursorShapeProperty =
		AvaloniaProperty.Register<TerminalCanvas, TerminalCursorShape>(nameof(CursorShape), TerminalCursorShape.Block);

	public TerminalCursorShape CursorShape
	{
		get => GetValue(CursorShapeProperty);
		set => SetValue(CursorShapeProperty, value);
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
		AffectsRender<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty, CellPaddingProperty, ContentPaddingProperty);
		AffectsMeasure<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty, CellPaddingProperty, ContentPaddingProperty);
	}

	private float _cellWidth = 8;
	private float _cellHeight = 16;
	private bool _metricsDirty = true;
	private GlyphAtlas? _glyphAtlas;
	private GlyphDiscovery? _glyphDiscovery;
	private TerminalFrameComposer? _frameComposer;
	private DispatcherTimer? _frameDebounceTimer;
	private bool _framePending;
	private const double FrameDebounceMs = 8;
	private bool _lastBufferWasAlternate = false;
	private double _renderScaling = 1.0;
	private GlyphRasterizationOptions _glyphRasterizationOptions = new();
    
	public SKPaint? SkPaint { get; private set; }
	public double CellWidth => _cellWidth;
	public double CellHeight => _cellHeight;
	public bool ShowCursor { get; set; } = true;

	protected override Size MeasureOverride(Size availableSize)
	{
		EnsureMetrics();
		var buf = Buffer;
		if (buf == null) return base.MeasureOverride(availableSize);
		var padding = ContentPadding;
		return new Size(
			buf.Columns * _cellWidth + padding.Left + padding.Right,
			buf.Rows * _cellHeight + padding.Top + padding.Bottom);
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);

		// Fill background using Avalonia so it's consistent with theming
		var bg = ResolveResourceBrush(Application.Current?.Resources, "TerminalBackground", Brushes.Black);
		context.FillRectangle(bg, new Rect(Bounds.Size));

		var padding = ContentPadding;

		var buffer = Buffer;
		if (buffer == null) return;

		EnsureMetrics();
		// If the buffer toggled between main/alternate screens, reset the
		// composer's caches so stale per-row bitmaps are not reused.
		if (_frameComposer != null && buffer.IsAlternateScreenActive != _lastBufferWasAlternate)
		{
			_frameComposer.ResetCaches();
			_lastBufferWasAlternate = buffer.IsAlternateScreenActive;
		}

		// Enqueue a single Skia custom draw operation. The Skia operation will
		// compose the entire frame offscreen and blit it in one GPU operation.
		context.Custom(new SkiaDrawing(this, buffer, _cellWidth, _cellHeight, _frameComposer, padding));
	}

	/// <summary>
	/// Called when the buffer is updated (mutation time). This allows discovery
	/// to run at mutation time instead of during Render.
	/// </summary>
	public void OnBufferUpdated(TerminalBuffer buffer)
	{
		if (buffer == null) return;
		if (_glyphDiscovery == null) return;
		_glyphDiscovery.EnsureSize(buffer.Rows);
		// Dirty tracking removed: enqueue all rows for discovery.
		for (int r = 0; r < buffer.Rows; r++) _glyphDiscovery.EnqueueRow(r);
		// discovery work is enqueued; it will be processed a bit at a time when a frame is requested
	}

	/// <summary>
	/// Request a single coalesced frame. Multiple calls before the dispatcher
	/// runs will only cause a single InvalidateVisual.
	/// </summary>
	public void RequestFrame()
	{
		_framePending = true;
		EnsureFrameTimer();
		if (_frameDebounceTimer != null && !_frameDebounceTimer.IsEnabled)
		{
			_frameDebounceTimer.Start();
		}
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		EnsureFrameTimer();
	}

	private void EnsureFrameTimer()
	{
		if (_frameDebounceTimer != null) return;
		_frameDebounceTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(FrameDebounceMs)
		};
		_frameDebounceTimer.Tick += FrameDebounceTick;
	}

	private void FrameDebounceTick(object? sender, EventArgs e)
	{
		if (_frameDebounceTimer == null) return;
		_frameDebounceTimer.Stop();
		if (!_framePending) return;
		_framePending = false;
		ProcessGlyphDiscoverySlice();
		try
		{
			InvalidateVisual();
		}
		catch { }
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

		SkPaint?.Dispose();
		var fontSize = double.IsNaN(FontSize) || FontSize <= 0 ? 13.0 : FontSize;
		var scale = Math.Max(0.1, _renderScaling);
		var scaledFontSize = Math.Max(1f, (float)(fontSize * scale));
		var familyName = FontFamily?.Name ?? "monospace";
		var typeface = SKTypeface.FromFamilyName(familyName);
		SkPaint = new SKPaint
		{
			Typeface = typeface,
			TextSize = scaledFontSize,
			IsAntialias = true,
			IsLinearText = true,
			SubpixelText = true,
			IsAutohinted = true,
			LcdRenderText = true,
			Color = SKColors.White,
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
		_cellWidth = Math.Max(4, glyphAdvance / (float)scale + (float)(padding * 2.0));
		_cellHeight = Math.Max((float)fontSize, glyphHeight / (float)scale + (float)(padding * 2.0));

		// Recreate glyph atlas when metrics change (font family/size)
		_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
		_glyphAtlas?.Dispose();
		_glyphAtlas = new GlyphAtlas(SkPaint.Typeface, SkPaint.TextSize, _glyphRasterizationOptions);
		_glyphAtlas.PreloadCommonGlyphs();
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
				// Ensure glyph atlas exists for current metrics. Replace only if missing
				if (_glyphAtlas == null)
				{
					_glyphRasterizationOptions = CreateRasterizationOptions(SkPaint);
					_glyphAtlas = new GlyphAtlas(SkPaint?.Typeface ?? SKTypeface.Default, SkPaint?.TextSize ?? 12f, _glyphRasterizationOptions);
				}
				else
				{
					// If metrics changed EnsureMetrics will have recreated SkPaint and caller
					// might have updated the atlas there; keep existing atlas otherwise.
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
				_lastBufferWasAlternate = buf.IsAlternateScreenActive;
			}
			else
			{
				_glyphDiscovery = null;
				_glyphAtlas?.Dispose();
				_glyphAtlas = null;
				_frameComposer?.Dispose();
				_frameComposer = null;
			}
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		if (_frameDebounceTimer != null)
		{
			_frameDebounceTimer.Stop();
			_frameDebounceTimer.Tick -= FrameDebounceTick;
			_frameDebounceTimer = null;
		}
		SkPaint?.Dispose();
		SkPaint = null;
	}

	private IBrush ResolveResourceBrush(IResourceDictionary? resources, string key, IBrush fallback)
	{
		if (resources != null && resources.TryGetResource(key, ThemeVariant.Default, out var value) && value is IBrush brush)
		{
			return brush;
		}

		return fallback;
	}


	private double GetRenderScaling()
	{
		return VisualRoot?.RenderScaling ?? 1.0;
	}

	private static GlyphRasterizationOptions CreateRasterizationOptions(SKPaint? paint)
	{
		return new GlyphRasterizationOptions
		{
			IsAntialias = paint?.IsAntialias ?? true,
			IsLinearText = paint?.IsLinearText ?? true,
			SubpixelText = paint?.SubpixelText ?? true,
			IsAutohinted = paint?.IsAutohinted ?? true,
			LcdRenderText = paint?.LcdRenderText ?? true,
		};
	}
}