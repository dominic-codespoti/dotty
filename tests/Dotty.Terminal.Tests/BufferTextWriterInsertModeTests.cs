using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class BufferTextWriterInsertModeTests
{
    private static TerminalAdapter Create(int rows = 2, int columns = 8)
    {
        var adapter = new TerminalAdapter(rows, columns);
        adapter.Buffer.InsertMode = true;
        return adapter;
    }

    private static string Row(TerminalAdapter adapter, int row = 0)
    {
        var result = new StringBuilder(adapter.Buffer.Columns);
        for (int col = 0; col < adapter.Buffer.Columns; col++)
        {
            CellHot cell = adapter.Buffer.GetCell(row, col);
            result.Append(cell.IsContinuation ? '~' : cell.IsEmpty ? ' ' : (char)cell.Rune);
        }
        return result.ToString();
    }

    [Fact]
    public void AsciiByteRunAndFragmentedCharWritesHaveTheSameInsertion()
    {
        var batch = Create();
        batch.OnPrint("abcdefgh");
        batch.Buffer.SetCursor(0, 2);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = batch };
        parser.Feed(Encoding.ASCII.GetBytes("XY"));

        var fragmented = Create();
        fragmented.OnPrint("abcdefgh");
        fragmented.Buffer.SetCursor(0, 2);
        fragmented.OnPrint("X");
        fragmented.OnPrint("Y");

        Assert.Equal("abXYcdef", Row(batch));
        Assert.Equal(Row(batch), Row(fragmented));
    }

    [Fact]
    public void InsertRespectsRightMarginAndDoesNotTouchCellsOutsideIt()
    {
        var adapter = Create(columns: 8);
        adapter.Buffer.InsertMode = false;
        adapter.OnPrint("abcdefgh");
        adapter.Buffer.SetLeftRightMarginMode(true);
        adapter.Buffer.SetHorizontalMargins(3, 6);
        adapter.Buffer.InsertMode = true;
        adapter.Buffer.MoveCursorToScreenColumn(4);
        adapter.Buffer.SetAutoWrap(false);
        adapter.OnPrint("XY");
        Assert.Equal("abcdXYgh", Row(adapter));
        Assert.Equal(5, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void InsertMovesProtectedAndColdCellMetadataWithItsGlyph()
    {
        var adapter = Create(columns: 8);
        adapter.Buffer.InsertMode = false;
        adapter.OnPrint("abcd");
        adapter.Buffer.SetCursor(0, 1);
        adapter.Buffer.SetIsoProtection(true);
        adapter.OnPrint("P");
        adapter.Buffer.SetIsoProtection(false);
        adapter.Buffer.ActiveBuffer.SetColdHyperlink(0, 1, 42);
        adapter.Buffer.SetCursor(0, 0);
        adapter.Buffer.InsertMode = true;
        adapter.OnPrint("X");

        Assert.Equal('P', (char)adapter.Buffer.GetCell(0, 2).Rune);
        Assert.True(adapter.Buffer.GetCell(0, 2).IsProtected);
        Assert.Equal((ushort)42, adapter.Buffer.GetColdCell(0, 2).HyperlinkId);
    }

    [Fact]
    public void WideGraphemeInsertsByCellWidthAndCombiningWidthGrowthInsertsOnlyDelta()
    {
        var adapter = Create(columns: 8);
        adapter.Buffer.InsertMode = false;
        adapter.OnPrint("abcd");
        adapter.Buffer.SetCursor(0, 1);
        adapter.Buffer.InsertMode = true;
        adapter.OnPrint("界");
        Assert.Equal("a界~bcd  ", Row(adapter));

        var growth = Create(columns: 8);
        growth.Buffer.InsertMode = false;
        growth.OnPrint("\u2764x");
        growth.Buffer.SetCursor(0, 1);
        growth.Buffer.InsertMode = true;
        growth.OnPrint("\uFE0F");
        Assert.Equal(2, growth.Buffer.GetCell(0, 0).Width);
        Assert.True(growth.Buffer.GetCell(0, 1).IsContinuation);
        Assert.Equal('x', (char)growth.Buffer.GetCell(0, 2).Rune);
        Assert.Equal(2, growth.Buffer.CursorCol);
    }

    [Fact]
    public void InsertDoesNotWrapWhenAutowrapIsDisabled()
    {
        var adapter = Create(rows: 2, columns: 4);
        adapter.Buffer.InsertMode = false;
        adapter.OnPrint("abcd");
        adapter.Buffer.InsertMode = true;
        adapter.Buffer.SetAutoWrap(false);
        adapter.Buffer.SetCursor(0, 3);
        adapter.OnPrint("Z");

        Assert.Equal("abcZ", Row(adapter));
        Assert.Equal(0, adapter.Buffer.CursorRow);
        Assert.Equal(3, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void RepeatCharacterUsesInsertModeBulkPath()
    {
        var adapter = Create(columns: 8);
        adapter.Buffer.InsertMode = false;
        adapter.OnPrint("abcd");
        adapter.Buffer.SetCursor(0, 1);
        adapter.Buffer.InsertMode = true;

        adapter.OnRepeatCharacter(2);

        Assert.Equal("addbcd  ", Row(adapter));
    }
}
