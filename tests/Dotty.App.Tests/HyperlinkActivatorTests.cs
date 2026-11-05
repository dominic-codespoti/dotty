using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkActivatorTests
{
    private sealed class RecordingOpener : IHyperlinkOpener
    {
        public List<HyperlinkTarget> Opened { get; } = new();

        public Task<HyperlinkOpenResult> OpenAsync(HyperlinkTarget target, CancellationToken ct = default)
        {
            Opened.Add(target);
            return Task.FromResult(HyperlinkOpenResult.Opened);
        }
    }

    private sealed class Harness
    {
        public List<(string Url, HyperlinkSource Source)> HookCalls { get; } = new();
        public Func<string, bool> Hook { get; set; } = _ => false;
        public RecordingOpener Opener { get; } = new();
        public HyperlinkOptions Options { get; set; } = HyperlinkOptions.Default;
        public HyperlinkActivator Activator { get; }

        public Harness()
        {
            Activator = new HyperlinkActivator(
                () => Options,
                (url, source) =>
                {
                    HookCalls.Add((url, source));
                    return Hook(url);
                },
                Opener,
                machineName: "devbox",
                isWindows: false);
        }
    }

    [Fact]
    public void InvalidLink_NeverReachesHook()
    {
        var h = new Harness { Hook = _ => true };

        var activation = h.Activator.Activate("https://exa\u0007mple.com/", HyperlinkSource.Explicit, "x");

        Assert.Equal(HyperlinkActivationKind.Blocked, activation.Kind);
        Assert.Empty(h.HookCalls);
        Assert.Equal(HyperlinkActivationKind.Blocked, h.Activator.Activate("/etc/passwd", HyperlinkSource.Detected, null).Kind);
        Assert.Equal(HyperlinkActivationKind.Blocked, h.Activator.Activate("javascript:alert(1)", HyperlinkSource.Detected, null).Kind);
        Assert.Empty(h.HookCalls);
    }

    [Fact]
    public void HookReturningTrue_HandlesLinkWithoutPolicyOrOpener()
    {
        // Uri may or may not append a trailing slash to an empty path for unregistered schemes.
        var h = new Harness { Hook = url => url.TrimEnd('/') == "dotty://handled" };

        var activation = h.Activator.Activate("dotty://handled", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkActivationKind.HandledByLua, activation.Kind);
        Assert.Null(activation.Target);
        Assert.Empty(h.Opener.Opened);
    }

    [Fact]
    public void HookReturningFalse_FallsThroughToPolicy()
    {
        var h = new Harness();

        Assert.Equal(HyperlinkActivationKind.Blocked,
            h.Activator.Activate("dotty://unhandled", HyperlinkSource.Detected, null).Kind);

        var open = h.Activator.Activate("https://example.com/x", HyperlinkSource.Detected, null);
        Assert.Equal(HyperlinkActivationKind.Open, open.Kind);
        Assert.Equal("https://example.com/x", open.Target!.Canonical);
        Assert.Equal(2, h.HookCalls.Count);
    }

    [Fact]
    public void HookThrowing_NeverBypassesPolicy()
    {
        var h = new Harness { Hook = _ => throw new InvalidOperationException("boom") };

        Assert.Equal(HyperlinkActivationKind.Blocked,
            h.Activator.Activate("dotty://x", HyperlinkSource.Detected, null).Kind);
        Assert.Equal(HyperlinkActivationKind.NeedsConfirmation,
            h.Activator.Activate("ssh://host/x", HyperlinkSource.Detected, null).Kind);
    }

    [Fact]
    public void HookReceivesCanonicalUrlAndSource()
    {
        var h = new Harness();

        h.Activator.Activate("HTTP://Example.COM/a b", HyperlinkSource.Explicit, "text");
        h.Activator.Activate("https://b\u00FCcher.de/", HyperlinkSource.Detected, null);

        Assert.Equal(("http://example.com/a%20b", HyperlinkSource.Explicit), h.HookCalls[0]);
        Assert.Equal(("https://xn--bcher-kva.de/", HyperlinkSource.Detected), h.HookCalls[1]);
    }

    [Fact]
    public void RiskyLinks_NeedConfirmationWithRisks()
    {
        var h = new Harness();

        var ssh = h.Activator.Activate("ssh://host/x", HyperlinkSource.Detected, null);
        Assert.Equal(HyperlinkActivationKind.NeedsConfirmation, ssh.Kind);
        Assert.Equal(HyperlinkRisk.NonWebScheme, ssh.Risks);

        var mismatch = h.Activator.Activate("https://evil.test/", HyperlinkSource.Explicit, "https://paypal.com");
        Assert.Equal(HyperlinkActivationKind.NeedsConfirmation, mismatch.Kind);
        Assert.Equal(HyperlinkRisk.TextMismatch, mismatch.Risks);

        var file = h.Activator.Activate("file:///tmp/a.txt", HyperlinkSource.Detected, null);
        Assert.Equal(HyperlinkActivationKind.NeedsConfirmation, file.Kind);
        Assert.Equal(HyperlinkRisk.LocalFile, file.Risks);
    }

    [Fact]
    public void Blocked_CarriesReason()
    {
        var h = new Harness();

        var activation = h.Activator.Activate("file:///tmp/a.exe", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkActivationKind.Blocked, activation.Kind);
        Assert.Contains("executable", activation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitLinks_AreBlockedWhenOsc8IsDisabled_ButDetectedStillWork()
    {
        var h = new Harness { Options = HyperlinkOptions.Default with { Osc8 = false } };

        var explicitLink = h.Activator.Activate("https://example.com/", HyperlinkSource.Explicit, "x");
        var detected = h.Activator.Activate("https://example.com/", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkActivationKind.Blocked, explicitLink.Kind);
        Assert.Equal(HyperlinkActivationKind.Open, detected.Kind);
        Assert.Single(h.HookCalls);
    }

    [Fact]
    public void Options_AreReadOnEveryActivation()
    {
        var h = new Harness();
        Assert.Equal(HyperlinkActivationKind.Open,
            h.Activator.Activate("https://example.com/", HyperlinkSource.Detected, null).Kind);

        h.Options = HyperlinkOptions.Default with { Confirm = HyperlinkConfirmMode.Always };

        Assert.Equal(HyperlinkActivationKind.NeedsConfirmation,
            h.Activator.Activate("https://example.com/", HyperlinkSource.Detected, null).Kind);
    }

    [Fact]
    public async Task OpenApprovedAsync_ForwardsTargetToOpener()
    {
        var h = new Harness();
        var activation = h.Activator.Activate("https://example.com/x", HyperlinkSource.Detected, null);

        var result = await h.Activator.OpenApprovedAsync(activation.Target!, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Same(activation.Target, Assert.Single(h.Opener.Opened));
    }
}
