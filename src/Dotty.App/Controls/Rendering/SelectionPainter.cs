using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Dotty.App.Controls.Selection;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class SelectionPainter
{
    public void DrawSelectedTextOverlay(
        DrawingContext context,
        TerminalBuffer buffer,
        SelectionModel selection,
        RowLayoutCache rowCache,
        int row,
        double originX,
        double textY,
        Func<bool, bool, TextRunProperties> getSelectionRunProps,
        Func<string, bool, bool, Typeface?>? findTypefaceForGrapheme,
        double fontSize,
        IBrush selectionForeground)
    {
        var layout = rowCache.Layout;
        if (layout is null || !selection.HasSelection)
        {
            return;
        }

        int colIdx = 0;
        while (colIdx < buffer.Columns)
        {
            if (!selection.IsCellSelected(row, colIdx))
            {
                colIdx++;
                continue;
            }

            int startCol = colIdx;
            while (colIdx < buffer.Columns && selection.IsCellSelected(row, colIdx)) colIdx++;
            int endCol = colIdx - 1;

            int startIndex = rowCache.ColumnToTextIndex[startCol];
            int endIndex = rowCache.ColumnToTextIndex[Math.Min(endCol + 1, rowCache.ColumnToTextIndex.Length - 1)];
            int len = Math.Max(0, endIndex - startIndex);
            if (len <= 0)
            {
                continue;
            }

            if (string.IsNullOrEmpty(rowCache.Text) || startIndex + len > rowCache.Text.Length)
            {
                continue;
            }

            var substr = rowCache.Text.Substring(startIndex, len);
            var selectionLayout = BuildSelectionLayout(substr, buffer, row, startCol, endCol, getSelectionRunProps, findTypefaceForGrapheme, fontSize, selectionForeground);
            var hitRects = layout.HitTestTextRange(startIndex, len);
            foreach (var hr in hitRects)
            {
                selectionLayout.Draw(context, new Point(originX + hr.X, textY));
            }
        }
    }

    private static TextLayout BuildSelectionLayout(
        string text,
        TerminalBuffer buffer,
        int row,
        int startCol,
        int endCol,
        Func<bool, bool, TextRunProperties> getSelectionRunProps,
        Func<string, bool, bool, Typeface?>? findTypefaceForGrapheme,
        double fontSize,
        IBrush selectionForeground)
    {
        var spans = new List<ValueSpan<TextRunProperties>>();
        int textIndex = 0;
        TextRunProperties? currentProps = null;
        int currentSpanStart = 0;

        for (int col = startCol; col <= endCol; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation)
            {
                continue;
            }

            var glyph = string.IsNullOrEmpty(cell.Grapheme) ? " " : cell.Grapheme;
            var runProps = getSelectionRunProps(cell.Bold, cell.Italic);
            if (findTypefaceForGrapheme != null)
            {
                var tf = findTypefaceForGrapheme(glyph, cell.Bold, cell.Italic);
                if (tf != null)
                {
                    runProps = new GenericTextRunProperties((Typeface)tf, fontSize, foregroundBrush: selectionForeground);
                }
            }
            int widthUnits = Math.Max(1, cell.Width == 0 ? 1 : (int)cell.Width);

            if (!ReferenceEquals(runProps, currentProps))
            {
                if (currentProps is { } prev && textIndex > currentSpanStart)
                {
                    spans.Add(new ValueSpan<TextRunProperties>(currentSpanStart, textIndex - currentSpanStart, prev));
                }

                currentProps = runProps;
                currentSpanStart = textIndex;
            }

            textIndex += glyph.Length;

            for (int w = 1; w < widthUnits; w++)
            {
                textIndex++;
            }
        }

        if (currentProps is { } last && textIndex > currentSpanStart)
        {
            spans.Add(new ValueSpan<TextRunProperties>(currentSpanStart, textIndex - currentSpanStart, last));
        }

        var styleOverrides = spans.Count == 0
            ? new[] { new ValueSpan<TextRunProperties>(0, Math.Max(1, text.Length), getSelectionRunProps(false, false)) }
            : spans.ToArray();

        var baseTypeface = styleOverrides.Length > 0
            ? ((GenericTextRunProperties)styleOverrides[0].Value).Typeface
            : ((GenericTextRunProperties)getSelectionRunProps(false, false)).Typeface;

        return new TextLayout(
            text.Length == 0 ? " " : text,
            baseTypeface,
            fontSize,
            selectionForeground,
            TextAlignment.Left,
            TextWrapping.NoWrap,
            TextTrimming.None,
            textStyleOverrides: styleOverrides);
    }
}
