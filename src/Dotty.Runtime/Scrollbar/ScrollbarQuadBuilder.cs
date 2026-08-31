using System;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;

namespace Dotty.Runtime.Scrollbar;

/// <summary>
/// Builds GPU <see cref="CellInstance"/> quads for rendering an in-window scrollbar indicator
/// (track and thumb) reflecting total scrollback depth, viewport size, and current scroll offset.
/// </summary>
public static class ScrollbarQuadBuilder
{
    /// <summary>
    /// Builds cell instances for the pane scrollbar and writes them into <paramref name="destination"/>.
    /// Returns the number of cell instances written.
    /// </summary>
    public static int Build(
        int startColOffset,
        int startRowOffset,
        int paneCols,
        int paneRows,
        int scrollbackCount,
        int scrollOffset,
        IColorScheme theme,
        Span<CellInstance> destination,
        bool isHoveredOrDragging = false)
    {
        if (scrollbackCount <= 0 || paneCols <= 0 || paneRows <= 0 || destination.IsEmpty)
        {
            return 0;
        }

        int written = 0;
        int scrollCol = startColOffset + paneCols - 1;

        int totalRows = scrollbackCount + paneRows;
        // Calculate thumb size in rows (at least 1 row, proportional to viewport / total)
        int thumbRows = Math.Clamp((int)Math.Round((float)paneRows * paneRows / totalRows), 1, paneRows);
        int availableTrackRows = Math.Max(0, paneRows - thumbRows);

        // Progress from top (0.0 = oldest scrollback, 1.0 = newest/bottom active prompt)
        float progress = 1.0f - Math.Clamp((float)scrollOffset / scrollbackCount, 0.0f, 1.0f);
        int thumbStartRow = (int)Math.Round(progress * availableTrackRows);
        int thumbEndRow = Math.Min(paneRows - 1, thumbStartRow + thumbRows - 1);

        uint fgColor = theme.Foreground != 0 ? theme.Foreground : 0xFFD4D4D4;
        uint bgColor = theme.Background != 0 ? theme.Background : 0xFF1E1E1E;
        uint accentColor = theme.AnsiBlue != 0 ? theme.AnsiBlue : 0xFF3B8EEA;

        // When hovered or dragged, draw sleek translucent track groove underneath
        if (isHoveredOrDragging)
        {
            byte trackR = (byte)((bgColor >> 16) & 0xFF);
            byte trackG = (byte)((bgColor >> 8) & 0xFF);
            byte trackB = (byte)(bgColor & 0xFF);
            byte trackA = 50;

            for (int r = 0; r < paneRows; r++)
            {
                if (written >= destination.Length) break;
                destination[written++] = new CellInstance
                {
                    Col = (ushort)scrollCol,
                    Row = (ushort)(startRowOffset + r),
                    BgR = trackR,
                    BgG = trackG,
                    BgB = trackB,
                    BgA = trackA,
                    Flags = 0
                };
            }
        }

        // Thumb color: accent/bright when hovered or dragged
        uint thumbColor = isHoveredOrDragging ? accentColor : fgColor;
        byte thumbR = (byte)((thumbColor >> 16) & 0xFF);
        byte thumbG = (byte)((thumbColor >> 8) & 0xFF);
        byte thumbB = (byte)(thumbColor & 0xFF);
        byte thumbAlpha = (byte)(isHoveredOrDragging ? 230 : (scrollOffset > 0 ? 170 : 80));

        for (int r = thumbStartRow; r <= thumbEndRow; r++)
        {
            if (written >= destination.Length) break;
            destination[written++] = new CellInstance
            {
                Col = (ushort)scrollCol,
                Row = (ushort)(startRowOffset + r),
                BgR = thumbR,
                BgG = thumbG,
                BgB = thumbB,
                BgA = thumbAlpha,
                Flags = 0
            };
        }

        return written;
    }
}
