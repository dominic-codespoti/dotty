using System;
using Avalonia;
using Avalonia.Media;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class UnderlinePainter
{
    public void DrawUnderlines(
        DrawingContext context,
        TerminalBuffer buffer,
        int row,
        double originX,
        double rowY,
        double cellWidth,
        double cellHeight,
        double renderScaling,
        Func<Cell, IBrush> resolveForeground,
        Func<Cell, IBrush> resolveUnderlineColor,
        Func<double, double, double, double, Rect> createRect)
    {
        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation || !cell.Underline)
            {
                continue;
            }

            var underlineBrush = resolveUnderlineColor(cell) ?? resolveForeground(cell);
            int widthUnits = Math.Max(1, cell.Width == 0 ? 1 : (int)cell.Width);
            DrawUnderline(context, originX + col * cellWidth, rowY, widthUnits * cellWidth, underlineBrush, cellHeight, renderScaling, createRect);
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
