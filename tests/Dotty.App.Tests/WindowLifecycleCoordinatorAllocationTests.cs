using System;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class WindowLifecycleCoordinatorAllocationTests
{
    private static int _callbackCount;
    private static readonly Action Callback = IncrementCallback;

    [Fact]
    public void EnqueueAndDrainAllocateNothingAfterWarmup()
    {
        using var coordinator = new WindowLifecycleCoordinator();
        int initialCallbackCount = _callbackCount;
        for (int i = 0; i < 128; i++)
        {
            coordinator.TryEnqueue(Callback);
            coordinator.Drain();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            coordinator.TryEnqueue(Callback);
            coordinator.Drain();
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(initialCallbackCount + 1128, _callbackCount);
    }

    private static void IncrementCallback() => _callbackCount++;
}
