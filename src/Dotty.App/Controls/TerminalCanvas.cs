using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.Utilities;
using Dotty.Terminal;

namespace Dotty.App.Controls;

/// <summary>
/// Immediate-mode terminal renderer that draws the buffer onto a single drawing surface.
/// This avoids building thousands of TextBlocks and keeps each cell aligned to a consistent grid.
/// </summary>
public class TerminalCanvas : Control
{
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalCanvas, FontFamily>(nameof(FontFamily), new FontFamily("JetBrainsMono Nerd Font, JetBrains Mono, Cascadia Code, DejaVu Sans Mono, Monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalCanvas, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<TerminalBuffer?> BufferProperty =
        AvaloniaProperty.Register<TerminalCanvas, TerminalBuffer?>(nameof(Buffer));

    public static readonly StyledProperty<bool> ShowCursorProperty =
        AvaloniaProperty.Register<TerminalCanvas, bool>(nameof(ShowCursor), true);

    public TerminalBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
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

    public bool ShowCursor
    {
        get => GetValue(ShowCursorProperty);
        set => SetValue(ShowCursorProperty, value);
    }

    private readonly FontFamily _fallbackFont = new("JetBrainsMono Nerd Font, JetBrains Mono, Cascadia Code, DejaVu Sans Mono, Monospace");
    private readonly Dictionary<string, IBrush> _colorBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RunStyleKey, TextRunProperties> _textRunPropertiesCache = new();
    private readonly StringBuilder _rowTextBuilder = new();
    private readonly StringBuilder _signatureBuilder = new();
    private readonly List<ValueSpan<TextRunProperties>> _styleSpanScratch = new();
    private RowLayoutCache[] _rowCaches = Array.Empty<RowLayoutCache>();
    private int[] _columnToTextIndexScratch = Array.Empty<int>();
    private Typeface _normalTypeface;
    private Typeface _boldTypeface;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private bool _metricsDirty = true;
    private double _renderScaling = 1.0;

    static TerminalCanvas()
    {
        AffectsRender<TerminalCanvas>(BufferProperty, ShowCursorProperty, FontFamilyProperty, FontSizeProperty);
        AffectsMeasure<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty);
    }

    public TerminalCanvas()
    {
        UseLayoutRounding = true;
        
        _normalTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Normal, FontWeight.Normal);
        _boldTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Normal, FontWeight.Bold);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearRowCaches();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _metricsDirty = true;
            ClearRowCaches();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        var buffer = Buffer;
        if (buffer == null)
        {
            return new Size(0, 0);
        }

        return new Size(buffer.Columns * _cellWidth, buffer.Rows * _cellHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureMetrics();
        var buffer = Buffer;
        if (buffer == null)
        {
            return;
        }

        _renderScaling = (VisualRoot as IRenderRoot)?.RenderScaling ?? 1.0;

        var resources = Application.Current?.Resources;
        var defaultBg = ResolveResourceBrush(resources, "TerminalBackground", Brushes.Black);
        var defaultFg = ResolveResourceBrush(resources, "TerminalForeground", Brushes.White);

        bool drawCursor = ShowCursor && buffer.CursorVisible;
        int cursorRow = buffer.CursorRow;
        int cursorCol = buffer.CursorCol;

        EnsureRowCacheCapacity(buffer.Rows);

        for (int row = 0; row < buffer.Rows; row++)
        {
            var y = row * _cellHeight;
            bool rowHasCursor = drawCursor && row == cursorRow;
            var rowCache = EnsureRowLayout(buffer, row, defaultFg);

            // 1. Draw Backgrounds (Per cell)
            for (int col = 0; col < buffer.Columns; col++)
            {
                var cell = buffer.GetCell(row, col);
                if (cell.IsContinuation)
                {
                    continue;
                }

                var bgBrush = ResolveColorBrush(cell.Background, defaultBg) ?? defaultBg;
                int widthUnits = Math.Max(1, cell.Width == 0 ? 1 : (int)cell.Width);
                var rect = CreateSnappedRect(col * _cellWidth, y, widthUnits * _cellWidth, _cellHeight);
                context.FillRectangle(bgBrush, rect);
            }

            if (rowCache.Layout is { } layout)
            {
                var layoutHeight = layout.Height;
                var textY = Math.Round(y + Math.Max(0, (_cellHeight - layoutHeight) / 2));
                layout.Draw(context, new Point(0, textY));

                for (int col = 0; col < buffer.Columns; col++)
                {
                    var cell = buffer.GetCell(row, col);
                    if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme))
                    {
                        continue;
                    }

                    var glyph = cell.Grapheme[0];
                    if (!IsPowerlineGlyph(glyph))
                    {
                        continue;
                    }

                    var fgBrush = ResolveColorBrush(cell.Foreground, defaultFg) ?? defaultFg;
                    DrawPowerlineGlyph(context, glyph, col * _cellWidth, y, fgBrush);
                }
            }

