using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ScrollbackPolicyTests
{
    [Fact]
    public void Default_MatchesDottyDefaults()
    {
        ScrollbackPolicy policy = ScrollbackPolicy.Default;
        Assert.Equal(DottyDefaults.ScrollbackLines, policy.MaxLines);
        Assert.Equal(DottyDefaults.ScrollbackLimitBytes, policy.MaxBytes);
        Assert.Equal(5000, DottyDefaults.ScrollbackLines);
        Assert.Equal(67_108_864L, DottyDefaults.ScrollbackLimitBytes);
    }

    [Fact]
    public void LibraryDefaultConstructorsUseTheSingleDefault()
    {
        using var buffer = new TerminalBuffer();
        Assert.Equal(DottyDefaults.ScrollbackLines, buffer.ActiveBuffer.ScrollbackCapacity);
        using var adapter = new TerminalAdapter();
        Assert.Equal(DottyDefaults.ScrollbackLines, adapter.Buffer.ActiveBuffer.ScrollbackCapacity);
        var screen = new Screen(rows: 2, columns: 2);
        try { Assert.Equal(DottyDefaults.ScrollbackLines, screen.ScrollbackCapacity); }
        finally { screen.Dispose(); }
        using var manager = new ScreenManager(2, 2);
        Assert.Equal(DottyDefaults.ScrollbackLines, manager.Active.ScrollbackCapacity);
    }

    [Fact]
    public void Normalize_ClampsLinesAndBytes()
    {
        Assert.Equal(new ScrollbackPolicy(0, 0), new ScrollbackPolicy(0, 0).Normalize());
        Assert.Equal(DottyDefaults.ScrollbackLines, new ScrollbackPolicy(-1, 0).Normalize().MaxLines);
        Assert.Equal(ScrollbackPolicy.HardMaxLines, new ScrollbackPolicy(int.MaxValue, 0).Normalize().MaxLines);
        Assert.Equal(1_000_000, ScrollbackPolicy.HardMaxLines);
        Assert.Equal(DottyDefaults.ScrollbackLimitBytes, new ScrollbackPolicy(10, -5).Normalize().MaxBytes);
        Assert.Equal(DottyDefaults.ScrollbackLimitBytes, new ScrollbackPolicy(10, 1000).Normalize().MaxBytes);
        Assert.Equal(1_048_576L, new ScrollbackPolicy(10, 1_048_576).Normalize().MaxBytes);
        Assert.Equal(0L, new ScrollbackPolicy(10, 0).Normalize().MaxBytes);
        Assert.Equal(long.MaxValue, new ScrollbackPolicy(10, long.MaxValue).Normalize().MaxBytes);
    }

    [Fact]
    public void CapacityFor_AppliesTheFirstLimitReached()
    {
        // floor(1048576 / (80 * 12)) - 24 = 1092 - 24 = 1068
        Assert.Equal(1068, new ScrollbackPolicy(5000, 1_048_576).CapacityFor(24, 80));
        Assert.Equal(500, new ScrollbackPolicy(500, 1_048_576).CapacityFor(24, 80));
        Assert.Equal(5000, new ScrollbackPolicy(5000, 0).CapacityFor(24, 80));
        Assert.Equal(0, new ScrollbackPolicy(0, 0).CapacityFor(24, 80));
        Assert.Equal(5000, ScrollbackPolicy.Fixed(5000).CapacityFor(24, 80));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 80)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 100000)]
    [InlineData(0, 0)]
    public void CapacityFor_IsNeverNegative(int rows, int columns)
    {
        Assert.True(new ScrollbackPolicy(5000, 1_048_576).CapacityFor(rows, columns) >= 0);
        Assert.True(new ScrollbackPolicy(5000, 1).CapacityFor(rows, columns) >= 0);
        Assert.Equal(0, new ScrollbackPolicy(5000, 1_048_576).CapacityFor(5000, 100000));
    }

    [Fact]
    public void CapacityFor_DoesNotOverflowForLargeValues()
    {
        int capacity = new ScrollbackPolicy(ScrollbackPolicy.HardMaxLines, long.MaxValue).CapacityFor(int.MaxValue, int.MaxValue);
        Assert.True(capacity >= 0 && capacity <= ScrollbackPolicy.HardMaxLines);
    }
}
