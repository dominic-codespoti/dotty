using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class QuadFrameBuilderOffsetTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(11.49f, 11)]
    [InlineData(11.5f, 12)]     // half away from zero (truncation gave 11)
    [InlineData(11.9f, 12)]     // truncation gave 11
    [InlineData(-0.4f, 0)]
    [InlineData(-0.5f, -1)]     // half away from zero (truncation gave 0)
    [InlineData(-2.7f, -3)]     // truncation gave -2
    [InlineData(12.5f, 13)]     // not banker's rounding (which gives 12)
    [InlineData(1e9f, short.MaxValue)]
    [InlineData(-1e9f, short.MinValue)]
    public void RoundPixelOffset_RoundsHalfAwayFromZero(float offset, short expected) =>
        Assert.Equal(expected, QuadFrameBuilder.RoundPixelOffset(offset));
}
