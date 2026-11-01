using System;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class GridFitTests
{
    [Theory]
    [InlineData(14d, 1f, 14)]
    [InlineData(14d, 1.25f, 18)]   // 17.5 rounds away from zero, deterministically
    [InlineData(14d, 1.5f, 21)]
    [InlineData(14d, 1.75f, 25)]   // 24.5
    [InlineData(14d, 2f, 28)]
    [InlineData(8d, 1.25f, 10)]
    [InlineData(0.3d, 1f, 0)]
    [InlineData(0d, 2f, 0)]
    [InlineData(-4d, 2f, 0)]
    [InlineData(double.NaN, 2f, 0)]
    [InlineData(10d, float.NaN, 0)]
    [InlineData(10d, 0f, 0)]
    [InlineData(1e12, 1f, GridFit.MaxClientUnits)]
    public void ToDevicePixels_RoundsLogicalLengths(double logical, float scale, int expected) =>
        Assert.Equal(expected, GridFit.ToDevicePixels(logical, scale));

    [Fact]
    public void DevicePadding_FromLogicalRoundsEachSide()
    {
        var padding = DevicePadding.FromLogical(14, 8, 14, 8, 1.25f);
        Assert.Equal(new DevicePadding(18, 10, 18, 10), padding);
        Assert.Equal(36, padding.Horizontal);
        Assert.Equal(20, padding.Vertical);
    }

    [Theory]
    [InlineData(800, 600, 9, 19, 0, 0, 0, 88, 31)]
    [InlineData(800, 600, 9, 19, 1, 0, 0, 88, 30)]
    [InlineData(800, 600, 9, 19, 1, 28, 16, 85, 29)]
    [InlineData(810, 608, 9, 19, 0, 0, 0, 90, 32)] // exact fit
    [InlineData(809, 607, 9, 19, 0, 0, 0, 89, 31)] // one pixel short
    [InlineData(0, 0, 9, 19, 1, 28, 16, 1, 1)]     // minimised
    [InlineData(10, 10, 9, 19, 1, 28, 16, 1, 1)]
    [InlineData(100, 100, 0, -5, 0, 0, 0, 100, 100)] // nonsense cell becomes 1 px
    public void Compute_FitsWholeCells(int fbWidth, int fbHeight, int cellWidth, int cellHeight, int barRows, int padX, int padY, int cols, int rows)
    {
        var padding = new DevicePadding(padX / 2, padY / 2, padX - padX / 2, padY - padY / 2);
        Assert.Equal((cols, rows), GridFit.Compute(fbWidth, fbHeight, cellWidth, cellHeight, barRows, padding));
    }

    // cols, rows, cell px w/h, bar rows, logical padding (l, t, r, b), content scale, window→device.
    public static TheoryData<int, int, int, int, int, double, double, float, float> RoundTrips => new()
    {
        { 80, 24, 9, 19, 0, 14, 8, 1f, 1f },
        { 80, 24, 9, 19, 1, 14, 8, 1f, 1f },
        { 132, 43, 8, 17, 2, 0, 0, 1f, 1f },
        { 100, 30, 18, 38, 1, 14, 8, 2f, 2f },       // Retina / Wayland 2x
        { 100, 30, 11, 24, 1, 14, 8, 1.25f, 1f },     // Windows 125 %: window units are device pixels
        { 100, 30, 11, 24, 1, 14, 8, 1.25f, 1.25f },  // Wayland 1.25
        { 97, 31, 13, 28, 1, 10.5, 4.75, 1.5f, 1.5f },
        { 200, 60, 16, 33, 1, 20, 10, 1.75f, 1.75f },
        { 120, 40, 12, 25, 1, 14, 8, 140f / 120f, 140f / 120f }, // non-terminating Wayland scale
        { 1, 1, 4, 8, 0, 0, 0, 3f, 3f },
    };

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void ClientSize_RoundTripsThroughCompute(
        int cols, int rows, int cellWidth, int cellHeight, int barRows, double padX, double padY, float contentScale, float windowToDevice)
    {
        var padding = DevicePadding.FromLogical(padX, padY, padX, padY, contentScale);
        var (width, height) = GridFit.ClientSize(cols, rows, cellWidth, cellHeight, barRows, padding, windowToDevice);

        // The compositor/GLFW rounding of window units to the framebuffer (Wayland: round(w * n / 120)).
        int fbWidth = (int)Math.Round(width * (double)windowToDevice, MidpointRounding.AwayFromZero);
        int fbHeight = (int)Math.Round(height * (double)windowToDevice, MidpointRounding.AwayFromZero);

        Assert.Equal((cols, rows), GridFit.Compute(fbWidth, fbHeight, cellWidth, cellHeight, barRows, padding));
        // Grid plus padding never exceeds the framebuffer.
        Assert.True(cols * cellWidth + padding.Horizontal <= fbWidth);
        Assert.True((rows + barRows) * cellHeight + padding.Vertical <= fbHeight);
    }

    [Theory]
    [InlineData(80, 24, 9, 19, 0, 14, 8)]
    [InlineData(80, 24, 9, 19, 1, 14, 8)]
    [InlineData(100, 30, 11, 24, 1, 18, 10)]
    public void ClientSize_IsTheSmallestSizeThatFitsAtUnitRatio(int cols, int rows, int cellWidth, int cellHeight, int barRows, int padX, int padY)
    {
        var padding = new DevicePadding(padX, padY, padX, padY);
        var (width, height) = GridFit.ClientSize(cols, rows, cellWidth, cellHeight, barRows, padding, 1f);

        Assert.Equal((cols - 1, rows - 1), GridFit.Compute(width - 1, height - 1, cellWidth, cellHeight, barRows, padding));
    }

    [Fact]
    public void ClientSize_ExactQuotientsDoNotRoundUpByFloatNoise()
    {
        // 900 device px at 1.5 is exactly 600 window units.
        var (width, _) = GridFit.ClientSize(100, 1, 9, 10, 0, default, 1.5f);
        Assert.Equal(600, width);
    }

    [Fact]
    public void ClientSize_SurvivesGarbageInputs()
    {
        var (width, height) = GridFit.ClientSize(int.MaxValue, 0, int.MaxValue, -3, -7, default, float.NaN);
        Assert.InRange(width, 1, GridFit.MaxClientUnits);
        Assert.InRange(height, 1, GridFit.MaxClientUnits);
    }

    [Fact]
    public void Compute_PropertyHoldsForManyFramebuffers()
    {
        var random = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            int cellWidth = random.Next(1, 40), cellHeight = random.Next(1, 80), barRows = random.Next(0, 3);
            var padding = new DevicePadding(random.Next(0, 40), random.Next(0, 40), random.Next(0, 40), random.Next(0, 40));
            int fbWidth = padding.Horizontal + cellWidth + random.Next(0, 4000);
            int fbHeight = padding.Vertical + cellHeight * (barRows + 1) + random.Next(0, 3000);

            var (cols, rows) = GridFit.Compute(fbWidth, fbHeight, cellWidth, cellHeight, barRows, padding);

            Assert.True(cols * cellWidth + padding.Horizontal <= fbWidth);
            Assert.True((cols + 1) * cellWidth + padding.Horizontal > fbWidth);
            Assert.True((rows + barRows) * cellHeight + padding.Vertical <= fbHeight);
            Assert.True((rows + barRows + 1) * cellHeight + padding.Vertical > fbHeight);
        }
    }
}
