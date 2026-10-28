using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Direct tests of the single row-to-text implementation and its two continuation-cell policies (WI-T3).</summary>
public sealed class RowTextTests
{
    private static CellHot Cell(uint rune, bool wide = false, bool continuation = false)
    {
        var cell = new CellHot { Rune = rune };
        if (wide) cell.Width = 2;
        if (continuation) cell.IsContinuation = true;
        return cell;
    }

    private static string AppendOne(CellHot cell, short graphemeIndex)
    {
        var sb = ZStr.CreateStringBuilder();
        try
        {
            int written = RowText.AppendCell(ref sb, in cell, graphemeIndex);
            Assert.Equal(sb.Length, written);
            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    [Theory]
    [InlineData(0u, "")]          // empty cell reads as one space (expected below)
    [InlineData((uint)'a', "a")]
    [InlineData(0x00E9u, "\u00E9")]
    [InlineData(0x754Cu, "界")]
    [InlineData(0x1F600u, "\U0001F600")]
    [InlineData(0xD800u, " ")]    // surrogate scalar: unrepresentable
    [InlineData(0x110000u, " ")]  // beyond Unicode
    public void AppendCell_SingleScalars(uint rune, string expected)
    {
        if (rune == 0) expected = " ";
        Assert.Equal(expected, AppendOne(Cell(rune), -1));
    }

    [Fact]
    public void AppendCell_StoredGraphemeWinsOverTheBaseRune()
    {
        short index = GraphemeHelper.StoreGrapheme("e\u0301");
        Assert.True(index > 0);
        Assert.Equal("e\u0301", AppendOne(Cell('e'), index));
    }

    [Fact]
    public void AppendCell_NoGraphemeIndexMeansBaseRuneOnly()
    {
        Assert.Equal("e", AppendOne(Cell('e'), -1));
        Assert.Equal("e", AppendOne(Cell('e'), 0));
    }

    [Fact]
    public void AppendCell_EmptyCellIgnoresAStaleGraphemeIndex()
    {
        short index = GraphemeHelper.StoreGrapheme("e\u0301");
        Assert.Equal(" ", AppendOne(Cell(0), index));
    }

    private static CellHot[] WideRow() =>
    [
        Cell(0x754C, wide: true),
        Cell(0, continuation: true),
        Cell('a'),
        Cell(0),
        Cell('b'),
    ];

    [Fact]
    public void Build_SpacePolicy_KeepsOneSpacePerPlaceholderAndEmptyCell()
    {
        Assert.Equal("界 a b", RowText.Build(WideRow(), default, 0, 4, ContinuationPolicy.SpaceCell));
    }

    [Fact]
    public void Build_SkipPolicy_DropsPlaceholderCellsOnly()
    {
        Assert.Equal("界a b", RowText.Build(WideRow(), default, 0, 4, ContinuationPolicy.SkipCell));
    }

    [Fact]
    public void Build_UsesColdGraphemesWhenPresentAndToleratesShortOrMissingColdSpans()
    {
        var hot = new[] { Cell('e'), Cell('x') };
        var cold = new[]
        {
            new ColdCell { GraphemeIndex = GraphemeHelper.StoreGrapheme("e\u0301") },
            new ColdCell { GraphemeIndex = -1 },
        };

        Assert.Equal("e\u0301x", RowText.Build(hot, cold, 0, 1, ContinuationPolicy.SpaceCell));
        Assert.Equal("e\u0301x", RowText.Build(hot, cold.AsSpan(0, 1), 0, 1, ContinuationPolicy.SpaceCell));
        Assert.Equal("ex", RowText.Build(hot, default, 0, 1, ContinuationPolicy.SpaceCell));
    }

    [Fact]
    public void Build_ClampsTheRangeToTheRow()
    {
        CellHot[] row = WideRow();
        Assert.Equal("a b", RowText.Build(row, default, 2, 99, ContinuationPolicy.SpaceCell));
        Assert.Equal("界 ", RowText.Build(row, default, -5, 1, ContinuationPolicy.SpaceCell));
        Assert.Equal(string.Empty, RowText.Build(row, default, 3, 2, ContinuationPolicy.SpaceCell));
        Assert.Equal(string.Empty, RowText.Build(default, default, 0, 5, ContinuationPolicy.SpaceCell));
    }

    [Fact]
    public void Append_ExtendsAnExistingBuilder()
    {
        var sb = ZStr.CreateStringBuilder();
        try
        {
            sb.Append('>');
            RowText.Append(ref sb, WideRow(), default, 0, 2, ContinuationPolicy.SkipCell);
            Assert.Equal(">界a", sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }
}
