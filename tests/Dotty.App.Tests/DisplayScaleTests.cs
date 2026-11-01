using System;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DisplayScaleTests
{
    private static readonly DisplayMetrics Identity = DisplayMetrics.Identity;

    // platform, window w/h, framebuffer w/h, glfw content scale x/y, expected content scale, expected window→device.
    public static TheoryData<DisplayPlatform, int, int, int, int, float, float, float, float> PlatformCases => new()
    {
        // Windows: per-monitor DPI aware, framebuffer == window in physical pixels; only the content scale knows the DPI.
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 1f, 1f, 1f, 1f },
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 1.25f, 1.25f, 1.25f, 1f },
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 1.5f, 1.5f, 1.5f, 1f },
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 1.75f, 1.75f, 1.75f, 1f },
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 2f, 2f, 2f, 1f },
        { DisplayPlatform.Win32, 1000, 700, 1000, 700, 3f, 3f, 3f, 1f },
        // X11 with Xft.dpi = 144: same shape as Windows.
        { DisplayPlatform.X11, 1200, 800, 1200, 800, 1.5f, 1.5f, 1.5f, 1f },
        // macOS Retina: backing framebuffer is twice the window; ratio is the content scale and the pointer factor.
        { DisplayPlatform.Cocoa, 800, 600, 1600, 1200, 2f, 2f, 2f, 2f },
        { DisplayPlatform.Cocoa, 800, 600, 800, 600, 1f, 1f, 1f, 1f },
        { DisplayPlatform.Cocoa, 800, 600, 2400, 1800, 3f, 3f, 3f, 3f },
        // Wayland fractional-scale-v1: content scale reported exactly; framebuffer = round(w * n / 120).
        { DisplayPlatform.Wayland, 1000, 700, 1250, 875, 1.25f, 1.25f, 1.25f, 1.25f },
        { DisplayPlatform.Wayland, 1000, 700, 1500, 1050, 1.5f, 1.5f, 1.5f, 1.5f },
        { DisplayPlatform.Wayland, 1000, 700, 1750, 1225, 1.75f, 1.75f, 1.75f, 1.75f },
        { DisplayPlatform.Wayland, 1000, 700, 2000, 1400, 2f, 2f, 2f, 2f },
        { DisplayPlatform.Wayland, 1000, 700, 3000, 2100, 3f, 3f, 3f, 3f },
    };

    [Theory]
    [MemberData(nameof(PlatformCases))]
    public void Compute_SelectsThePlatformSource(
        DisplayPlatform platform, int windowWidth, int windowHeight, int framebufferWidth, int framebufferHeight,
        float contentScaleX, float contentScaleY, float expectedContent, float expectedWindow)
    {
        var metrics = DisplayScale.Compute(platform, windowWidth, windowHeight, framebufferWidth, framebufferHeight,
            contentScaleX, contentScaleY, Identity);

        Assert.Equal(expectedContent, metrics.ContentScale, 5);
        Assert.Equal(expectedWindow, metrics.WindowUnitsToDevice, 5);
    }

    [Fact]
    public void Compute_Windows125PercentWithEqualSizes_ScalesFontsButNotThePointer()
    {
        // The defect this model fixes: fb/window is 1.0 on Windows at any DPI.
        var metrics = DisplayScale.Compute(DisplayPlatform.Win32, 1000, 700, 1000, 700, 1.25f, 1.25f, Identity);

        Assert.Equal(new DisplayMetrics(1.25f, 1f), metrics);
        Assert.Equal(500f, metrics.WindowToDevice(500f));
    }

    [Theory]
    [InlineData(150)] // 1.25
    [InlineData(144)] // 1.2
    [InlineData(140)] // 1.1666…
    [InlineData(180)] // 1.5
    [InlineData(210)] // 1.75
    public void Compute_WaylandFramebufferJitterQuantisesToTheSameScale(int numerator)
    {
        float expected = numerator / 120f;
        for (int width = 600; width <= 3000; width++)
        {
            // GLFW fork: fbWidth = (width * numerator + 60) / 120 — the ratio jitters with the width.
            int framebufferWidth = (width * numerator + 60) / 120;
            var fromRatio = DisplayScale.Compute(DisplayPlatform.Wayland, width, 500, framebufferWidth, 0, 0f, 0f, Identity);
            Assert.Equal(expected, fromRatio.ContentScale);
        }
    }

    [Theory]
    [InlineData(1.2491f, 1.25f)]
    [InlineData(1.2541f, 1.25f)]
    [InlineData(1.16667f, 140f / 120f)]
    [InlineData(0.001f, 1f / 120f)] // never quantises to zero
    public void QuantizeWayland_RoundsToOneHundredTwentieths(float raw, float expected) =>
        Assert.Equal(expected, DisplayScale.QuantizeWayland(raw));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.PositiveInfinity)]
    public void Compute_FailedContentScaleQueryFallsBackToTheFramebufferRatio(float bad)
    {
        var win = DisplayScale.Compute(DisplayPlatform.Win32, 1000, 700, 1000, 700, bad, bad, new DisplayMetrics(1.5f, 1f));
        Assert.Equal(new DisplayMetrics(1f, 1f), win);

        var wayland = DisplayScale.Compute(DisplayPlatform.Wayland, 1000, 700, 1500, 1050, bad, bad, Identity);
        Assert.Equal(new DisplayMetrics(1.5f, 1.5f), wayland);
    }

    [Fact]
    public void Compute_UsesTheYScaleWhenOnlyXIsInvalid() =>
        Assert.Equal(1.5f, DisplayScale.Compute(DisplayPlatform.X11, 100, 100, 100, 100, float.NaN, 1.5f, Identity).ContentScale);

    [Theory]
    [InlineData(DisplayPlatform.Win32)]
    [InlineData(DisplayPlatform.Cocoa)]
    [InlineData(DisplayPlatform.Wayland)]
    [InlineData(DisplayPlatform.X11)]
    public void Compute_MinimisedZeroSizesAndNoScaleKeepThePreviousMetrics(DisplayPlatform platform)
    {
        var previous = new DisplayMetrics(1.75f, platform is DisplayPlatform.Cocoa or DisplayPlatform.Wayland ? 1.75f : 1f);

        var metrics = DisplayScale.Compute(platform, 0, 0, 0, 0, float.NaN, 0f, previous);

        Assert.Equal(previous, metrics);
    }

    [Fact]
    public void Compute_MinimisedWindowsKeepsTheReportedContentScale()
    {
        var metrics = DisplayScale.Compute(DisplayPlatform.Win32, 0, 0, 0, 0, 2f, 2f, new DisplayMetrics(1f, 1f));
        Assert.Equal(new DisplayMetrics(2f, 1f), metrics);
    }

    [Fact]
    public void Compute_GarbagePreviousMetricsBecomeIdentity() =>
        Assert.Equal(Identity, DisplayScale.Compute(DisplayPlatform.Unknown, 0, 0, 0, 0, 0f, 0f, new DisplayMetrics(float.NaN, -3f)));

    [Theory]
    [InlineData(DisplayPlatform.Win32, 1000, 1000, 1f, 2f, 2f, 1f)] // WM_DPICHANGED: window and framebuffer both double
    [InlineData(DisplayPlatform.Cocoa, 800, 800, 1f, 1600, 2f, 2f)]  // same logical size, backing doubles
    [InlineData(DisplayPlatform.Wayland, 800, 800, 1f, 1600, 2f, 2f)]
    public void Compute_MonitorMoveFromOneToTwoTimes(
        DisplayPlatform platform, int window, int framebufferBefore, float scaleBefore, float framebufferAfterOrScale, float expectedContent, float expectedWindow)
    {
        var before = DisplayScale.Compute(platform, window, window, framebufferBefore, framebufferBefore, scaleBefore, scaleBefore, Identity);
        Assert.Equal(1f, before.ContentScale);

        int windowAfter = platform == DisplayPlatform.Win32 ? window * 2 : window;
        int framebufferAfter = platform == DisplayPlatform.Win32 ? window * 2 : (int)framebufferAfterOrScale;
        var after = DisplayScale.Compute(platform, windowAfter, windowAfter, framebufferAfter, framebufferAfter, 2f, 2f, before);

        Assert.Equal(new DisplayMetrics(expectedContent, expectedWindow), after);
        Assert.True(DisplayScale.ContentScaleChanged(before, after));
    }

    [Theory]
    [InlineData(100f, DisplayScale.MaxScale)]
    [InlineData(0.01f, DisplayScale.MinScale)]
    public void Compute_ClampsAbsurdScales(float reported, float expected) =>
        Assert.Equal(expected, DisplayScale.Compute(DisplayPlatform.Win32, 100, 100, 100, 100, reported, reported, Identity).ContentScale);

    [Fact]
    public void ContentScaleChanged_IgnoresOnlyTheWindowRatio() =>
        Assert.False(DisplayScale.ContentScaleChanged(new DisplayMetrics(1.5f, 1f), new DisplayMetrics(1.5f, 1.5f)));

    [Fact]
    public void DeviceToWindow_InvertsWindowToDevice()
    {
        var metrics = new DisplayMetrics(1.5f, 1.5f);
        Assert.Equal(300f, metrics.WindowToDevice(200f));
        Assert.Equal(200f, metrics.DeviceToWindow(300f));
    }
}
