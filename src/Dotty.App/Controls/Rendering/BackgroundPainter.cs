using System;
using Avalonia;
using Avalonia.Media;
using Dotty.App.Controls.Selection;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class BackgroundPainter
{
    public void PaintRowBackgrounds(
        DrawingContext context,
        TerminalBuffer buffer,
        int row,
        double originX,
        double rowY,
        double cellWidth,
        double cellHeight,
        SelectionModel selection,
        IBrush selectionBackground,
        Func<Cell, IBrush> resolveBackground,
        Func<double, double, double, double, Rect> createRect)
    {
        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation)
            {
                continue;
            }

            var bgBrush = resolveBackground(cell);
            int widthUnits = Math.Max(1, cell.Width == 0 ? 1 : (int)cell.Width);
            var rect = createRect(originX + col * cellWidth, rowY, widthUnits * cellWidth, cellHeight);
            if (selection.IsCellSelected(row, col))
            {
                context.FillRectangle(selectionBackground, rect);
            }
            else
            {
                context.FillRectangle(bgBrush, rect);
            }
        }
    }
}
