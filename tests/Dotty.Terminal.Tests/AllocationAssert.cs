using System;
using Xunit;

namespace Dotty.Terminal.Tests;

internal static class AllocationAssert
{
    private const int DefaultWarmupIterations = 8;
    private const int DefaultMeasuredIterationsPerWindow = 20;
    private const int DefaultWindows = 5;

    public static void NoAllocations(
        Action action,
        int warmupIterations = DefaultWarmupIterations,
        int measuredIterationsPerWindow = DefaultMeasuredIterationsPerWindow,
        int windows = DefaultWindows,
        Action? beforeEachWindow = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measuredIterationsPerWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);

        for (int i = 0; i < warmupIterations; i++)
            action();

        long minimumWindowAllocation = long.MaxValue;
        for (int window = 0; window < windows; window++)
        {
            beforeEachWindow?.Invoke();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredIterationsPerWindow; i++)
                action();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < minimumWindowAllocation)
                minimumWindowAllocation = allocated;
        }

        Assert.Equal(0L, minimumWindowAllocation);
    }
}

[CollectionDefinition("Allocation-sensitive tests", DisableParallelization = true)]
public sealed class AllocationSensitiveTestCollection { }