using System;
using System.Globalization;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>Emits the ephemeral, cell-aligned composition text for an IME.</summary>
internal sealed class ImePreeditQuadBuilder
{
    private CellInstance[] _cachedInstances = Array.Empty<CellInstance>();
    private int _cachedCount;
    private bool _hasCachedState;
    private ImePreeditState _cachedState;
    private GlyphAtlas? _cachedAtlas;
    private SKTypeface? _cachedTypeface;
    private object? _cachedFallbackChain;
    private float _cachedFontSize;
    private int _cachedFirstColumn;
    private int _cachedRow;
    private int _cachedVisibleColumns;
    private int _cachedCaretCellOffset;
    private static readonly SgrColorArgb Foreground = SgrColorArgb.FromRgb(255, 255, 255);
    private static readonly SgrColorArgb SelectionBackground = SgrColorArgb.FromRgb(64, 112, 180);

    public int Build(
        in ImePreeditState state,
        int firstColumn,
        int row,
        int visibleColumns,
        Span<CellInstance> destination,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        HashSet<int> dirtyAtlasRows,
        out int caretCellOffset)
    {
        caretCellOffset = 0;
        if (!state.IsActive || visibleColumns <= 0 || destination.IsEmpty)
            return 0;

        if (_hasCachedState && state == _cachedState
            && ReferenceEquals(atlas, _cachedAtlas)
            && ReferenceEquals(typeface, _cachedTypeface)
            && ReferenceEquals(atlas.FallbackChain, _cachedFallbackChain)
            && fontSize.Equals(_cachedFontSize)
            && firstColumn == _cachedFirstColumn
            && row == _cachedRow
            && visibleColumns == _cachedVisibleColumns)
        {
            _cachedInstances.AsSpan(0, _cachedCount).CopyTo(destination);
            caretCellOffset = _cachedCaretCellOffset;
            return _cachedCount;
        }

        int count = BuildGlyphs(in state, firstColumn, row, visibleColumns, destination, atlas,
            typeface, fontSize, dirtyAtlasRows, out caretCellOffset);
        if (_cachedInstances.Length < count)
            Array.Resize(ref _cachedInstances, Math.Max(count, _cachedInstances.Length == 0 ? 8 : _cachedInstances.Length * 2));
        destination[..count].CopyTo(_cachedInstances);
        _cachedState = state;
        _cachedAtlas = atlas;
        _cachedTypeface = typeface;
        _cachedFallbackChain = atlas.FallbackChain;
        _cachedFontSize = fontSize;
        _cachedFirstColumn = firstColumn;
        _cachedRow = row;
        _cachedVisibleColumns = visibleColumns;
        _cachedCaretCellOffset = caretCellOffset;
        _cachedCount = count;
        _hasCachedState = true;
        return count;
    }
    private static int BuildGlyphs(
        in ImePreeditState state,
        int firstColumn,
        int row,
        int visibleColumns,
        Span<CellInstance> destination,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        HashSet<int> dirtyAtlasRows,
        out int caretCellOffset)
    {
        caretCellOffset = 0;
        if (!state.IsActive || visibleColumns <= 0 || destination.IsEmpty)
            return 0;

        ReadOnlySpan<char> text = state.Text.AsSpan();
        int selectionStart = Math.Clamp(state.SelectionStart, 0, text.Length);
        int selectionLength = Math.Clamp(state.SelectionLength, 0, text.Length - selectionStart);
        int selectionEnd = selectionStart + selectionLength;
        int caretUtf16 = Math.Clamp(state.CaretIndex, 0, text.Length);
        int written = 0;
        int utf16 = 0;
        int cellOffset = 0;
        bool caretResolved = false;
        while (utf16 < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text[utf16..]);
            if (length <= 0) break;
            ReadOnlySpan<char> graphemeSpan = text.Slice(utf16, length);
            int width = UnicodeWidth.GetWidth(graphemeSpan);
            if (!caretResolved && caretUtf16 <= utf16)
            {
                caretCellOffset = cellOffset;
                caretResolved = true;
            }
            bool selected = selectionStart < utf16 + length && selectionEnd > utf16;
            int visibleStart = Math.Max(cellOffset, 0);
            int visibleEnd = Math.Min(cellOffset + width, visibleColumns);
            // Instances are cell quads, not clipped glyph fragments. Do not let a
            // wide grapheme straddle the pane edge and bleed into its neighbour.
            if (width > 0 && visibleStart == cellOffset && visibleEnd == cellOffset + width
                && firstColumn + cellOffset >= 0 && firstColumn + cellOffset + width <= ushort.MaxValue)
            {
                if (written >= destination.Length) break;
                string grapheme = graphemeSpan.ToString();
                var key = new GlyphKey(grapheme, typeface, fontSize, false);
                int oldEntryCount = atlas.EntryCount;
                bool glyphOk = atlas.EnsureGlyph(key, out var glyphInfo);
                if (!glyphOk) glyphOk = atlas.TryGetFallbackGlyph(out glyphInfo);
                if (atlas.EntryCount > oldEntryCount) dirtyAtlasRows.Add(row);
                destination[written++] = new CellInstance
                {
                    Col = (ushort)(firstColumn + cellOffset),
                    Row = (ushort)row,
                    OffX = glyphOk ? (short)glyphInfo.LeftBearing : (short)0,
                    OffY = glyphOk ? (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing) : (short)0,
                    GlyphX = glyphOk ? (short)glyphInfo.X : (short)0,
                    GlyphY = glyphOk ? (short)glyphInfo.Y : (short)0,
                    GlyphW = glyphOk ? (short)glyphInfo.Width : (short)0,
                    GlyphH = glyphOk ? (short)glyphInfo.Height : (short)0,
                    FgR = Foreground.R,
                    FgG = Foreground.G,
                    FgB = Foreground.B,
                    FgA = 255,
                    Flags = (byte)((width == 2 ? CellFlags.WideCell : 0) | CellFlags.Underline | (glyphOk && glyphInfo.IsColor ? CellFlags.ColorGlyph : 0)),
                    BgR = SelectionBackground.R,
                    BgG = SelectionBackground.G,
                    BgB = SelectionBackground.B,
                    BgA = selected ? (byte)255 : (byte)0
                };
            }
            if (!caretResolved && caretUtf16 < utf16 + length)
            {
                caretCellOffset = cellOffset;
                caretResolved = true;
            }
            cellOffset += width;
            utf16 += length;
        }
        if (!caretResolved) caretCellOffset = cellOffset;
        return written;
    }

}
