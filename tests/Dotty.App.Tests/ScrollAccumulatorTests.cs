using Dotty.Runtime.Input;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScrollAccumulatorTests
{
    private static int SumVertical(double multiplier, params double[] deltas)
    {
        var accumulator = new ScrollAccumulator();
        int total = 0;
        foreach (double delta in deltas)
            total += accumulator.Add(0, delta, multiplier).Vertical;
        return total;
    }

    private static double[] Repeat(double delta, int count)
    {
        var deltas = new double[count];
        System.Array.Fill(deltas, delta);
        return deltas;
    }

    [Theory]
    [InlineData(0.1, 30, 3.0, 9)]
    [InlineData(1.0, 3, 3.0, 9)]
    [InlineData(1.0, 1, 3.0, 3)]
    [InlineData(-1.0, 2, 3.0, -6)]
    [InlineData(-0.5, 3, 1.0, -1)]
    [InlineData(-0.5, 4, 1.0, -2)]
    [InlineData(0.1, 1, 3.0, 0)]
    [InlineData(1.0, 2, 1.5, 3)]
    [InlineData(1.0, 3, 0.5, 1)]
    [InlineData(2.0, 1, 3.0, 6)]
    [InlineData(0.05, 100, 1.0, 5)]
    public void RepeatedDeltas_GiveExactLineCounts(double delta, int count, double multiplier, int expected) =>
        Assert.Equal(expected, SumVertical(multiplier, Repeat(delta, count)));

    [Fact]
    public void FractionCarriesAcrossEventsAndIsExposed()
    {
        var accumulator = new ScrollAccumulator();

        Assert.Equal(new ScrollSteps(0, 0), accumulator.Add(0, 0.4, 1.0));
        Assert.Equal(0.4, accumulator.VerticalRemainder, 9);
        Assert.Equal(new ScrollSteps(0, 1), accumulator.Add(0, 0.7, 1.0));
        Assert.Equal(0.1, accumulator.VerticalRemainder, 9);
    }

    [Fact]
    public void ReversingDirectionDropsTheOppositeRemainder()
    {
        var accumulator = new ScrollAccumulator();

        Assert.Equal(0, accumulator.Add(0, 0.9, 1.0).Vertical);
        Assert.Equal(0, accumulator.Add(0, -0.5, 1.0).Vertical);
        Assert.Equal(-0.5, accumulator.VerticalRemainder, 9);
        Assert.Equal(-1, accumulator.Add(0, -0.5, 1.0).Vertical);
    }

    [Fact]
    public void AxesAreIndependent_PositiveXIsLeft()
    {
        var accumulator = new ScrollAccumulator();

        Assert.Equal(new ScrollSteps(1, -2), accumulator.Add(1.0, -2.0, 1.0));
        Assert.Equal(new ScrollSteps(0, 0), accumulator.Add(0.5, 0, 1.0));
        Assert.Equal(new ScrollSteps(1, 0), accumulator.Add(0.5, 0, 1.0));
    }

    [Fact]
    public void ResetDropsBothRemainders()
    {
        var accumulator = new ScrollAccumulator();
        accumulator.Add(0.5, 0.5, 1.0);

        accumulator.Reset();

        Assert.Equal(0, accumulator.HorizontalRemainder);
        Assert.Equal(0, accumulator.VerticalRemainder);
        Assert.Equal(new ScrollSteps(0, 0), accumulator.Add(0.5, 0.5, 1.0));
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(1.0, -3.0)]
    [InlineData(1.0, double.NaN)]
    public void NonFiniteDeltaOrNonPositiveMultiplierScrollsNothing(double delta, double multiplier)
    {
        var accumulator = new ScrollAccumulator();

        Assert.True(accumulator.Add(delta, delta, multiplier).IsEmpty);
        Assert.Equal(0, accumulator.VerticalRemainder);
    }

    [Fact]
    public void HugeDeltaIsBoundedPerEvent()
    {
        var accumulator = new ScrollAccumulator();

        Assert.Equal(ScrollAccumulator.MaxStepsPerEvent, accumulator.Add(0, 1e12, 3.0).Vertical);
        Assert.Equal(-ScrollAccumulator.MaxStepsPerEvent, accumulator.Add(0, -1e12, 3.0).Vertical);
    }
}
