using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.Utilities;
using Dotty.App.Controls.Rendering;
using Dotty.App.Services;
using Dotty.App.Controls.Selection;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls;

/// <summary>
/// Immediate-mode terminal renderer that draws the buffer onto a single drawing surface.
/// This avoids building thousands of TextBlocks and keeps each cell aligned to a consistent grid.
/// </summary>
public class TerminalCanvas : Control
{
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalCanvas, FontFamily>(nameof(FontFamily), new FontFamily("monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalCanvas, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<TerminalBuffer?> BufferProperty =
        AvaloniaProperty.Register<TerminalCanvas, TerminalBuffer?>(nameof(Buffer));

    public static readonly StyledProperty<bool> ShowCursorProperty =
        AvaloniaProperty.Register<TerminalCanvas, bool>(nameof(ShowCursor), true);

    public static readonly StyledProperty<Thickness> ContentPaddingProperty =
        AvaloniaProperty.Register<TerminalCanvas, Thickness>(nameof(ContentPadding), new Thickness(0));

    public static readonly StyledProperty<TerminalCursorShape> CursorShapeProperty =
        AvaloniaProperty.Register<TerminalCanvas, TerminalCursorShape>(nameof(CursorShape), TerminalCursorShape.Block);

    public TerminalBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

#pragma warning disable IL2075
    public FontFamily FontFamily
    {
#pragma warning restore IL2075
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

    public Thickness ContentPadding
    {
        get => GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public TerminalCursorShape CursorShape
    {
        get => GetValue(CursorShapeProperty);
        set => SetValue(CursorShapeProperty, value);
    }

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

    private readonly FontFamily _fallbackFont;
    private static string? _lastReportedFontSignature;
    private readonly Dictionary<RunStyleKey, TextRunProperties> _textRunPropertiesCache = new();
    private Typeface _normalTypeface;
    private Typeface _boldTypeface;
    private Typeface _italicTypeface;
    private Typeface _boldItalicTypeface;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private bool _metricsDirty = true;
    private double _renderScaling = 1.0;

    private readonly TextLayoutCache _layoutCache = new();
    private readonly PowerlineGlyphRenderer _powerline = new();
    private readonly BackgroundPainter _backgroundPainter = new();
    private readonly UnderlinePainter _underlinePainter = new();
    private readonly CursorPainter _cursorPainter = new();
    private readonly SelectionPainter _selectionPainter = new();
    private readonly SelectionModel _selection = new();
    private readonly BrushResolver _brushResolver = new();
    // (map inspection logs removed; no need for a map-inspection flag)

    static TerminalCanvas()
    {
        AffectsRender<TerminalCanvas>(BufferProperty, ShowCursorProperty, FontFamilyProperty, FontSizeProperty, ContentPaddingProperty, CursorShapeProperty);
        AffectsMeasure<TerminalCanvas>(BufferProperty, FontFamilyProperty, FontSizeProperty, ContentPaddingProperty);
    }

    public TerminalCanvas()
    {
        UseLayoutRounding = true;
        _fallbackFont = new FontFamily("monospace");

        _normalTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Normal, FontWeight.Normal);
        _boldTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Normal, FontWeight.Bold);
        _italicTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Italic, FontWeight.Normal);
        _boldItalicTypeface = new Typeface(FontFamily ?? _fallbackFont, FontStyle.Italic, FontWeight.Bold);

        AddHandler(PointerPressedEvent, PointerPressedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, PointerMovedHandler, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, PointerReleasedHandler, RoutingStrategies.Tunnel);
    }

    private void PointerPressedHandler(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(this);
            if (PositionToBufferCell(pos, out var row, out var col))
            {
                if (_selection.Begin(row, col))
                {
                    InvalidateVisual();
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "PointerPressed");
        }
    }

    private void PointerMovedHandler(object? sender, PointerEventArgs e)
    {
        if (!_selection.IsSelecting)
        {
            return;
        }

        try
        {
            var pos = e.GetPosition(this);
            if (PositionToBufferCell(pos, out var row, out var col))
            {
                if (_selection.Update(row, col))
                {
                    InvalidateVisual();
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "PointerMoved");
        }
    }

    private void PointerReleasedHandler(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selection.IsSelecting)
        {
            return;
        }

        try
        {
            var pos = e.GetPosition(this);
            if (PositionToBufferCell(pos, out var row, out var col))
            {
                _selection.End(row, col);
            }
            else
            {
                _selection.EndWithoutPosition();
            }

            InvalidateVisual();

            var buffer = Buffer;
            if (buffer != null)
            {
                var text = _selection.GetSelectedText(buffer);
                if (!string.IsNullOrEmpty(text))
                {
                    _ = CopyToClipboardAsync(text);
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "PointerReleased");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearCaches();
        _selection.Clear();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _metricsDirty = true;
            ClearCaches();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        var buffer = Buffer;
        var padding = ContentPadding;
        double width = padding.Left + padding.Right;
        double height = padding.Top + padding.Bottom;

        if (buffer != null)
        {
            width += buffer.Columns * _cellWidth;
            height += buffer.Rows * _cellHeight;
        }

        return new Size(width, height);
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
        var selectionBg = ResolveResourceBrush(resources, "TerminalSelectionBackground", Brushes.DodgerBlue);
        var selectionFg = ResolveResourceBrush(resources, "TerminalSelectionForeground", Brushes.White);

        _brushResolver.UpdateDefaults(defaultFg, defaultBg);

        var padding = ContentPadding;
        var bounds = Bounds;
        context.FillRectangle(defaultBg, new Rect(new Point(0, 0), bounds.Size));

        bool drawCursor = ShowCursor && buffer.CursorVisible;
        int cursorRow = buffer.CursorRow;
        int cursorCol = buffer.CursorCol;

        double contentHeight = buffer.Rows * _cellHeight;
        double availableHeight = bounds.Height;
        double originX = padding.Left;
        double extraVertical = Math.Max(0, availableHeight - (padding.Top + padding.Bottom + contentHeight));
        double originY = padding.Top + extraVertical;

        Func<double, double, double, double, Rect> snapRect = CreateSnappedRect;

        Func<Cell, IBrush> resolveBackground = cell => _brushResolver.Background(cell);
        Func<Cell, IBrush> resolveForeground = cell => _brushResolver.Foreground(cell);
        Func<Cell, IBrush> resolveUnderline = cell => _brushResolver.Underline(cell);
        Func<Cell, (IBrush fg, IBrush bg)> resolveCursorBrushes = cell => _brushResolver.Cursor(cell);

        _layoutCache.EnsureRowCapacity(buffer.Rows);

        for (int row = 0; row < buffer.Rows; row++)
        {
            var rowY = originY + row * _cellHeight;
            bool rowHasCursor = drawCursor && row == cursorRow;
            var rowCache = BuildRowLayout(buffer, row, defaultFg, defaultBg);

            _backgroundPainter.PaintRowBackgrounds(
                context,
                buffer,
                row,
                originX,
                rowY,
                _cellWidth,
                _cellHeight,
                _selection,
                selectionBg,
                resolveBackground,
                snapRect);

            if (rowCache.Layout is { } layout)
            {
                var layoutHeight = layout.Height;
                var textY = Math.Round(rowY + Math.Max(0, (_cellHeight - layoutHeight) / 2));
                layout.Draw(context, new Point(originX, textY));

                try
                {
                    _selectionPainter.DrawSelectedTextOverlay(
                        context,
                        buffer,
                        _selection,
                        rowCache,
                        row,
                        originX,
                        textY,
                        (bold, italic) => GetTextRunProperties(bold, italic, selectionFg),
                        FindSupportingTypeface,
                        FontSize,
                        selectionFg);
                }
                catch
                {
                }

                for (int col = 0; col < buffer.Columns; col++)
                {
                    var cell = buffer.GetCell(row, col);
                    if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme))
                    {
                        continue;
                    }

                    // (debug-only probes removed)

// Use the grapheme's full codepoint (handles surrogate pairs) for debug checks
                    var glyph = cell.Grapheme[0];
                    int debugCode = char.IsSurrogate(cell.Grapheme, 0) ? char.ConvertToUtf32(cell.Grapheme, 0) : (int)glyph;
                    // (debug-only glyph-available probes removed)
                    if (!_powerline.IsPowerlineGlyph(glyph))
                    {
                        continue;
                    }

                    var fgBrush = resolveForeground(cell);
                    _powerline.Draw(context, glyph, originX + col * _cellWidth, rowY, fgBrush, _cellWidth, _cellHeight, _renderScaling);
                }

                _underlinePainter.DrawUnderlines(
                    context,
                    buffer,
                    row,
                    originX,
                    rowY,
                    _cellWidth,
                    _cellHeight,
                    _renderScaling,
                    resolveForeground,
                    resolveUnderline,
                    snapRect);
            }

            if (rowHasCursor && drawCursor)
            {
                _cursorPainter.DrawCursor(
                    context,
                    buffer,
                    rowCache,
                    cursorRow,
                    cursorCol,
                    originX,
                    rowY,
                    _cellWidth,
                    _cellHeight,
                    _renderScaling,
                    CursorShape,
                    resolveCursorBrushes,
                    snapRect);
            }
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_selection.HasSelection)
        {
            _selection.Clear();
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selection.HasSelection)
        {
            _selection.Clear();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
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
        _italicTypeface = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
        _boldItalicTypeface = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        ReportFontUsage(family);

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

        _powerline.UpdateGeometries(_cellWidth, _cellHeight);

        _metricsDirty = false;
    }

    private RowLayoutCache BuildRowLayout(TerminalBuffer buffer, int row, IBrush defaultFg, IBrush defaultBg)
    {
        return _layoutCache.BuildRowLayout(
            buffer,
            row,
            defaultFg,
            cell => _brushResolver.Foreground(cell),
            GetTextRunProperties,
            FindSupportingTypeface,
            _normalTypeface,
            FontSize);
    }

    private Typeface? FindSupportingTypeface(string grapheme, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(grapheme)) return null;
        // Use full Unicode codepoint (handles surrogate pairs / emoji etc.)
        int codepoint = char.IsSurrogate(grapheme, 0) ? char.ConvertToUtf32(grapheme, 0) : (int)grapheme[0];

        var primary = SelectTypeface(bold, italic);

        // Only attempt per-grapheme fallback for characters that are likely to be
        // symbol/glyph characters (box drawing, PUA icons, emoji, bullets). Avoid
        // trying to 'fix' spaces, ASCII letters, etc. Do not probe fonts for
        // common ASCII to avoid expensive HarfBuzz queries on every cell.
        if (!Services.FontHelpers.IsLikelySymbol(codepoint))
        {
            return null;
        }

        if (GlyphAvailableInTypeface(primary, codepoint))
        {
            // primary supports it
            if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_GLYPHS") == "1")
            {
                var msg = $"[Dotty][GlyphDbg] primary supports U+{codepoint:X4} -> using primary";
                try { Console.WriteLine(msg); } catch { }
                AppendTestLog(msg);
            }
            return primary;
        }

        // Try configured font stack candidates
        var candidates = Defaults.DefaultFontStack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            try
            {
                var fam = new FontFamily(candidate);
                var tf = new Typeface(fam, primary.Style, primary.Weight);
                    if (GlyphAvailableInTypeface(tf, codepoint))
                    {
                        if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_GLYPHS") == "1")
                        {
                            var msg = $"[Dotty][GlyphDbg] candidate {candidate} supports U+{codepoint:X4}";
                            try { Console.WriteLine(msg); } catch { }
                            AppendTestLog(msg);
                        }
                        return tf;
                    }
            }
            catch (Exception)
            {
                // ignore errors checking a candidate family
            }
        }

        // As a last resort for likely symbol characters, try a small set of known symbol
        // families but only select them if they actually contain the glyph.
        if (Services.FontHelpers.IsLikelySymbol(codepoint))
        {
            var fallbackFamilies = new[] { "Symbols Nerd Font Mono", "Symbols Nerd Font", Defaults.DefaultFontFamily };
            foreach (var famName in fallbackFamilies)
            {
                try
                {
                    var fam = new FontFamily(famName);
                    var tf = new Typeface(fam, primary.Style, primary.Weight);
                    if (GlyphAvailableInTypeface(tf, codepoint))
                    {
                        if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_GLYPHS") == "1")
                        {
                            var msg = $"[Dotty][GlyphDbg] fallback family {famName} supports U+{codepoint:X4}";
                            try { Console.WriteLine(msg); } catch { }
                            AppendTestLog(msg);
                        }
                        return tf;
                    }
                }
                catch { }
            }

            // Search system fonts for likely candidates (names containing NERD or SYMBOL)
            foreach (var fam in FontManager.Current.SystemFonts)
            {
                try
                {
                    var name = fam.Name ?? string.Empty;
                    var norm = name.ToUpperInvariant();
                    if (!norm.Contains("NERD") && !norm.Contains("SYMBOL") && !norm.Contains("FONTAWESOME") && !norm.Contains("POWERLINE"))
                    {
                        continue;
                    }

                    var tf = new Typeface(fam, primary.Style, primary.Weight);
                    if (GlyphAvailableInTypeface(tf, codepoint))
                    {
                        return tf;
                    }
                }
                catch { }
            }

            // No explicit fallback family matched. We will rely on the OS/text-engine fallback
            if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_GLYPHS") == "1")
            {
                var msg = $"[Dotty][GlyphDbg] No explicit fallback found for U+{codepoint:X4}";
                try { Console.WriteLine(msg); } catch { }
                AppendTestLog(msg);
            }
            // No explicit fallback found; rely on system/text-engine fallback
        }

        return null;
    }

