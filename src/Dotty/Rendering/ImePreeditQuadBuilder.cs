using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>Theme-derived preedit colours: text on an opaque background, the focused clause in selection colours.</summary>
internal readonly record struct PreeditColors(SgrColorArgb Foreground, SgrColorArgb Background, SgrColorArgb FocusedBackground)
{
    /// <summary>
    /// Colours from the pane's default foreground/background and the theme selection colour (alpha dropped:
    /// the preedit is drawn opaque so the cells it covers never show through).
    /// </summary>
    public static PreeditColors FromTheme(SgrColorArgb foreground, SgrColorArgb background, SgrColorArgb selection) =>
        new(Opaque(foreground), Opaque(background), selection.IsEmpty ? Opaque(foreground) : Opaque(selection));

    private static SgrColorArgb Opaque(SgrColorArgb color) => new(color.Argb | 0xFF000000u);
}

/// <summary>Emits the ephemeral, cell-aligned composition text for an IME, laid out by <see cref="PreeditLayout"/>.</summary>
internal sealed class ImePreeditQuadBuilder
{
    private readonly List<PreeditCell> _cells = new();
    private CellInstance[] _cachedInstances = Array.Empty<CellInstance>();
    private int _cachedCount;
    private bool _hasCachedState;
    private ImePreeditState _cachedState;
    private GlyphAtlas? _cachedAtlas;
    private SKTypeface? _cachedTypeface;
    private object? _cachedFallbackChain;
    private float _cachedFontSize;
    private int _cachedStartColumn;
    private int _cachedStartRow;
    private PreeditBox _cachedBox;
    private PreeditColors _cachedColors;
    private PreeditLayoutResult _cachedLayout;

    /// <summary>Upper bound of instances <see cref="Build"/> writes for <paramref name="state"/>.</summary>
    public static int MaxInstances(in ImePreeditState state) => state.Text?.Length ?? 0;

    public int Build(
        in ImePreeditState state,
        int startColumn,
        int startRow,
        PreeditBox box,
        PreeditColors colors,
        Span<CellInstance> destination,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        HashSet<int> dirtyAtlasRows,
        out PreeditLayoutResult layout)
    {
        if (_hasCachedState && state == _cachedState
            && ReferenceEquals(atlas, _cachedAtlas)
            && ReferenceEquals(typeface, _cachedTypeface)
            && ReferenceEquals(atlas.FallbackChain, _cachedFallbackChain)
            && fontSize.Equals(_cachedFontSize)
            && startColumn == _cachedStartColumn
            && startRow == _cachedStartRow
            && box == _cachedBox
            && colors == _cachedColors)
        {
            _cachedInstances.AsSpan(0, _cachedCount).CopyTo(destination);
            layout = _cachedLayout;
            return _cachedCount;
        }

        layout = PreeditLayout.Compute(in state, startColumn, startRow, box, _cells);
        int count = Emit(in state, destination, atlas, typeface, fontSize, colors, dirtyAtlasRows);
        if (_cachedInstances.Length < count)
            Array.Resize(ref _cachedInstances, Math.Max(count, _cachedInstances.Length == 0 ? 8 : _cachedInstances.Length * 2));
        destination[..count].CopyTo(_cachedInstances);
        _cachedState = state;
        _cachedAtlas = atlas;
        _cachedTypeface = typeface;
        _cachedFallbackChain = atlas.FallbackChain;
        _cachedFontSize = fontSize;
        _cachedStartColumn = startColumn;
        _cachedStartRow = startRow;
        _cachedBox = box;
        _cachedColors = colors;
        _cachedLayout = layout;
        _cachedCount = count;
        _hasCachedState = true;
        return count;
    }

    /// <summary>
    /// Removes from <paramref name="instances"/> the terminal cells the last built preedit covers, so their glyphs
    /// (drawn in a later pass than every background) cannot show through the opaque preedit. Returns the new count.
    /// </summary>
    public int RemoveCovered(Span<CellInstance> instances) =>
        RemoveCovered(instances, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_cells));

    internal static int RemoveCovered(Span<CellInstance> instances, ReadOnlySpan<PreeditCell> cells)
    {
        if (cells.IsEmpty)
            return instances.Length;
        int written = 0;
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly CellInstance instance = ref instances[i];
            int width = (instance.Flags & CellFlags.WideCell) != 0 ? 2 : 1;
            bool covered = false;
            foreach (var cell in cells)
            {
                if (cell.Row == instance.Row && instance.Col < cell.Column + cell.Width && instance.Col + width > cell.Column)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                instances[written++] = instances[i];
        }
        return written;
    }

    private int Emit(
        in ImePreeditState state,
        Span<CellInstance> destination,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        PreeditColors colors,
        HashSet<int> dirtyAtlasRows)
    {
        string text = state.Text;
        int written = 0;
        foreach (var cell in _cells)
        {
            if (written >= destination.Length)
                break;
            if (cell.Column < 0 || cell.Row < 0 || cell.Column + cell.Width > ushort.MaxValue || cell.Row > ushort.MaxValue)
                continue;
            string grapheme = text.Substring(cell.Utf16Start, cell.Utf16Length);
            var key = new GlyphKey(grapheme, typeface, fontSize, GlyphStyle.Regular);
            int oldEntryCount = atlas.EntryCount;
            bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
            if (!glyphOk) glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
            if (atlas.EntryCount > oldEntryCount) dirtyAtlasRows.Add(cell.Row);
            bool focused = cell.Style == PreeditCellStyle.Focused;
            SgrColorArgb background = focused ? colors.FocusedBackground : colors.Background;
            byte flags = (byte)((cell.Width == 2 ? CellFlags.WideCell : 0)
                | (focused ? 0 : CellFlags.Underline)
                | (glyphOk && glyphInfo.IsColor ? CellFlags.ColorGlyph : 0));
            destination[written++] = new CellInstance
            {
                Col = (ushort)cell.Column,
                Row = (ushort)cell.Row,
                OffX = glyphOk ? (short)glyphInfo.LeftBearing : (short)0,
                OffY = glyphOk ? QuadFrameBuilder.RoundPixelOffset(glyphInfo.BaselineOffset + glyphInfo.TopBearing) : (short)0,
                GlyphX = glyphOk ? (short)glyphInfo.X : (short)0,
                GlyphY = glyphOk ? (short)glyphInfo.Y : (short)0,
                GlyphW = glyphOk ? (short)glyphInfo.Width : (short)0,
                GlyphH = glyphOk ? (short)glyphInfo.Height : (short)0,
                FgR = colors.Foreground.R,
                FgG = colors.Foreground.G,
                FgB = colors.Foreground.B,
                FgA = 255,
                Flags = flags,
                BgR = background.R,
                BgG = background.G,
                BgB = background.B,
                BgA = 255,
            };
        }
        return written;
    }
}
