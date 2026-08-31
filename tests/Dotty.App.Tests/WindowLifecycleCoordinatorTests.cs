using System;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowLifecycleCoordinatorTests
{
    [Fact]
    public void Drain_ExecutesCallbacksInOrder()
    {
        using var lifecycle = new WindowLifecycleCoordinator();
        string value = string.Empty;

        Assert.True(lifecycle.TryEnqueue(() => value += "a"));
        Assert.True(lifecycle.TryEnqueue(() => value += "b"));

        Assert.Equal(2, lifecycle.Drain());
        Assert.Equal("ab", value);
    }

    [Fact]
    public void Close_PreventsQueuedAndFutureCallbacks()
    {
        using var lifecycle = new WindowLifecycleCoordinator();
        bool executed = false;
        lifecycle.TryEnqueue(() => executed = true);

        lifecycle.Close();

        Assert.True(lifecycle.IsClosed);
        Assert.False(lifecycle.TryEnqueue(() => executed = true));
        Assert.Equal(0, lifecycle.Drain());
        Assert.False(executed);
    }

    [Fact]
    public void Drain_ContinuesAfterCallbackFailure()
    {
        using var lifecycle = new WindowLifecycleCoordinator();
        bool executed = false;
        lifecycle.TryEnqueue(() => throw new InvalidOperationException("stale callback"));
        lifecycle.TryEnqueue(() => executed = true);

        Assert.Equal(2, lifecycle.Drain());
        Assert.True(executed);
    }
}
