using System;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

[Collection("Allocation-sensitive tests")]
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

        AllocationAssert.NoAllocations(
            EnqueueAndDrain,
            warmupIterations: 0,
            measuredIterationsPerWindow: 200);
        Assert.Equal(initialCallbackCount + 1128, _callbackCount);

        void EnqueueAndDrain()
        {
            coordinator.TryEnqueue(Callback);
            coordinator.Drain();
        }
    }

    private static void IncrementCallback() => _callbackCount++;
}
