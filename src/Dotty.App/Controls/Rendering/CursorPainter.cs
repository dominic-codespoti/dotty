using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class CursorPainter
{
    public void DrawCursor(
        DrawingContext context,
        TerminalBuffer buffer,
        RowLayoutCache rowCache,
        int cursorRow,
        int cursorCol,
        double originX,
        double rowY,
        double cellWidth,
        double cellHeight,
        double renderScaling,
        TerminalCursorShape shape,
        Func<Cell, (IBrush foreground, IBrush background)> resolveBrushes,
        Func<double, double, double, double, Rect> createRect)
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

        double cursorX = (caretRect.Width > 0 ? caretRect.X : cursorCol * cellWidth) + originX;
        double cursorWidth = caretRect.Width > 0 ? caretRect.Width : cellWidth;

        var cell = buffer.GetCell(cursorRow, cursorCol);
        var (cursorBrush, cursorBg) = resolveBrushes(cell);

        switch (shape)
        {
            case TerminalCursorShape.Block:
                var rect = createRect(cursorX, rowY, cursorWidth, cellHeight);
                context.FillRectangle(cursorBrush, rect);
                break;
            case TerminalCursorShape.Beam:
                double beamWidth = Math.Max(1.0, renderScaling);
                var beamRect = createRect(cursorX, rowY, beamWidth, cellHeight);
                context.FillRectangle(cursorBrush, beamRect);
                break;
            case TerminalCursorShape.Underline:
                DrawUnderline(context, cursorX, rowY, cursorWidth, cursorBrush, cellHeight, renderScaling, createRect);
                break;
            default:
                var fallbackRect = createRect(cursorX, rowY, cursorWidth, cellHeight);
                context.FillRectangle(cursorBrush, fallbackRect);
                break;
        }
    }

    private static void DrawUnderline(
        DrawingContext context,
        double x,
        double rowY,
        double width,
        IBrush brush,
        double cellHeight,
        double renderScaling,
        Func<double, double, double, double, Rect> createRect)
    {
        double thickness = Math.Max(1.0, renderScaling);
        double y = rowY + cellHeight - thickness * 1.2;
        var rect = createRect(x, y, width, thickness);
        context.FillRectangle(brush, rect);
    }
}
