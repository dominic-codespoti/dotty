using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class TextLayoutCache
{
    private RowLayoutCache[] _rows = Array.Empty<RowLayoutCache>();
    private readonly StringBuilder _rowTextBuilder = new();
    private readonly StringBuilder _signatureBuilder = new();
    private readonly List<ValueSpan<TextRunProperties>> _styleSpanScratch = new();
    private int[] _columnToTextIndexScratch = Array.Empty<int>();

    public void EnsureRowCapacity(int rows)
    {
        if (_rows.Length == rows)
        {
            return;
        }

        if (_rows.Length > rows)
        {
            for (int i = rows; i < _rows.Length; i++)
            {
                _rows[i]?.Dispose();
            }
        }

        int oldLength = _rows.Length;
        Array.Resize(ref _rows, rows);
        for (int i = oldLength; i < rows; i++)
        {
            _rows[i] = new RowLayoutCache();
        }
    }

    public RowLayoutCache BuildRowLayout(
        TerminalBuffer buffer,
        int row,
        IBrush defaultFg,
        Func<Cell, IBrush> resolveForeground,
        Func<bool, bool, IBrush, TextRunProperties> getRunProps,
        Func<string, bool, bool, Typeface?>? findTypefaceForGrapheme,
        Typeface normalTypeface,
        double fontSize)
    {
        var cache = _rows[row];
        EnsureColumnIndexScratch(buffer.Columns + 1);

        _rowTextBuilder.Clear();
        _signatureBuilder.Clear();
        _styleSpanScratch.Clear();

        int textIndex = 0;
        TextRunProperties? currentProps = null;
        int currentSpanStart = 0;

        int lastBaseStart = 0;
        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation)
            {
                _columnToTextIndexScratch[col] = lastBaseStart;
                AppendSignature(cell);
                continue;
            }

            _columnToTextIndexScratch[col] = textIndex;
            lastBaseStart = textIndex;
            AppendSignature(cell);

            var glyph = cell.Grapheme;
            if (string.IsNullOrEmpty(glyph))
            {
                glyph = " ";
            }

#if DEBUG
            if (findTypefaceForGrapheme != null)
            {
                try
                {
                    var tfCheck = findTypefaceForGrapheme(glyph, cell.Bold, cell.Italic);
                    if (tfCheck == null)
                    {
                        // Only dump when grapheme looks like a likely symbol (PUA/box-drawing/bullets)
                        bool likely = false;
                        if (!string.IsNullOrEmpty(glyph))
                        {
                            var c = glyph[0];
                            var code = (int)c;
                            if ((code >= 0xE000 && code <= 0xF8FF) || (code >= 0x2500 && code <= 0x27BF) || code == 0x2022)
                                likely = true;
                        }

                        if (likely)
                        {
                            // Dump a small neighborhood
                            int start = Math.Max(0, col - 8);
                            int end = Math.Min(buffer.Columns - 1, col + 8);
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"[Dotty][Debug] Buffer dump around r{row}c{col} (no fallback found for '{glyph}'):");
                            for (int ccol = start; ccol <= end; ccol++)
                            {
                                var cell2 = buffer.GetCell(row, ccol);
                                var g = cell2.Grapheme ?? "";
                                var display = string.IsNullOrEmpty(g) ? "(space)" : g;
                                sb.Append($"c{ccol}: '{display}' ");
                                // show codepoints
                                if (!string.IsNullOrEmpty(g))
                                {
                                    foreach (var ch in g)
                                    {
                                        sb.AppendFormat("U+{0:X4}", (int)ch);
                                        sb.Append(' ');
                                    }
                                }
                                sb.AppendLine();
                            }
                            Console.WriteLine(sb.ToString());
                        }
                    }
                }
                catch { }
            }
#endif

            var fgBrush = resolveForeground(cell);
            var runProps = getRunProps(cell.Bold, cell.Italic, fgBrush);

            if (findTypefaceForGrapheme != null)
            {
                var tf = findTypefaceForGrapheme(glyph, cell.Bold, cell.Italic);
                if (tf != null)
                {
                    runProps = new GenericTextRunProperties((Typeface)tf, fontSize, foregroundBrush: fgBrush);
                }
            }
            int widthUnits = Math.Max(1, cell.Width == 0 ? 1 : (int)cell.Width);

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

            // Pad out additional width units so the TextLayout reflects the rendered cell width.
            for (int w = 1; w < widthUnits; w++)
            {
                _rowTextBuilder.Append(' ');
                textIndex++;
            }
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
            var defaultProps = getRunProps(false, false, defaultFg);
            _styleSpanScratch.Add(new ValueSpan<TextRunProperties>(0, rowText.Length, defaultProps));
        }

        var styleOverrides = _styleSpanScratch.ToArray();
        _styleSpanScratch.Clear();
        _rowTextBuilder.Clear();

    #if DEBUG
            try
            {
                var fams = new System.Collections.Generic.List<string>();
                foreach (var s in styleOverrides)
                {
                    if (s.Value is GenericTextRunProperties gtrp)
                    {
                        fams.Add(gtrp.Typeface.FontFamily?.ToString() ?? "(unknown)");
                    }
                }
                Console.WriteLine($"[Dotty][Debug] Row {row} style families: {string.Join(", ", fams)}");
            }
            catch { }
    #endif

        cache.Layout?.Dispose();
        cache.Layout = new TextLayout(
            rowText,
            normalTypeface,
            fontSize,
            defaultFg,
            TextAlignment.Left,
            TextWrapping.NoWrap,
            textStyleOverrides: styleOverrides);

        cache.Signature = signature;
        cache.Text = rowText;
        cache.TextLength = rowText.Length;
        cache.EnsureColumnCapacity(buffer.Columns + 1);
        Array.Copy(_columnToTextIndexScratch, cache.ColumnToTextIndex, buffer.Columns + 1);

        return cache;
    }

    public void Clear()
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i]?.Dispose();
        }

        _rows = Array.Empty<RowLayoutCache>();
        _rowTextBuilder.Clear();
        _signatureBuilder.Clear();
        _styleSpanScratch.Clear();
        _columnToTextIndexScratch = Array.Empty<int>();
    }

    private void EnsureColumnIndexScratch(int size)
    {
        if (_columnToTextIndexScratch.Length < size)
        {
            Array.Resize(ref _columnToTextIndexScratch, size);
        }
    }

    private void AppendSignature(in Cell cell)
    {
        _signatureBuilder.Append(cell.Grapheme ?? " ");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Foreground?.Hex ?? "_");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Background?.Hex ?? "_");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Bold ? '1' : '0');
        _signatureBuilder.Append(cell.Italic ? '1' : '0');
        _signatureBuilder.Append(cell.Underline ? '1' : '0');
        _signatureBuilder.Append(cell.Faint ? '1' : '0');
        _signatureBuilder.Append(cell.Inverse ? '1' : '0');
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.UnderlineColor?.Hex ?? "_");
        _signatureBuilder.Append('|');
        _signatureBuilder.Append(cell.Width);
        _signatureBuilder.Append(cell.IsContinuation ? 'C' : 'B');
        _signatureBuilder.Append(';');
    }
}

internal sealed class RowLayoutCache : IDisposable
{
    public string Signature = string.Empty;
    public TextLayout? Layout;
    public string Text = string.Empty;
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
        Text = string.Empty;
    }
}
