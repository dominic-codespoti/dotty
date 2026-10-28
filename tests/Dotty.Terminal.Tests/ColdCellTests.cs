using System.Runtime.CompilerServices;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// <see cref="ColdCell"/> is stored as index + 1 so that all-zero memory is the empty cell. The screen arenas
/// depend on that (fresh and discarded OS pages are zero), as does the 12 bytes/cell arena arithmetic.
/// </summary>
public sealed class ColdCellTests
{
    private static uint Bits(ColdCell cell) => Unsafe.As<ColdCell, uint>(ref cell);

    [Fact]
    public void CellLayoutIsFourColdBytesAndEightHotBytes()
    {
        Assert.Equal(4, Unsafe.SizeOf<ColdCell>());
        Assert.Equal(8, Unsafe.SizeOf<CellHot>());
    }

    [Fact]
    public void DefaultIsTheEmptyCell()
    {
        ColdCell cell = default;

        Assert.Equal((ushort)0, cell.HyperlinkId);
        Assert.Equal(-1, cell.GraphemeIndex);
        Assert.Equal(0u, Bits(cell));
        Assert.Equal(0u, Bits(new ColdCell()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(32767)] // short.MaxValue
    public void GraphemeIndexRoundTrips(int index)
    {
        var cell = new ColdCell { GraphemeIndex = (short)index };

        Assert.Equal((short)index, cell.GraphemeIndex);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void ExplicitMinusOneIsBitwiseIdenticalToDefault()
    {
        var cell = new ColdCell { GraphemeIndex = -1 };

        Assert.Equal(0u, Bits(cell));
        Assert.Equal(default(ColdCell), cell);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-32768)] // short.MinValue
    public void AnyNegativeIndexMeansNoGrapheme(int index)
    {
        var cell = new ColdCell { GraphemeIndex = (short)index };

        Assert.Equal(-1, cell.GraphemeIndex);
        Assert.Equal(0u, Bits(cell));
    }

    [Fact]
    public void HyperlinkIdAndGraphemeIndexAreIndependent()
    {
        var cell = new ColdCell { HyperlinkId = ushort.MaxValue, GraphemeIndex = short.MaxValue };

        Assert.Equal(ushort.MaxValue, cell.HyperlinkId);
        Assert.Equal(short.MaxValue, cell.GraphemeIndex);

        cell.GraphemeIndex = -1;
        Assert.Equal(ushort.MaxValue, cell.HyperlinkId);
        Assert.Equal(-1, cell.GraphemeIndex);

        cell.GraphemeIndex = 7;
        cell.HyperlinkId = 0;
        Assert.Equal(7, cell.GraphemeIndex);
    }

    [Fact]
    public void ResetRestoresTheDefaultCell()
    {
        var cell = new ColdCell { HyperlinkId = 9, GraphemeIndex = 123 };

        cell.Reset();

        Assert.Equal(0u, Bits(cell));
        Assert.Equal(-1, cell.GraphemeIndex);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void ZeroFilledSpanIsAnEmptyRow()
    {
        var row = new ColdCell[80];
        foreach (var cell in row)
        {
            Assert.Equal(-1, cell.GraphemeIndex);
            Assert.Equal((ushort)0, cell.HyperlinkId);
        }

        Span<ColdCell> span = row;
        span.Fill(new ColdCell { HyperlinkId = 3, GraphemeIndex = 7 });
        span.Clear();
        foreach (var cell in span)
        {
            Assert.Equal(-1, cell.GraphemeIndex);
            Assert.Equal((ushort)0, cell.HyperlinkId);
        }
    }

    [Fact]
    public void FreshBufferColdPlaneIsEmptyAndConsistent()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10, scrollbackCapacity: 8);

        for (int row = 0; row < 4; row++)
        {
            var cold = buffer.GetRowColdCells(row);
            Assert.Equal(10, cold.Length);
            foreach (var cell in cold)
            {
                Assert.Equal(-1, cell.GraphemeIndex);
                Assert.Equal((ushort)0, cell.HyperlinkId);
            }
        }

        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void ErasingAGraphemeCellRestoresTheEmptyColdCell()
    {
        using var buffer = new TerminalBuffer(rows: 2, columns: 10, scrollbackCapacity: 4);
        buffer.SetCursor(0, 0);
        buffer.WriteText("e\u0301".AsSpan(), CellAttributes.Default);
        Assert.True(buffer.GetColdCell(0, 0).GraphemeIndex >= 0);

        buffer.SetCursor(0, 0);
        buffer.EraseCharacters(1);

        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal(-1, cold.GraphemeIndex);
        Assert.Equal((ushort)0, cold.HyperlinkId);
        Assert.Empty(buffer.ValidateInvariants());
    }
}