            if (rowHasCursor && drawCursor)
            {
                DrawCursorRect(context, buffer, rowCache, cursorRow, cursorCol, y, defaultFg);
            }
        }
    }

    private void EnsureMetrics()
    {
        if (!_metricsDirty)
        {
            return;
        }

        var family = FontFamily ?? _fallbackFont;
        _normalTypeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        _boldTypeface = new Typeface(family, FontStyle.Normal, FontWeight.Bold);

        // Measure a string of Ws to get a better average width and avoid rounding errors
        // that occur when measuring a single character.
        var testString = "WWWWWWWWWW";
        var layout = CreateTextLayout(testString, false, Brushes.Transparent);

        // Use the same TextLayout path that draws the glyphs so cursor/background math match rendered runs.
        var layoutWidth = layout.WidthIncludingTrailingWhitespace > 0
            ? layout.WidthIncludingTrailingWhitespace
            : layout.Width;
        var avgCharWidth = layoutWidth / Math.Max(1, testString.Length);

        _cellWidth = Math.Max(4, avgCharWidth);
        _cellHeight = Math.Max(FontSize, layout.Height);
        
        UpdatePowerlineGeometries();
        
        _metricsDirty = false;
    }

    private RowLayoutCache EnsureRowLayout(TerminalBuffer buffer, int row, IBrush defaultFg)
    {
        var cache = _rowCaches[row];
        EnsureColumnIndexScratch(buffer.Columns + 1);

        _rowTextBuilder.Clear();
        _signatureBuilder.Clear();
        _styleSpanScratch.Clear();

        int textIndex = 0;
        TextRunProperties? currentProps = null;
        int currentSpanStart = 0;

        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            _columnToTextIndexScratch[col] = textIndex;
            AppendSignature(cell);

            if (cell.IsContinuation)
            {
                continue;
            }

            var glyph = cell.Grapheme;
            if (string.IsNullOrEmpty(glyph))
            {
                glyph = " ";
            }

            var fgBrush = ResolveColorBrush(cell.Foreground, defaultFg) ?? defaultFg;
            var runProps = GetTextRunProperties(cell.Bold, fgBrush);

            if (!ReferenceEquals(currentProps, runProps))
            {
                if (currentProps is { } prev && textIndex > currentSpanStart)
                {
                    _styleSpanScratch.Add(new ValueSpan<TextRunProperties>(currentSpanStart, textIndex - currentSpanStart, prev));
                }

                currentProps = runProps;
                currentSpanStart = textIndex;
            }

            _rowTextBuilder.Append(glyph);
            textIndex += glyph.Length;
        }

        if (currentProps is { } last && textIndex > currentSpanStart)
        {
            _styleSpanScratch.Add(new ValueSpan<TextRunProperties>(currentSpanStart, textIndex - currentSpanStart, last));
        }

        _columnToTextIndexScratch[buffer.Columns] = textIndex;

        var signature = _signatureBuilder.ToString();
        _signatureBuilder.Clear();

        bool canReuse = cache.Signature == signature && cache.Layout != null && cache.ColumnToTextIndex.Length == buffer.Columns + 1;
        if (canReuse)
        {
            _rowTextBuilder.Clear();
            _styleSpanScratch.Clear();
            return cache;
        }

        var rowText = _rowTextBuilder.Length == 0 ? " " : _rowTextBuilder.ToString();
        if (_styleSpanScratch.Count == 0)
        {
            var defaultProps = GetTextRunProperties(false, defaultFg);
            _styleSpanScratch.Add(new ValueSpan<TextRunProperties>(0, rowText.Length, defaultProps));
        }

        var styleOverrides = _styleSpanScratch.ToArray();
        _styleSpanScratch.Clear();
        _rowTextBuilder.Clear();

        cache.Layout?.Dispose();
        cache.Layout = new TextLayout(
            rowText,
            _normalTypeface,
            FontSize,
            defaultFg,
            TextAlignment.Left,
            TextWrapping.NoWrap,
            textStyleOverrides: styleOverrides);

        cache.Signature = signature;
        cache.TextLength = rowText.Length;
        cache.EnsureColumnCapacity(buffer.Columns + 1);
        Array.Copy(_columnToTextIndexScratch, cache.ColumnToTextIndex, buffer.Columns + 1);

        return cache;
    }

    private void DrawCursorRect(DrawingContext context, TerminalBuffer buffer, RowLayoutCache rowCache, int cursorRow, int cursorCol, double y, IBrush defaultFg)
    {
        var layout = rowCache.Layout;
        if (layout is null || rowCache.ColumnToTextIndex.Length == 0)
        {
            return;
        }

        var indices = rowCache.ColumnToTextIndex;
        int start = cursorCol < indices.Length ? indices[cursorCol] : rowCache.TextLength;
        int end = cursorCol + 1 < indices.Length ? indices[cursorCol + 1] : rowCache.TextLength;
        int length = Math.Max(1, Math.Max(0, end - start));

        Rect caretRect = default;
        foreach (var hit in layout.HitTestTextRange(start, length))
        {
            caretRect = hit;
            break;
        }

        double cursorX = caretRect.Width > 0 ? caretRect.X : cursorCol * _cellWidth;
        double cursorWidth = caretRect.Width > 0 ? caretRect.Width : _cellWidth;

        var cell = buffer.GetCell(cursorRow, cursorCol);
        var cursorBrush = ResolveColorBrush(cell.Foreground, defaultFg);

    var rect = new Rect(Math.Round(cursorX), Math.Round(y), Math.Round(cursorWidth), Math.Round(_cellHeight));
    context.FillRectangle(cursorBrush, rect);
    }

    private void EnsureRowCacheCapacity(int rows)
    {
        if (_rowCaches.Length == rows)
        {
            return;
        }

        if (_rowCaches.Length > rows)
        {
            for (int i = rows; i < _rowCaches.Length; i++)
            {
                _rowCaches[i]?.Dispose();
            }
        }

        int oldLength = _rowCaches.Length;
        Array.Resize(ref _rowCaches, rows);
        for (int i = oldLength; i < rows; i++)
        {
            _rowCaches[i] = new RowLayoutCache();
        }
    }

    private void ClearRowCaches()
    {
        for (int i = 0; i < _rowCaches.Length; i++)
        {
            _rowCaches[i]?.Dispose();
        }

        _rowCaches = Array.Empty<RowLayoutCache>();
    }

    private void EnsureColumnIndexScratch(int size)
    {
        if (_columnToTextIndexScratch.Length < size)
        {
            Array.Resize(ref _columnToTextIndexScratch, size);
        }
    }

    private TextRunProperties GetTextRunProperties(bool bold, IBrush foreground)
    {
    var key = new RunStyleKey(foreground, bold);
        if (_textRunPropertiesCache.TryGetValue(key, out var props))
        {
            return props;
        }

        var typeface = bold ? _boldTypeface : _normalTypeface;
        props = new GenericTextRunProperties(typeface, FontSize, foregroundBrush: foreground);
        _textRunPropertiesCache[key] = props;
        return props;
    }

    private void AppendSignature(in TerminalBuffer.Cell cell)
    {
        _signatureBuilder.Append(cell.Grapheme ?? " ");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Foreground ?? "_");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Background ?? "_");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Bold ? '1' : '0');
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Width);
        _signatureBuilder.Append(cell.IsContinuation ? 'C' : 'B');
        _signatureBuilder.Append(';');
    }

    private sealed class RowLayoutCache : IDisposable
    {
        public string Signature = string.Empty;
        public TextLayout? Layout;
        public int[] ColumnToTextIndex = Array.Empty<int>();
        public int TextLength;

        public void EnsureColumnCapacity(int size)
        {
            if (ColumnToTextIndex.Length != size)
            {
                Array.Resize(ref ColumnToTextIndex, size);
            }
        }

        public void Dispose()
        {
            Layout?.Dispose();
            Layout = null;
            ColumnToTextIndex = Array.Empty<int>();
            Signature = string.Empty;
            TextLength = 0;
        }
    }

    private readonly record struct RunStyleKey(IBrush Brush, bool Bold);

    private IBrush ResolveResourceBrush(IResourceDictionary? resources, string key, IBrush fallback)
    {
        if (resources != null && resources.TryGetResource(key, ThemeVariant.Default, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private IBrush ResolveColorBrush(string? hex, IBrush fallback)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return fallback;
        }

        if (_colorBrushCache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        if (Color.TryParse(hex, out var color))
        {
            var brush = new SolidColorBrush(color);
            _colorBrushCache[hex] = brush;
            return brush;
        }

        return fallback;
    }

    private TextLayout CreateTextLayout(string text, bool bold, IBrush brush)
    {
        var typeface = bold ? _boldTypeface : _normalTypeface;
        return new TextLayout(text, typeface, FontSize, brush, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None);
    }


    private StreamGeometry? _geoRightArrow;
    private StreamGeometry? _geoLeftArrow;
    private StreamGeometry? _geoRightSemicircle;
    private StreamGeometry? _geoLeftSemicircle;

    private static readonly RenderOptions s_aliasRenderOptions = new() { EdgeMode = EdgeMode.Aliased };

    private enum PowerlineGlyphVariant
    {
        FilledRightArrow,
        ThinRightArrow,
        FilledLeftArrow,
        ThinLeftArrow,
        FilledRightArc,
        ThinRightArc,
        FilledLeftArc,
        ThinLeftArc
    }

    private static readonly Dictionary<char, PowerlineGlyphVariant> s_powerlineGlyphVariants = new()
    {
        ['\uE0B0'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0B1'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0B2'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0B3'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0B4'] = PowerlineGlyphVariant.FilledRightArc,
        ['\uE0B5'] = PowerlineGlyphVariant.ThinRightArc,
        ['\uE0B6'] = PowerlineGlyphVariant.FilledLeftArc,
        ['\uE0B7'] = PowerlineGlyphVariant.ThinLeftArc,
        ['\uE0B8'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0B9'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0BA'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0BB'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0BC'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0BD'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0BE'] = PowerlineGlyphVariant.FilledLeftArrow,
        ['\uE0BF'] = PowerlineGlyphVariant.ThinLeftArrow,
        ['\uE0C0'] = PowerlineGlyphVariant.FilledRightArc,
        ['\uE0C1'] = PowerlineGlyphVariant.ThinRightArc,
        ['\uE0C2'] = PowerlineGlyphVariant.FilledLeftArc,
        ['\uE0C3'] = PowerlineGlyphVariant.ThinLeftArc,
        ['\uE0C4'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0C5'] = PowerlineGlyphVariant.ThinRightArrow,
        ['\uE0C6'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0CE'] = PowerlineGlyphVariant.FilledRightArrow,
        ['\uE0CF'] = PowerlineGlyphVariant.ThinRightArrow,
    };

    private void UpdatePowerlineGeometries()
    {
        double w = _cellWidth;
        double h = _cellHeight;

        // E0B0: Right Arrow (Filled)
        _geoRightArrow = new StreamGeometry();
        using (var ctx = _geoRightArrow.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(0, h));
            ctx.LineTo(new Point(w, h / 2));
            ctx.EndFigure(true);
        }

        // E0B2: Left Arrow (Filled)
        _geoLeftArrow = new StreamGeometry();
        using (var ctx = _geoLeftArrow.Open())
        {
            ctx.BeginFigure(new Point(w, 0), true);
            ctx.LineTo(new Point(w, h));
            ctx.LineTo(new Point(0, h / 2));
            ctx.EndFigure(true);
        }

        // E0B4: Right Semicircle (Filled) - curves right
        _geoRightSemicircle = new StreamGeometry();
        using (var ctx = _geoRightSemicircle.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.LineTo(new Point(w, 0));
            ctx.ArcTo(new Point(w, h), new Size(w, h), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0, h));
            ctx.EndFigure(true);
        }

        // E0B6: Left Semicircle (Filled) - curves left
        _geoLeftSemicircle = new StreamGeometry();
        using (var ctx = _geoLeftSemicircle.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true);
            ctx.ArcTo(new Point(0, h), new Size(w, h), 0, false, SweepDirection.CounterClockwise);
            ctx.LineTo(new Point(w, h));
            ctx.LineTo(new Point(w, 0));
            ctx.EndFigure(true);
        }
    }

    private static bool IsPowerlineGlyph(char c)
    {
        return s_powerlineGlyphVariants.ContainsKey(c);
    }

    private void DrawPowerlineGlyph(DrawingContext context, char glyph, double x, double y, IBrush brush)
    {
        if (!s_powerlineGlyphVariants.TryGetValue(glyph, out var variant))
        {
            DrawFallbackRect(context, x, y, brush);
            return;
        }

        switch (variant)
        {
            case PowerlineGlyphVariant.FilledRightArrow:
                DrawFilledGeometry(context, _geoRightArrow, x, y, brush);
                break;
            case PowerlineGlyphVariant.FilledLeftArrow:
                DrawFilledGeometry(context, _geoLeftArrow, x, y, brush);
                break;
            case PowerlineGlyphVariant.FilledRightArc:
                DrawFilledGeometry(context, _geoRightSemicircle, x, y, brush);
                break;
            case PowerlineGlyphVariant.FilledLeftArc:
                DrawFilledGeometry(context, _geoLeftSemicircle, x, y, brush);
                break;
            case PowerlineGlyphVariant.ThinRightArrow:
                DrawOutlineGeometry(context, _geoRightArrow, x, y, brush);
                break;
            case PowerlineGlyphVariant.ThinLeftArrow:
                DrawOutlineGeometry(context, _geoLeftArrow, x, y, brush);
                break;
            case PowerlineGlyphVariant.ThinRightArc:
                DrawOutlineGeometry(context, _geoRightSemicircle, x, y, brush);
                break;
            case PowerlineGlyphVariant.ThinLeftArc:
                DrawOutlineGeometry(context, _geoLeftSemicircle, x, y, brush);
                break;
            default:
                DrawFallbackRect(context, x, y, brush);
                break;
        }
    }

    private void DrawFilledGeometry(DrawingContext context, StreamGeometry? geometry, double x, double y, IBrush brush)
    {
        if (geometry is null)
        {
            DrawFallbackRect(context, x, y, brush);
            return;
        }

        using var alias = context.PushRenderOptions(s_aliasRenderOptions);
        using var state = context.PushTransform(Matrix.CreateTranslation(Snap(x), Snap(y)));
        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawOutlineGeometry(DrawingContext context, StreamGeometry? geometry, double x, double y, IBrush brush)
    {
        if (geometry is null)
        {
            DrawFallbackRect(context, x, y, brush);
            return;
        }

        var thickness = Math.Max(1, _cellWidth * 0.18);
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };

        using var alias = context.PushRenderOptions(s_aliasRenderOptions);
        using var state = context.PushTransform(Matrix.CreateTranslation(Snap(x), Snap(y)));
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawFallbackRect(DrawingContext context, double x, double y, IBrush brush)
    {
        var rect = CreateSnappedRect(x, y, _cellWidth, _cellHeight);
        context.FillRectangle(brush, rect);
    }

    private double Snap(double value)
    {
        var scale = _renderScaling <= 0 ? 1.0 : _renderScaling;
        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    private Rect CreateSnappedRect(double x, double y, double width, double height)
    {
        var left = Snap(x);
        var top = Snap(y);
        var right = Snap(x + width);
        var bottom = Snap(y + height);
        var scale = _renderScaling <= 0 ? 1.0 : _renderScaling;
        double min = 1.0 / scale;
        return new Rect(left, top, Math.Max(right - left, min), Math.Max(bottom - top, min));
    }
}
