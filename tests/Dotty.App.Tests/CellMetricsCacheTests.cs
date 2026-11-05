using System;
using Dotty.Silk.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class CellMetricsCacheTests
{
    private static readonly TypefaceIdentity Face = new("Test Mono", 400, 5, SKFontStyleSlant.Upright);

    private static CellMetricsKey Key(char name) => KeyOf(Face, name);

    private static CellMetricsKey KeyOf(TypefaceIdentity face, float size) => new(face, new CellMetricsOptions(size, 1.2, 1f));

    private static CellMetrics Metrics(char name) => new(name, name * 2, name, name, 1, 0, name + 1, 1, name / 2, 1, 0, 1);

    // Operations: "+X" sets X, "?X" reads X. Expected: the keys present afterwards, in any order.
    [Theory]
    [InlineData(2, "+A +B +C", "BC")]               // oldest insert evicted
    [InlineData(2, "+A +B ?A +C", "AC")]            // a hit refreshes recency
    [InlineData(2, "+A +B +A +C", "AC")]            // overwrite refreshes recency
    [InlineData(2, "+A +B +B", "AB")]               // overwrite at capacity evicts nothing
    [InlineData(3, "+A +B +C ?A ?B +D +E", "BDE")]  // repeated eviction follows recency
    [InlineData(1, "+A +B ?A +C", "C")]
    public void EvictsLeastRecentlyUsed(int capacity, string operations, string expectedKeys)
    {
        var cache = new CellMetricsCache(capacity);
        foreach (string op in operations.Split(' '))
        {
            if (op[0] == '+')
                cache.Set(Key(op[1]), Metrics(op[1]));
            else
                cache.TryGet(Key(op[1]), out _);
        }

        Assert.Equal(expectedKeys.Length, cache.Count);
        foreach (char name in expectedKeys)
        {
            Assert.True(cache.TryGet(Key(name), out CellMetrics metrics), $"{name} should be cached");
            Assert.Equal(Metrics(name), metrics);
        }
    }

    [Fact]
    public void KeysCompareFaceByValue()
    {
        var cache = new CellMetricsCache(4);
        var metrics = Metrics('H');
        cache.Set(KeyOf(new TypefaceIdentity("Test Mono", 400, 5, SKFontStyleSlant.Upright), 14f), metrics);

        Assert.True(cache.TryGet(KeyOf(new TypefaceIdentity("Test Mono", 400, 5, SKFontStyleSlant.Upright), 14f), out var hit));
        Assert.Equal(metrics, hit);
        Assert.False(cache.TryGet(KeyOf(new TypefaceIdentity("Test Mono", 700, 5, SKFontStyleSlant.Upright), 14f), out _));
        Assert.False(cache.TryGet(KeyOf(new TypefaceIdentity("Test Mono", 400, 5, SKFontStyleSlant.Italic), 14f), out _));
        Assert.False(cache.TryGet(KeyOf(new TypefaceIdentity("Other Mono", 400, 5, SKFontStyleSlant.Upright), 14f), out _));
    }

    [Fact]
    public void RejectsNonPositiveCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CellMetricsCache(0));
}
