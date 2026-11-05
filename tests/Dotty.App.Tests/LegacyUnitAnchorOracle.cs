using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

/// <summary>
/// Test-only copy of how the reflow located a cursor/mark anchor in the source layout before the anchor-only rewrite
/// (plan 08, WI-R2): <c>BuildSourceLayoutCore</c> recorded one <c>SourceUnit (column, width, line offset)</c> per
/// non-continuation cell of every source row, and <c>GetReflowAnchor</c> read those lists. The production code now
/// recomputes the unit offset by rescanning the source row; this oracle keeps the table-based behavior so the two can be
/// compared for every (row, column, wrap-pending) of a real screen. Frozen like <see cref="LegacyDenseReflowMapping"/>;
/// it reads cells through the public row accessors only.
/// </summary>
internal sealed class LegacyUnitAnchorOracle
{
    private readonly record struct Unit(int Column, int Width, int Offset);

    private sealed class Row
    {
        internal int LogicalLine = -1;
        internal int UnitStart;
        internal readonly List<Unit> Units = new();
    }

    private readonly Screen _screen;
    private readonly int _scrollbackRows;
    private readonly List<Row> _rows = new();

    private LegacyUnitAnchorOracle(Screen screen, int scrollbackRows)
    {
        _screen = screen;
        _scrollbackRows = scrollbackRows;
    }

    /// <summary>Builds the legacy per-row unit table for <paramref name="screen"/> with <paramref name="scrollbackRows"/> history rows.</summary>
    internal static LegacyUnitAnchorOracle Build(Screen screen, int scrollbackRows)
    {
        int capacity = screen.ScrollbackCapacity;
        int retained = Math.Clamp(scrollbackRows < 0 ? capacity : scrollbackRows, 0, capacity);
        var oracle = new LegacyUnitAnchorOracle(screen, retained);

        var lineCellCounts = new List<int>();
        int currentLine = -1;
        for (int index = 0; index < retained + screen.Rows; index++)
        {
            int logicalRow = index - retained;
            int physicalRow = screen.GetPhysicalRow(logicalRow);
            var row = new Row();
            oracle._rows.Add(row);

            if (!screen.RowContinuesPrevious[physicalRow])
            {
                currentLine = lineCellCounts.Count;
                lineCellCounts.Add(0);
            }
            else if (currentLine < 0)
            {
                continue; // an orphan continuation row has no line
            }

            row.LogicalLine = currentLine;
            row.UnitStart = lineCellCounts[currentLine];
            int rowEnd = screen.RowEndCol[physicalRow];
            if (rowEnd < 0)
                rowEnd = FallbackRowEnd(screen, logicalRow);
            if (rowEnd < 0)
                continue;

            rowEnd = Math.Min(rowEnd, screen.Columns - 1);
            for (int column = 0; column <= rowEnd; column++)
            {
                CellHot hot = screen.GetCellRef(logicalRow, column);
                if (hot.IsContinuation)
                    continue;

                int width = 1;
                if (hot.Width > 1
                    && column + 1 <= rowEnd
                    && column + 1 < screen.Columns
                    && screen.GetCellRef(logicalRow, column + 1).IsContinuation)
                {
                    width = 2;
                }

                int offset = lineCellCounts[currentLine];
                lineCellCounts[currentLine] = offset + 1;
                row.Units.Add(new Unit(column, width, offset));
            }
        }

        return oracle;
    }

    /// <summary>The anchor the pre-rewrite <c>GetReflowAnchor</c> produced for a screen position.</summary>
    internal ReflowCursorAnchor Anchor(int logicalRow, int column, bool wrapPending)
    {
        int sb = _scrollbackRows;
        int rowIndex = sb + Math.Clamp(logicalRow, -sb, _screen.Rows - 1);
        if (rowIndex < 0 || rowIndex >= _rows.Count)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        var row = _rows[rowIndex];
        if (row.LogicalLine < 0)
            return new ReflowCursorAnchor(0, 0, wrapPending);

        int col = Math.Clamp(column, 0, _screen.Columns - 1);
        if (row.Units.Count == 0)
            return new ReflowCursorAnchor(row.LogicalLine, col, wrapPending);

        int offset = row.UnitStart;
        foreach (var unit in row.Units)
        {
            if (col <= unit.Column)
                break;
            if (col < unit.Column + unit.Width)
            {
                offset = unit.Offset;
                break;
            }
            offset = unit.Offset + 1;
        }

        return new ReflowCursorAnchor(row.LogicalLine, offset, wrapPending);
    }

    private static int FallbackRowEnd(Screen screen, int logicalRow)
    {
        for (int column = screen.Columns - 1; column >= 0; column--)
        {
            CellHot hot = screen.GetCellRef(logicalRow, column);
            ColdCell cold = screen.GetColdCellRef(logicalRow, column);
            if (hot.Rune != 0 || hot.PackedFlags != 0 || hot.StyleId != 0
                || cold.HyperlinkId != 0 || cold.GraphemeIndex != -1)
            {
                return column;
            }
        }

        return -1;
    }
}
