namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    public void CopyRectangle(int top, int left, int bottom, int right, int destinationTop, int destinationLeft)
    {
        GetRectangleBounds(out int minRow, out int minColumn, out int maxRow, out int maxColumn);
        if (!TryRectangleAxis(top, bottom, minRow, maxRow, out int sourceTop, out int sourceBottom)
            || !TryRectangleAxis(left, right, minColumn, maxColumn, out int sourceLeft, out int sourceRight))
            return;

        int destTop = MapRectangleCoordinate(destinationTop, minRow, maxRow);
        int destLeft = MapRectangleCoordinate(destinationLeft, minColumn, maxColumn);
        int height = Math.Min(sourceBottom - sourceTop + 1, maxRow - destTop + 1);
        int width = Math.Min(sourceRight - sourceLeft + 1, maxColumn - destLeft + 1);
        if (height <= 0 || width <= 0) return;

        ActiveBuffer.CopyRectangle(sourceTop, sourceLeft, destTop, destLeft, height, width);
        MarkRowRangeDirty(destTop, height);
    }

    public void FillRectangle(uint rune, int top, int left, int bottom, int right, in CellAttributes attributes)
    {
        GetRectangleBounds(out int minRow, out int minColumn, out int maxRow, out int maxColumn);
        if (!TryRectangleAxis(top, bottom, minRow, maxRow, out int rectTop, out int rectBottom)
            || !TryRectangleAxis(left, right, minColumn, maxColumn, out int rectLeft, out int rectRight))
            return;

        var sgr = attributes;
        sgr.HyperlinkId = 0;
        ushort styleId = StyleSet.GetOrCreateId(in sgr);
        var cell = new CellHot { Rune = rune, StyleId = styleId, IsProtected = _protectionActive };
        ActiveBuffer.FillRectangle(rectTop, rectLeft, rectBottom, rectRight, in cell);
        MarkRowRangeDirty(rectTop, rectBottom - rectTop + 1);
    }

    public void EraseRectangle(int top, int left, int bottom, int right, bool selective)
    {
        GetRectangleBounds(out int minRow, out int minColumn, out int maxRow, out int maxColumn);
        if (!TryRectangleAxis(top, bottom, minRow, maxRow, out int rectTop, out int rectBottom)
            || !TryRectangleAxis(left, right, minColumn, maxColumn, out int rectLeft, out int rectRight))
            return;

        if (selective)
            ActiveBuffer.WipeRectangle(rectTop, rectLeft, rectBottom, rectRight, respectProtection: !RespectIsoProtection);
        else
        {
            var blank = new CellHot { Rune = 32 };
            ActiveBuffer.FillRectangle(rectTop, rectLeft, rectBottom, rectRight, in blank);
        }
        MarkRowRangeDirty(rectTop, rectBottom - rectTop + 1);
    }

    private void GetRectangleBounds(out int minRow, out int minColumn, out int maxRow, out int maxColumn)
    {
        if (!_originMode)
        {
            minRow = minColumn = 0;
            maxRow = Rows - 1;
            maxColumn = Columns - 1;
            return;
        }

        minRow = _scrollTop;
        maxRow = _scrollBottom;
        minColumn = _leftRightMarginMode ? _leftMargin : 0;
        maxColumn = _leftRightMarginMode ? _rightMargin : Columns - 1;
    }

    private static bool TryRectangleAxis(int first, int last, int minimum, int maximum, out int clippedFirst, out int clippedLast)
    {
        clippedFirst = MapRectangleCoordinate(first, minimum, maximum);
        clippedLast = last == 0 ? maximum : MapRectangleCoordinate(last, minimum, maximum);
        return clippedFirst <= clippedLast;
    }

    private static int MapRectangleCoordinate(int coordinate, int minimum, int maximum)
    {
        long mapped = (coordinate == 0 ? 1L : coordinate) + minimum - 1L;
        if (mapped < minimum) return minimum;
        if (mapped > maximum) return maximum;
        return (int)mapped;
    }
}