    private static bool GlyphAvailableInTypeface(Typeface? tf, char ch)
    {
        return GlyphAvailableInTypeface(tf, (int)ch);
    }

    private static bool GlyphAvailableInTypeface(Typeface? tf, int codepoint)
    {
        if (tf == null) return false;
        if (!FontManager.Current.TryGetGlyphTypeface((Typeface)tf, out var gf)) return false;

        try
        {
            var prop = gf.GetType().GetProperty("CharacterToGlyphMap") ?? gf.GetType().GetProperty("CharacterMap");
            if (prop != null)
            {
                var map = prop.GetValue(gf) as System.Collections.IDictionary;
                if (map != null)
                {
                    // (map inspection logs removed)

                    // Keys may be ints rather than char; check multiple key types
                    if (codepoint <= 0xFFFF)
                    {
                        var ch = (char)codepoint;
                        if (map.Contains(ch)) return true;
                    }

                    if (map.Contains(codepoint)) return true;
                    if (map.Contains((uint)codepoint)) return true;
                    if (map.Contains((long)codepoint)) return true;
                }
            }
        }
        catch { }

        // If no CharacterMap or it didn't contain the codepoint, try HarfBuzz Font TryGet... methods
        try
        {
            var fontProp = gf.GetType().GetProperty("Font");
            var font = fontProp?.GetValue(gf);
            if (font != null)
            {
                var cp = (uint)codepoint;
                var tryNames = new[] { "TryGetNominalGlyph", "TryGetGlyph", "TryGetVariationGlyph" };
                foreach (var name in tryNames)
                {
                    // Suppress ILLinker trimming warning for runtime method lookup
#pragma warning disable IL2075
                    var meth = font.GetType().GetMethod(name, new[] { typeof(uint), typeof(uint).MakeByRefType() })
                              ?? font.GetType().GetMethod(name, new[] { typeof(int), typeof(int).MakeByRefType() });
#pragma warning restore IL2075
                        if (meth != null)
                        {
                            var outParamType = meth.GetParameters()[1].ParameterType.GetElementType();
                            object outValue = outParamType == typeof(uint) ? (object)0u : (object)0;
                            var args = new object[] { cp, outValue };
                            var okObj = meth.Invoke(font, args);
                            var ok = okObj is bool b && b;
                            // (HarfBuzz TryGet logs removed to avoid noisy output)
                            if (ok)
                            {
                                var glyphObj = args[1];
                                if (glyphObj is uint gUint)
                                {
                                    if (gUint != 0) return true;
                                }
                                else if (glyphObj is int gInt)
                                {
                                    if (gInt != 0) return true;
                                }
                                else if (glyphObj != null && !glyphObj.Equals(0))
                                {
                                    return true;
                                }
                            }
                        }
                }
            }
        }
        catch { }

        return false;
    }

