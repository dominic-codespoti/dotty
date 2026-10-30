using System.Collections.Generic;
using System.Text.Json;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Bell rate limit, effect selection, flash envelope and pacing (<see cref="BellPolicy"/>), and the <c>bell</c> config section.</summary>
public sealed class BellPolicyTests
{
    [Fact]
    public void FirstBellIsAccepted()
    {
        long last = BellPolicy.Never;
        Assert.True(BellPolicy.TryAccept(ref last, 5_000));
        Assert.Equal(5_000, last);
    }

    [Theory]
    [InlineData(1_000L, 1_000L, false)] // same instant
    [InlineData(1_000L, 1_149L, false)] // just inside the interval
    [InlineData(1_000L, 1_150L, true)]  // exactly one interval later
    [InlineData(1_000L, 9_000L, true)]
    [InlineData(1_000L, 900L, true)]    // clock stepped backwards: never suppress until it catches up
    public void RateLimitAcceptsOncePerInterval(long lastAccepted, long now, bool accepted)
    {
        long last = lastAccepted;
        Assert.Equal(accepted, BellPolicy.TryAccept(ref last, now));
        Assert.Equal(accepted ? now : lastAccepted, last);
    }

    [Fact]
    public void BellFloodProducesOneEffectPerInterval()
    {
        long last = BellPolicy.Never;
        var accepted = new List<long>();
        for (long now = 0; now <= 400; now += 10)
        {
            if (BellPolicy.TryAccept(ref last, now))
                accepted.Add(now);
        }

        Assert.Equal(new long[] { 0, 150, 300 }, accepted);
    }

    [Theory]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, true, true, false, true)]
    public void AttentionOnlyWhenUnfocusedFlashWheneverVisual(bool attention, bool visual, bool focused, bool expectAttention, bool expectFlash)
    {
        BellEffects effects = BellPolicy.Decide(attention, visual, focused);
        Assert.Equal(expectAttention, (effects & BellEffects.Attention) != 0);
        Assert.Equal(expectFlash, (effects & BellEffects.Flash) != 0);
        Assert.Equal(BellEffects.None, effects & ~(BellEffects.Attention | BellEffects.Flash));
    }

    [Theory]
    [InlineData(-1L, 0f)]
    [InlineData(0L, BellPolicy.FlashPeakAlpha)]
    [InlineData(75L, BellPolicy.FlashPeakAlpha * 0.25f)]
    [InlineData(150L, 0f)]
    [InlineData(10_000L, 0f)]
    public void FlashEnvelopeFadesFromPeakToZero(long elapsedMs, float expected)
    {
        Assert.Equal(expected, BellPolicy.FlashAlpha(elapsedMs), 5);
    }

    [Fact]
    public void FlashEnvelopeIsVisibleUntilItsEndAndNeverRises()
    {
        float previous = BellPolicy.FlashAlpha(0);
        for (long t = 1; t < BellPolicy.FlashDurationMs; t++)
        {
            float alpha = BellPolicy.FlashAlpha(t);
            Assert.True(alpha > 0f, $"alpha at {t} ms");
            Assert.True(alpha <= previous, $"alpha rose at {t} ms");
            previous = alpha;
        }
    }

    [Theory]
    [InlineData(1_000L, 990L, 1_016L)]   // before the start: first frame boundary
    [InlineData(1_000L, 1_000L, 1_016L)]
    [InlineData(1_000L, 1_015L, 1_016L)]
    [InlineData(1_000L, 1_016L, 1_032L)]
    [InlineData(1_000L, 1_140L, 1_144L)]
    [InlineData(1_000L, 1_145L, 1_150L)] // capped at the end: the clearing frame is never skipped
    [InlineData(1_000L, 1_150L, BellPolicy.Never)]
    [InlineData(1_000L, 2_000L, BellPolicy.Never)]
    public void FlashFramesArePacedAndEndOnTheClearingFrame(long start, long now, long expected)
    {
        Assert.Equal(expected, BellPolicy.NextFlashFrameMs(start, now));
    }

    [Fact]
    public void Json_ReadsBellKeysAndKeepsOmittedDefaults()
    {
        var c = JsonSerializer.Deserialize<DottyUserConfig>("{\"bell\":{\"attention\":false,\"visual\":true,\"audible\":true}}")!;
        Assert.False(c.Bell.Attention);
        Assert.True(c.Bell.Visual);
        Assert.True(c.Bell.Audible);

        var partial = JsonSerializer.Deserialize<DottyUserConfig>("{\"bell\":{\"visual\":true}}")!;
        Assert.True(partial.Bell.Attention);
        Assert.True(partial.Bell.Visual);
        Assert.False(partial.Bell.Audible);
    }

    [Fact]
    public void Lua_SetsAndReadsBellKeys()
    {
        var config = new DottyUserConfig();
        using var tabManager = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(tabManager), tabManager);
        Assert.True(host.Evaluate(config, null));

        Assert.True(host.ExecuteString("dotty.config.bell.visual=true; assert(dotty.config.bell.visual==true)"));
        Assert.True(config.Bell.Visual);
        Assert.True(host.ExecuteString("dotty.config.apply_table({bell={attention=false,audible=true}})"));
        Assert.False(config.Bell.Attention);
        Assert.True(config.Bell.Audible);
    }
}
