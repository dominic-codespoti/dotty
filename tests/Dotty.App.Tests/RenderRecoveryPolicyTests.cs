using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class RenderRecoveryPolicyTests
{
    private const long Soon = 100;
    private const long Quiet = RenderRecoveryPolicy.HealthyPeriodMs;

    [Fact]
    public void StartsHealthy()
    {
        var policy = new RenderRecoveryPolicy();

        Assert.Equal(RenderRecoveryState.Healthy, policy.State);
        Assert.Equal(0, policy.FailureCount);
    }

    [Fact]
    public void RepeatedFailuresRetryOnceRebuildOnceThenGiveUpToTheUser()
    {
        var policy = new RenderRecoveryPolicy();

        Assert.Equal(RenderRecoveryAction.Retry, policy.OnRenderFailure(1_000));
        Assert.Equal(RenderRecoveryState.Retrying, policy.State);
        Assert.Equal(RenderRecoveryAction.Rebuild, policy.OnRenderFailure(1_000 + Soon));
        Assert.Equal(RenderRecoveryState.Rebuilt, policy.State);
        Assert.Equal(RenderRecoveryAction.Fatal, policy.OnRenderFailure(1_000 + 2 * Soon));
        Assert.Equal(RenderRecoveryState.Fatal, policy.State);
        // Further failures stay fatal (the host shows its prompt only once).
        Assert.Equal(RenderRecoveryAction.Fatal, policy.OnRenderFailure(1_000 + 3 * Soon));
        Assert.Equal(4, policy.FailureCount);
    }

    [Theory]
    // a frame presented this long after the failure -> state
    [InlineData(Soon, RenderRecoveryState.Retrying)]
    [InlineData(Quiet - 1, RenderRecoveryState.Retrying)]
    [InlineData(Quiet, RenderRecoveryState.Healthy)]
    public void PresentedFramesEndARecoveryOnlyAfterAQuietPeriod(long presentedAfter, RenderRecoveryState expected)
    {
        var policy = new RenderRecoveryPolicy();
        policy.OnRenderFailure(5_000);

        policy.OnFramePresented(5_000 + presentedAfter);

        Assert.Equal(expected, policy.State);
    }

    [Fact]
    public void FailuresFarApartNeverEscalate()
    {
        var policy = new RenderRecoveryPolicy();
        long now = 0;
        for (int i = 0; i < 5; i++)
        {
            now += Quiet;
            Assert.Equal(RenderRecoveryAction.Retry, policy.OnRenderFailure(now));
        }
    }

    [Fact]
    public void RebuiltStateAlsoExpiresAfterAQuietPeriod()
    {
        var policy = new RenderRecoveryPolicy();
        policy.OnRenderFailure(0);
        policy.OnRenderFailure(Soon);

        Assert.Equal(RenderRecoveryAction.Retry, policy.OnRenderFailure(Soon + Quiet));
    }

    [Fact]
    public void FatalIsStickyUntilTheUserResets()
    {
        var policy = new RenderRecoveryPolicy();
        policy.OnRenderFailure(0);
        policy.OnRenderFailure(1);
        policy.OnRenderFailure(2);

        policy.OnFramePresented(2 + 10 * Quiet);
        Assert.Equal(RenderRecoveryState.Fatal, policy.State);
        Assert.Equal(RenderRecoveryAction.Fatal, policy.OnRenderFailure(3 + 10 * Quiet));

        policy.Reset();
        Assert.Equal(RenderRecoveryState.Healthy, policy.State);
        Assert.Equal(RenderRecoveryAction.Retry, policy.OnRenderFailure(4 + 10 * Quiet));
    }

    [Theory]
    // exception failures before the lost context (0: Healthy, 1: Retrying, 2: Rebuilt, 3: Fatal)
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ALostContextStopsRenderingFromAnyState(int priorFailures)
    {
        var policy = new RenderRecoveryPolicy();
        for (int i = 0; i < priorFailures; i++)
            policy.OnRenderFailure(i);

        Assert.Equal(RenderRecoveryAction.StopRendering, policy.OnContextLost(priorFailures));
        Assert.Equal(RenderRecoveryState.ContextLost, policy.State);
        Assert.Equal(priorFailures + 1, policy.FailureCount);
    }

    [Fact]
    public void ContextLostIsTerminal()
    {
        var policy = new RenderRecoveryPolicy();
        policy.OnContextLost(0);

        // Neither later failures, a quiet period nor the prompt's reset leave it: only a restart draws again.
        Assert.Equal(RenderRecoveryAction.StopRendering, policy.OnRenderFailure(Soon));
        policy.OnFramePresented(Soon + 10 * Quiet);
        Assert.Equal(RenderRecoveryAction.StopRendering, policy.OnRenderFailure(Soon + 10 * Quiet));
        policy.Reset();
        Assert.Equal(RenderRecoveryState.ContextLost, policy.State);
        Assert.Equal(RenderRecoveryAction.StopRendering, policy.OnContextLost(2 * Soon + 10 * Quiet));
        Assert.Equal(4, policy.FailureCount);
    }
}
