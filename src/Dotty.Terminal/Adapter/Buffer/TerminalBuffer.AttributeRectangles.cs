using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    internal void ChangeRectangleAttributes(int top, int left, int bottom, int right, ReadOnlySpan<int> parameters, bool reverse, bool rectangleExtent)
    {
        GetRectangleBounds(out int minRow, out int minColumn, out int maxRow, out int maxColumn);
        if (!TryRectangleAxis(top, bottom, minRow, maxRow, out int firstRow, out int lastRow)
            || !TryRectangleAxis(left, right, minColumn, maxColumn, out int firstColumn, out int lastColumn))
            return;

        for (int row = firstRow; row <= lastRow; row++)
        {
            int start = rectangleExtent || row == firstRow ? firstColumn : minColumn;
            int end = rectangleExtent || row == lastRow ? lastColumn : maxColumn;
            for (int column = start; column <= end; column++)
            {
                CellHot cell = ActiveBuffer.GetCell(row, column);
                if (!rectangleExtent && cell.IsEmpty)
                    continue;
                CellAttributes attributes = StyleSet.GetStyle(cell.StyleId);
                CellAttributes updated = reverse
                    ? ReverseAttributes(attributes, parameters)
                    : ApplyRectangleAttributes(attributes, parameters);
                ushort styleId = StyleSet.GetOrCreateId(in updated);
                ActiveBuffer.SetRectangleCellStyle(row, column, styleId);
            }
            MarkRowDirty(row);
        }
    }

    private static CellAttributes ApplyRectangleAttributes(CellAttributes attributes, ReadOnlySpan<int> parameters)
    {
        foreach (int parameter in parameters)
        {
            switch (parameter)
            {
                case 0:
                    attributes.Bold = false;
                    attributes.UnderlineStyle = UnderlineStyle.None;
                    attributes.SlowBlink = false;
                    attributes.Inverse = false;
                    break;
                case 1: attributes.Bold = true; break;
                case 4: attributes.UnderlineStyle = UnderlineStyle.Single; break;
                case 5: attributes.SlowBlink = true; break;
                case 7: attributes.Inverse = true; break;
                case 8: attributes.Invisible = true; break;
                case 22: attributes.Bold = false; break;
                case 24: attributes.UnderlineStyle = UnderlineStyle.None; break;
                case 25: attributes.SlowBlink = false; break;
                case 27: attributes.Inverse = false; break;
                case 28: attributes.Invisible = false; break;
            }
        }
        return attributes;
    }

    private static CellAttributes ReverseAttributes(CellAttributes attributes, ReadOnlySpan<int> parameters)
    {
        foreach (int parameter in parameters)
        {
            switch (parameter)
            {
                case 0:
                    attributes.Bold = !attributes.Bold;
                    attributes.UnderlineStyle = attributes.UnderlineStyle == UnderlineStyle.None ? UnderlineStyle.Single : UnderlineStyle.None;
                    attributes.SlowBlink = !attributes.SlowBlink;
                    attributes.Inverse = !attributes.Inverse;
                    break;
                case 1: attributes.Bold = !attributes.Bold; break;
                case 4: attributes.UnderlineStyle = attributes.UnderlineStyle == UnderlineStyle.None ? UnderlineStyle.Single : UnderlineStyle.None; break;
                case 5: attributes.SlowBlink = !attributes.SlowBlink; break;
                case 7: attributes.Inverse = !attributes.Inverse; break;
                case 8: attributes.Invisible = !attributes.Invisible; break;
            }
        }
        return attributes;
    }
}
