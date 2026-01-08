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

	public TerminalBuffer? Buffer
	{
		get => GetValue(BufferProperty);
		set => SetValue(BufferProperty, value);
	}

	public static readonly StyledProperty<Thickness> ContentPaddingProperty =
		AvaloniaProperty.Register<TerminalCanvas, Thickness>(nameof(ContentPadding), new Thickness(0));

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

	static TerminalCanvas()
	{
		AffectsRender<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty);
		AffectsMeasure<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty);
	}

	private float _cellWidth = 8;
	private float _cellHeight = 16;
	private bool _metricsDirty = true;
    private GlyphAtlas? _glyphAtlas;
    private GlyphDiscovery? _glyphDiscovery;
	private TerminalFrameComposer? _frameComposer;
	private DispatcherTimer? _renderTimer;
	private volatile bool _needsFrame = false;
	private bool _lastBufferWasAlternate = false;
    
	public SKPaint? SkPaint { get; private set; }
	public double CellWidth => _cellWidth;
	public double CellHeight => _cellHeight;
	public bool ShowCursor { get; set; } = true;

	protected override Size MeasureOverride(Size availableSize)
	{
		EnsureMetrics();
		var buf = Buffer;
		if (buf == null) return base.MeasureOverride(availableSize);
		return new Size(buf.Columns * _cellWidth, buf.Rows * _cellHeight);
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);

		// Fill background using Avalonia so it's consistent with theming
		var bg = ResolveResourceBrush(Application.Current?.Resources, "TerminalBackground", Brushes.Black);
		context.FillRectangle(bg, new Rect(Bounds.Size));

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
		context.Custom(new SkiaDrawing(this, buffer, _cellWidth, _cellHeight, _glyphAtlas, _frameComposer));
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
		var dirty = buffer.DirtyRows;
		if (dirty == null) return;
		for (int r = 0; r < buffer.Rows; r++)
		{
			if (r < dirty.Length && dirty[r])
			{
				_glyphDiscovery.EnqueueRow(r);
			}
		}
		// discovery work is enqueued; it will be processed a bit at a time when a frame is requested
	}

	/// <summary>
	/// Request a single coalesced frame. Multiple calls before the dispatcher
	/// runs will only cause a single InvalidateVisual.
	/// </summary>
	public void RequestFrame()
	{
		// Mark that we need a frame; the render timer will coalesce many requests
		// and ensure we render at a steady, vsync-like cadence.
		_needsFrame = true;
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		// Start a single render timer (approx 60fps) that pulls the latest state.
		if (_renderTimer == null)
		{
			_renderTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16)
			};
			_renderTimer.Tick += (_, __) =>
			{
				if (!_needsFrame) return;
				_needsFrame = false;
				try
				{
					// Budget a slice of discovery work before rendering so glyphs gradually populate.
					try { _glyphDiscovery?.Process(Buffer!, 50); } catch { }
				}
				catch { }
				InvalidateVisual();
			};
			_renderTimer.Start();
		}
	}

	private void EnsureMetrics()
	{
		if (!_metricsDirty && SkPaint != null) return;

		SkPaint?.Dispose();
		var familyName = FontFamily?.Name ?? "monospace";
		var typeface = SKTypeface.FromFamilyName(familyName);
		SkPaint = new SKPaint
		{
			Typeface = typeface,
			TextSize = (float)FontSize,
			IsAntialias = true,
			Color = SKColors.White,
		};

		// Measure a 'W' for approximate cell size
		_cellWidth = Math.Max(4, SkPaint.MeasureText("W"));
		var fm = SkPaint.FontMetrics;
		_cellHeight = Math.Max((float)FontSize, Math.Abs(fm.Descent) + Math.Abs(fm.Ascent));

		// Recreate glyph atlas when metrics change (font family/size)
		_glyphAtlas?.Dispose();
		_glyphAtlas = new GlyphAtlas(SkPaint.Typeface, SkPaint.TextSize);
		if (Buffer != null)
		{
			_glyphDiscovery = new GlyphDiscovery(Buffer.Rows, _glyphAtlas);
		}

		_metricsDirty = false;
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
					_glyphAtlas = new GlyphAtlas(SkPaint?.Typeface ?? SKTypeface.Default, SkPaint?.TextSize ?? 12f);
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
		// Stop the render timer to avoid background ticks after detachment.
		try
		{
			if (_renderTimer != null)
			{
				_renderTimer.Stop();
				_renderTimer.Tick -= (_, __) => { };
				_renderTimer = null;
			}
		}
		catch { }
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
}