    private static void AppendTestLog(string line)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("DOTTY_TEST_OUTPUT");
            if (string.IsNullOrEmpty(path)) return;
            // Ensure directory exists
            try { Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "."); } catch { }
            File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    // Symbol heuristics were moved to `FontHelpers` to make them testable.

    private void ClearCaches()
    {
        _layoutCache.Clear();
        _textRunPropertiesCache.Clear();
        _brushResolver.ClearCaches();
    }

    private TextRunProperties GetTextRunProperties(bool bold, bool italic, IBrush foreground)
    {
        var key = new RunStyleKey(foreground, bold, italic);
        if (_textRunPropertiesCache.TryGetValue(key, out var props))
        {
            return props;
        }

        var typeface = SelectTypeface(bold, italic);
        props = new GenericTextRunProperties(typeface, FontSize, foregroundBrush: foreground);
        _textRunPropertiesCache[key] = props;
        return props;
    }

    private readonly record struct RunStyleKey(IBrush Foreground, bool Bold, bool Italic);

    private Typeface SelectTypeface(bool bold, bool italic)
    {
        if (bold && italic) return _boldItalicTypeface;
        if (bold) return _boldTypeface;
        if (italic) return _italicTypeface;
        return _normalTypeface;
    }

    private IBrush ResolveResourceBrush(IResourceDictionary? resources, string key, IBrush fallback)
    {
        if (resources != null && resources.TryGetResource(key, ThemeVariant.Default, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private TextLayout CreateTextLayout(string text, bool bold, IBrush brush)
    {
        var typeface = bold ? _boldTypeface : _normalTypeface;
        return new TextLayout(text, typeface, FontSize, brush, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None);
    }

    private void ReportFontUsage(FontFamily? family)
    {
        var signature = $"{family?.ToString() ?? "(unknown)"}|{FontSize:0.##}";
        if (signature == _lastReportedFontSignature)
        {
            return;
        }

        _lastReportedFontSignature = signature;

        try
        {
            var source = family?.ToString() ?? "(unknown)";
            Console.WriteLine($"[Dotty] Terminal font in use: {source} @ {FontSize:0.##}pt");
        }
        catch
        {
            // ignore logging errors
        }
    }

    private bool PositionToBufferCell(Point pos, out int row, out int col)
    {
        row = 0; col = 0;
        var buffer = Buffer;
        if (buffer == null) return false;
        EnsureMetrics();
        var padding = ContentPadding;
        double contentHeight = buffer.Rows * _cellHeight;
        double availableHeight = Bounds.Height;
        double originX = padding.Left;
        double extraVertical = Math.Max(0, availableHeight - (padding.Top + padding.Bottom + contentHeight));
        double originY = padding.Top + extraVertical;

        double relX = pos.X - originX;
        double relY = pos.Y - originY;

        if (relX < 0 || relY < 0 || relX >= buffer.Columns * _cellWidth || relY >= buffer.Rows * _cellHeight)
        {
            return false;
        }

        col = Math.Clamp((int)Math.Floor(relX / _cellWidth), 0, buffer.Columns - 1);
        row = Math.Clamp((int)Math.Floor(relY / _cellHeight), 0, buffer.Rows - 1);
        return true;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            var clipboard = top?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "ClipboardCopy");
        }
    }

    private static void LogError(Exception ex, string context)
    {
        try
        {
            Console.WriteLine($"[Dotty] TerminalCanvas {context} error: {ex.Message}");
        }
        catch
        {
            // avoid throwing from logging
        }
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

public enum TerminalCursorShape
{
    Block,
    Beam,
    Underline
}
