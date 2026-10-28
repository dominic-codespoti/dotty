using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Unicode-placeholder cells (U+10EEEE + row/column/msb diacritics) as stored by the buffer writer: the
/// cluster becomes one narrow cell whose triple lives in <see cref="ColdCell.GraphemeIndex"/> as a negative code.
/// </summary>
public sealed class PlaceholderWriterTests
{
    private const string P = "\U0010EEEE";
    private const string D0 = "\u0305";   // index 0
    private const string D1 = "\u030D";   // index 1
    private const string D2 = "\u030E";   // index 2
    private const string D17 = "\u0363";  // index 17
    private const ushort Absent = PlaceholderIndices.Absent;

    private static TerminalBuffer NewBuffer(int rows = 2, int cols = 16) => new(rows, cols);

    private static void Write(TerminalBuffer buffer, string text) => buffer.WriteText(text.AsSpan(), CellAttributes.Default);

    private static PlaceholderIndices Triple(TerminalBuffer buffer, int row, int col)
    {
        short code = buffer.GetColdCell(row, col).GraphemeIndex;
        Assert.True(code <= -2, $"cell ({row},{col}) has grapheme index {code}, expected a placeholder code");
        Assert.True(PlaceholderTable.TryGet(code, out var indices));
        return indices;
    }

    private static void AssertNoPlaceholderCode(TerminalBuffer buffer, int row, int col) =>
        Assert.Equal((short)-1, buffer.GetColdCell(row, col).GraphemeIndex);

