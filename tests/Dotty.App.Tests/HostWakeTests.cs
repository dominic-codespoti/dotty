using System.Threading.Tasks;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

[CollectionDefinition(HostWakeCollection.Name, DisableParallelization = true)]
public sealed class HostWakeCollection
{
    public const string Name = "Host wake static state";
}

[Collection(HostWakeCollection.Name)]
public sealed class HostWakeTests : IDisposable
{
    public HostWakeTests() => HostWake.Shutdown();

    public void Dispose() => HostWake.Shutdown();

    [Fact]
    public async Task BackgroundRequestsPostOnceUntilReset()
    {
        int posts = 0;
        HostWake.Initialize(() => Interlocked.Increment(ref posts), Environment.CurrentManagedThreadId);

        await Task.Run(() => { for (int i = 0; i < 20; i++) HostWake.Request(); }, TestContext.Current.CancellationToken);
        Assert.True(HostWake.IsPending);
        Assert.Equal(1, posts);

        HostWake.Reset();
        Assert.False(HostWake.IsPending);
        await Task.Run(HostWake.Request, TestContext.Current.CancellationToken);
        Assert.True(HostWake.IsPending);
        Assert.Equal(2, posts);
    }

    [Fact]
    public void UiThreadRequestMarksPendingWithoutPosting()
    {
        int posts = 0;
        HostWake.Initialize(() => posts++, Environment.CurrentManagedThreadId);

        HostWake.Request();

        Assert.True(HostWake.IsPending);
        Assert.Equal(0, posts);
    }

    [Fact]
    public void RequestsOutsideInitializedPeriodDoNotThrowOrPost()
    {
        int posts = 0;
        HostWake.Request();
        Assert.True(HostWake.IsPending);
        Assert.Equal(0, posts);

        HostWake.Initialize(() => posts++, Environment.CurrentManagedThreadId);
        HostWake.Shutdown();
        HostWake.Request();
        Assert.True(HostWake.IsPending);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task ConcurrentRequestsPostAtMostOncePerPeriod()
    {
        int posts = 0;
        HostWake.Initialize(() => Interlocked.Increment(ref posts), Environment.CurrentManagedThreadId);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(HostWake.Request)));

        Assert.True(HostWake.IsPending);
        Assert.Equal(1, posts);
    }
}
