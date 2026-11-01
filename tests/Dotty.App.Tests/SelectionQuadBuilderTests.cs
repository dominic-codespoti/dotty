using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Selection;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class SelectionQuadBuilderTests
{
    private static readonly SgrColorArgb SelectionColor = new(0x60123456);
    private static readonly SgrColorArgb Foreground = new(0xFFD0D0D0);
    private const byte SelectionAlpha = 0x60;

    public static TheoryData<string, SelectionMode, int, int, int, int, int> Selections => new()
    {
        // name, mode, anchor row, anchor column, active row, active column, scroll offset
        { "single cell", SelectionMode.Character, 1, 3, 1, 3, 0 },
        { "one row forward", SelectionMode.Character, 2, 1, 2, 9, 0 },
        { "one row backward", SelectionMode.Character, 2, 9, 2, 1, 0 },
        { "wrapped forward", SelectionMode.Character, 1, 7, 4, 2, 0 },
        { "wrapped backward", SelectionMode.Word, 4, 2, 1, 7, 0 },
        { "line", SelectionMode.Line, 0, 0, 5, 11, 0 },
        { "wrapped into scrollback", SelectionMode.Character, -3, 5, 2, 4, 3 },
        { "wrapped past both edges", SelectionMode.Character, -9, 4, 30, 1, 2 },
        { "columns outside the pane", SelectionMode.Character, 2, -4, 3, 40, 0 },
        { "block", SelectionMode.Block, 1, 8, 4, 3, 0 },
        { "block reversed", SelectionMode.Block, 4, 3, 1, 8, 0 },
        { "block in scrollback", SelectionMode.Block, -2, 2, 1, 6, 2 },
        { "block wider than pane", SelectionMode.Block, 0, -3, 2, 50, 0 },
        { "selection fully scrolled out", SelectionMode.Character, 3, 0, 5, 4, 4 },
        { "selection below the pane", SelectionMode.Character, 7, 0, 9, 4, 0 },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public void Apply_MatchesThePerCellScanOnRandomRows(
        string name, SelectionMode mode, int anchorRow, int anchorColumn, int activeRow, int activeColumn, int scrollOffset)
    {
        const int paneRows = 6;
        const int paneColumns = 12;
        bool block = mode == SelectionMode.Block;
        var range = block
            ? new TerminalSelectionRange(
                Math.Min(anchorRow, activeRow), Math.Min(anchorColumn, activeColumn),
                Math.Max(anchorRow, activeRow), Math.Max(anchorColumn, activeColumn))
            : TerminalSelectionRange.From(anchorRow, anchorColumn, activeRow, activeColumn);
        var random = new Random(9173 + name.Length * 101 + anchorRow * 13 + activeColumn * 7 + scrollOffset);

        for (int trial = 0; trial < 40; trial++)
        {
            int columnOffset = random.Next(0, 20);
            int rowOffset = random.Next(0, 5);
            CellInstance[] pane = RandomPaneInstances(random, paneRows, paneColumns, columnOffset, rowOffset);

            CellInstance[] expected = OldOverlay(pane, range, block, paneRows, paneColumns, scrollOffset, columnOffset, rowOffset);

            int coverageLength = SelectionQuadBuilder.CoverageLength(range, paneRows, paneColumns, scrollOffset);
            var actual = new CellInstance[pane.Length + coverageLength];
            pane.CopyTo(actual, 0);
            var coverage = new bool[coverageLength + random.Next(0, 8)];
            // Stale coverage from an earlier pane must not leak into this one.
            coverage.AsSpan().Fill(true);
            int appended = SelectionQuadBuilder.Apply(
                actual, pane.Length, range, block, paneRows, paneColumns, scrollOffset,
                columnOffset, rowOffset, SelectionColor, SelectionAlpha, Foreground, coverage);

            Assert.Equal(expected.Length, pane.Length + appended);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Apply_FillsAnEmptyWrappedSelectionRowByRow()
    {
        var range = TerminalSelectionRange.From(0, 2, 1, 1);
        var instances = new CellInstance[SelectionQuadBuilder.CoverageLength(range, 3, 4, 0)];
        var coverage = new bool[instances.Length];

        int appended = SelectionQuadBuilder.Apply(instances, 0, range, block: false, paneRows: 3, paneColumns: 4,
            scrollOffset: 0, columnOffset: 10, rowOffset: 1, SelectionColor, SelectionAlpha, Foreground, coverage);

        Assert.Equal(4, appended);
        Assert.Equal(new (int Col, int Row)[] { (12, 1), (13, 1), (10, 2), (11, 2) },
            Array.ConvertAll(instances[..appended], instance => ((int)instance.Col, (int)instance.Row)));
        Assert.All(instances[..appended], instance =>
        {
            Assert.Equal(SelectionAlpha, instance.BgA);
            Assert.Equal(SelectionColor.R, instance.BgR);
            Assert.Equal(Foreground.R, instance.FgR);
        });
    }

    [Fact]
    public void Apply_AllocatesNothing()
    {
        var range = TerminalSelectionRange.From(-5, 3, 50, 7);
        var random = new Random(7);
        CellInstance[] pane = RandomPaneInstances(random, 60, 200, 0, 0);
        int coverageLength = SelectionQuadBuilder.CoverageLength(range, 60, 200, 5);
        var instances = new CellInstance[pane.Length + coverageLength];
        var coverage = new bool[coverageLength];

        AllocationAssert.NoAllocations(() =>
        {
            pane.CopyTo(instances, 0);
            SelectionQuadBuilder.Apply(instances, pane.Length, range, block: false, 60, 200, 5, 0, 0,
                SelectionColor, SelectionAlpha, Foreground, coverage);
        });
    }

    private static CellInstance[] RandomPaneInstances(Random random, int rows, int columns, int columnOffset, int rowOffset)
    {
        var instances = new List<CellInstance>();
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int roll = random.Next(0, 10);
                if (roll < 4)
                    continue; // blank default-background cell: no instance

                int copies = roll == 9 ? 2 : 1; // a decorated cell can carry a second instance
                for (int copy = 0; copy < copies; copy++)
                {
                    instances.Add(new CellInstance
                    {
                        Col = (ushort)(column + columnOffset),
                        Row = (ushort)(row + rowOffset),
                        GlyphX = (short)random.Next(0, 500),
                        FgR = (byte)random.Next(256),
                        FgA = 255,
                        BgR = (byte)random.Next(256),
                        BgA = (byte)(random.Next(2) * 255),
                        Flags = (byte)random.Next(0, 4),
                    });
                }
            }
        }
        return instances.ToArray();
    }

    private static bool IsSelected(TerminalSelectionRange range, bool block, int row, int col) =>
        block
            ? row >= range.StartRow && row <= range.EndRow && col >= range.StartColumn && col <= range.EndColumn
            : range.Contains(row, col);

    // Oracle: the pre-W4-B6 composer algorithm (per-instance IsCellSelected, then a full scan of the pane's
    // instances for every selected cell).
    private static CellInstance[] OldOverlay(
        CellInstance[] pane, TerminalSelectionRange range, bool block, int paneRows, int paneColumns, int scrollOffset,
        int startColumnOffset, int startRowOffset)
    {
        var result = new List<CellInstance>(pane);
        for (int i = 0; i < result.Count; i++)
        {
            var instance = result[i];
            int localColumn = instance.Col - startColumnOffset;
            int viewRow = instance.Row - startRowOffset;
            int logicalRow = viewRow - scrollOffset;
            if (IsSelected(range, block, logicalRow, localColumn))
            {
                instance.BgR = SelectionColor.R;
                instance.BgG = SelectionColor.G;
                instance.BgB = SelectionColor.B;
                instance.BgA = SelectionAlpha;
                result[i] = instance;
            }
        }

        int firstLogicalRow = Math.Max(range.StartRow, -scrollOffset);
        int lastLogicalRow = Math.Min(range.EndRow, paneRows - 1 - scrollOffset);
        for (int logicalRow = firstLogicalRow; logicalRow <= lastLogicalRow; logicalRow++)
        {
            int viewRow = logicalRow + scrollOffset;
            int minColumn = Math.Max(0, logicalRow == range.StartRow ? range.StartColumn : 0);
            int maxColumn = Math.Min(paneColumns - 1, logicalRow == range.EndRow ? range.EndColumn : paneColumns - 1);
            for (int column = minColumn; column <= maxColumn; column++)
            {
                if (!IsSelected(range, block, logicalRow, column))
                    continue;

                int targetColumn = column + startColumnOffset;
                int targetRow = viewRow + startRowOffset;
                bool covered = false;
                foreach (var existing in result)
                {
                    if (existing.Col == targetColumn && existing.Row == targetRow)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    result.Add(new CellInstance
                    {
                        Col = (ushort)targetColumn,
                        Row = (ushort)targetRow,
                        FgR = Foreground.R,
                        FgG = Foreground.G,
                        FgB = Foreground.B,
                        FgA = 255,
                        BgR = SelectionColor.R,
                        BgG = SelectionColor.G,
                        BgB = SelectionColor.B,
                        BgA = SelectionAlpha,
                        Flags = 0
                    });
                }
            }
        }
        return result.ToArray();
    }
}