    [Fact]
    public void SpecExampleRowZeroColumnZero()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0 + D0);

        CellHot cell = buffer.GetCell(0, 0);
        Assert.Equal(0x10EEEEu, cell.Rune);
        Assert.Equal(1, cell.Width);
        Assert.False(cell.HasGrapheme);
        Assert.Equal(new PlaceholderIndices(0, 0, Absent), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void RowAndColumnComeFromTheFirstTwoDiacritics()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + D2);

        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(buffer, 0, 0));
    }

    [Fact]
    public void ThirdDiacriticIsTheHighByte()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0 + D1 + D17);

        Assert.Equal(new PlaceholderIndices(0, 1, 17), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void FourthAndLaterDiacriticsAreDroppedWithoutConsumingACell()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0 + D1 + D2 + D17 + D17);

        Assert.Equal(new PlaceholderIndices(0, 1, 2), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Equal(0u, buffer.GetCell(0, 1).Rune);
    }

    [Fact]
    public void RowOnlyPlaceholderInheritsTheRest()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1);

        Assert.Equal(new PlaceholderIndices(1, Absent, Absent), Triple(buffer, 0, 0));
    }

    [Fact]
    public void PlaceholderWithoutDiacriticsStoresNoCodeButKeepsTheRune()
    {
        var buffer = NewBuffer();
        Write(buffer, P);

        CellHot cell = buffer.GetCell(0, 0);
        Assert.Equal(0x10EEEEu, cell.Rune);
        Assert.Equal(1, cell.Width);
        Assert.False(cell.HasGrapheme);
        AssertNoPlaceholderCode(buffer, 0, 0);
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void PlaceholderCodeIsNeverTreatedAsAGraphemeStoreIndex()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0 + D1 + D2);

        CellHot cell = buffer.GetCell(0, 0);
        ColdCell cold = buffer.GetColdCell(0, 0);
        Assert.True(cold.GraphemeIndex <= -2);
        Assert.Equal(P, GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
    }

    [Fact]
    public void NonDiacriticMarksAreDroppedAndTheCellStaysNarrow()
    {
        var buffer = NewBuffer();
        Write(buffer, P + "\u0301"); // U+0301 is not in the table

        Assert.Equal(0x10EEEEu, buffer.GetCell(0, 0).Rune);
        AssertNoPlaceholderCode(buffer, 0, 0);
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void NonDiacriticBetweenDiacriticsIsSkipped()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + "\u0301" + D2);

        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(buffer, 0, 0));
    }

    [Fact]
    public void AdjacentPlaceholdersKeepIndependentCodes()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0 + D0 + P + D0 + D1 + P + D1 + D2 + D17 + P);

        Assert.Equal(new PlaceholderIndices(0, 0, Absent), Triple(buffer, 0, 0));
        Assert.Equal(new PlaceholderIndices(0, 1, Absent), Triple(buffer, 0, 1));
        Assert.Equal(new PlaceholderIndices(1, 2, 17), Triple(buffer, 0, 2));
        AssertNoPlaceholderCode(buffer, 0, 3);
        Assert.Equal(4, buffer.CursorCol);
    }

    [Fact]
    public void MarksArrivingInLaterWritesCompleteTheTriple()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0);
        Assert.Equal(new PlaceholderIndices(0, Absent, Absent), Triple(buffer, 0, 0));

        Write(buffer, D1);
        Assert.Equal(new PlaceholderIndices(0, 1, Absent), Triple(buffer, 0, 0));

        Write(buffer, D17);
        Assert.Equal(new PlaceholderIndices(0, 1, 17), Triple(buffer, 0, 0));

        Write(buffer, D2); // surplus
        Assert.Equal(new PlaceholderIndices(0, 1, 17), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
    }

    [Fact]
    public void MarksAfterABarePlaceholderInALaterWriteAreAttached()
    {
        var buffer = NewBuffer();
        Write(buffer, P);
        Write(buffer, D2 + D1);

        Assert.Equal(new PlaceholderIndices(2, 1, Absent), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void LateNonDiacriticMarkIsConsumedAndDropped()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D0);
        Write(buffer, "\u0301");

        Assert.Equal(new PlaceholderIndices(0, Absent, Absent), Triple(buffer, 0, 0));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Equal(0u, buffer.GetCell(0, 1).Rune);
    }

    [Fact]
    public void LateMarkAttachesToThePlaceholderNotToANeighbour()
    {
        var buffer = NewBuffer();
        Write(buffer, "a" + P);
        Write(buffer, D1);
        Write(buffer, "b");

        Assert.Equal("a", GraphemeHelper.Resolve(buffer.GetCell(0, 0).Rune, buffer.GetColdCell(0, 0).GraphemeIndex));
        AssertNoPlaceholderCode(buffer, 0, 0);
        Assert.Equal(new PlaceholderIndices(1, Absent, Absent), Triple(buffer, 0, 1));
        Assert.Equal((uint)'b', buffer.GetCell(0, 2).Rune);
    }

    [Fact]
    public void WideNeighboursDoNotShareOrLoseTheCode()
    {
        var buffer = NewBuffer();
        Write(buffer, "界" + P + D1 + D2 + "界");

        Assert.Equal(2, buffer.GetCell(0, 0).Width);
        Assert.True(buffer.GetCell(0, 1).IsContinuation);
        Assert.Equal(0x10EEEEu, buffer.GetCell(0, 2).Rune);
        Assert.Equal(1, buffer.GetCell(0, 2).Width);
        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(buffer, 0, 2));
        Assert.Equal(2, buffer.GetCell(0, 3).Width);
        Assert.True(buffer.GetCell(0, 4).IsContinuation);
        AssertNoPlaceholderCode(buffer, 0, 1);
        AssertNoPlaceholderCode(buffer, 0, 3);
        AssertNoPlaceholderCode(buffer, 0, 4);
        Assert.Equal(5, buffer.CursorCol);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void PlaceholderAtLastColumnWrapsLikeAnyNarrowCell()
    {
        var buffer = NewBuffer(rows: 2, cols: 3);
        Write(buffer, "ab" + P + D1 + D2 + P + D0 + D0);

        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(buffer, 0, 2));
        Assert.Equal(new PlaceholderIndices(0, 0, Absent), Triple(buffer, 1, 0));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void OverwritingAPlaceholderWithAsciiClearsItsCode()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + D2 + P + D0 + D0);
        buffer.SetCursor(0, 0);
        Write(buffer, "xy");

        Assert.Equal("xy", buffer.GetRowText(0).Substring(0, 2));
        AssertNoPlaceholderCode(buffer, 0, 0);
        AssertNoPlaceholderCode(buffer, 0, 1);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void OverwritingAPlaceholderWithTabSpacesClearsItsCode()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + D2);
        buffer.SetCursor(0, 0);
        Write(buffer, "\t");

        Assert.Equal((uint)' ', buffer.GetCell(0, 0).Rune);
        AssertNoPlaceholderCode(buffer, 0, 0);
    }

    [Fact]
    public void OverwritingAPlaceholderWithANonAsciiCharacterClearsItsCode()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + D2);
        buffer.SetCursor(0, 0);
        Write(buffer, "é");

        Assert.Equal(0xE9u, buffer.GetCell(0, 0).Rune);
        AssertNoPlaceholderCode(buffer, 0, 0);
    }

    [Fact]
    public void OverwritingTwoPlaceholdersWithOneWideCharacterClearsBoth()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + P + D2 + P + D0);
        buffer.SetCursor(0, 0);
        Write(buffer, "界");

        Assert.Equal(2, buffer.GetCell(0, 0).Width);
        AssertNoPlaceholderCode(buffer, 0, 0);
        AssertNoPlaceholderCode(buffer, 0, 1);
        Assert.Equal(new PlaceholderIndices(0, Absent, Absent), Triple(buffer, 0, 2));
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void OverwritingAPlaceholderWithAPlaceholderReplacesTheCode()
    {
        var buffer = NewBuffer();
        Write(buffer, P + D1 + D2);
        buffer.SetCursor(0, 0);
        Write(buffer, P + D0);

        Assert.Equal(new PlaceholderIndices(0, Absent, Absent), Triple(buffer, 0, 0));

        buffer.SetCursor(0, 0);
        Write(buffer, P);
        AssertNoPlaceholderCode(buffer, 0, 0);
    }

    [Fact]
    public void ErasingAPlaceholderRowClearsTheCodes()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 16);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(P + D1 + D2 + P + D0 + D0));
        Assert.True(adapter.Buffer.GetColdCell(0, 0).GraphemeIndex <= -2);

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[2K"));

        AssertNoPlaceholderCode(adapter.Buffer, 0, 0);
        AssertNoPlaceholderCode(adapter.Buffer, 0, 1);
        Assert.Equal(0u, adapter.Buffer.GetCell(0, 0).Rune);
        Assert.Empty(adapter.Buffer.ValidateInvariants());
    }

    [Fact]
    public void InsertCharactersShiftsPlaceholderCodesWithTheirCells()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 16);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(P + D1 + D2 + P + D0 + D0 + "\u001b[1;1H\u001b[2@"));

        AssertNoPlaceholderCode(adapter.Buffer, 0, 0);
        AssertNoPlaceholderCode(adapter.Buffer, 0, 1);
        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(adapter.Buffer, 0, 2));
        Assert.Equal(new PlaceholderIndices(0, 0, Absent), Triple(adapter.Buffer, 0, 3));
    }

    [Fact]
    public void MarksSplitAcrossParserFeedsAreAttached()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 16);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(P));
        parser.Feed(Encoding.UTF8.GetBytes(D1));
        parser.Feed(Encoding.UTF8.GetBytes(D2));

        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(adapter.Buffer, 0, 0));
        Assert.Equal(1, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void GetRowTextReturnsTheBareRuneForPlaceholderCells()
    {
        var buffer = NewBuffer(cols: 6);
        Write(buffer, P + D0 + D0 + P + D1 + D2 + "a");

        Assert.Equal(P + P + "a   ", buffer.GetRowText(0));
    }

    [Fact]
    public void ScrollbackTextReturnsTheBareRuneForPlaceholderCells()
    {
        var buffer = NewBuffer(rows: 2, cols: 6);
        Write(buffer, P + D0 + D0 + P + D1 + D2 + "a");
        buffer.ScrollUpLines(1);

        string text = buffer.GetScrollbackLineText(0);
        Assert.Equal(P + P + "a", text.TrimEnd());
    }

    [Fact]
    public void ScrollbackRowKeepsTheCodesForTheResolver()
    {
        var buffer = NewBuffer(rows: 2, cols: 6);
        Write(buffer, P + D1 + D2);
        buffer.ScrollUpLines(1);

        Assert.True(buffer.TryGetScrollbackRow(0, out var hot, out var cold));
        Assert.Equal(0x10EEEEu, hot[0].Rune);
        Assert.True(PlaceholderTable.TryGet(cold[0].GraphemeIndex, out var indices));
        Assert.Equal(new PlaceholderIndices(1, 2, Absent), indices);
    }

    [Fact]
    public void ReflowKeepsEachCodeWithItsCell()
    {
        var buffer = NewBuffer(rows: 4, cols: 8);
        // Four placeholders whose row index encodes their original position.
        Write(buffer, P + D0 + D0 + P + D1 + D0 + P + D2 + D0 + P + D17 + D0);

        buffer.Resize(4, 2);

        // Reading order is preserved: cells land in rows of two.
        var seen = new List<ushort>();
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                if (buffer.GetCell(row, col).Rune == 0x10EEEEu)
                    seen.Add(Triple(buffer, row, col).Row);
            }
        }
        Assert.Equal(new ushort[] { 0, 1, 2, 17 }, seen);

        buffer.Resize(4, 8);
        for (int col = 0; col < 4; col++)
            Assert.Equal(new ushort[] { 0, 1, 2, 17 }[col], Triple(buffer, 0, col).Row);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void ReflowNarrowerThanAWideNeighbourStillKeepsPlaceholderCodes()
    {
        var buffer = NewBuffer(rows: 4, cols: 6);
        Write(buffer, P + D1 + D0 + "界" + P + D2 + D0);

        buffer.Resize(4, 3);

        var rows = new List<ushort>();
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                if (buffer.GetCell(row, col).Rune == 0x10EEEEu)
                    rows.Add(Triple(buffer, row, col).Row);
            }
        }
        Assert.Equal(new ushort[] { 1, 2 }, rows);
    }

    [Fact]
    public void PlaceholderCodesDoNotDisturbHyperlinksOnTheSameCell()
    {
        var buffer = NewBuffer();
        var attrs = new CellAttributes { HyperlinkId = 3 };
        buffer.WriteText((P + D1 + D2).AsSpan(), attrs);

        Assert.Equal((ushort)3, buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.True(buffer.GetCell(0, 0).HasHyperlink);
        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Triple(buffer, 0, 0));
    }
}
