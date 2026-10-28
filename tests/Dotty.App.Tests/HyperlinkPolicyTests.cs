using System;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkPolicyTests
{
    private const string Machine = "devbox";

    private static HyperlinkPolicy Policy(HyperlinkOptions? options = null, bool isWindows = false) =>
        new(options ?? HyperlinkOptions.Default, Machine, isWindows);

    private static HyperlinkOptions WithSchemes(params string[] schemes) =>
        HyperlinkOptions.Default with { AllowedSchemes = schemes };

    [Theory]
    [InlineData("https://example.com/x", HyperlinkVerdict.Open, HyperlinkRisk.None)]
    [InlineData("HTTP://Example.COM/a b", HyperlinkVerdict.Open, HyperlinkRisk.None)]
    [InlineData("https://[::1]:8080/", HyperlinkVerdict.Open, HyperlinkRisk.None)]
    [InlineData("https://xn--bcher-kva.de", HyperlinkVerdict.Confirm, HyperlinkRisk.Idn)]
    [InlineData("https://b\u00FCcher.de/", HyperlinkVerdict.Confirm, HyperlinkRisk.Idn)]
    [InlineData("https://paypal.com@evil.test/", HyperlinkVerdict.Confirm, HyperlinkRisk.Userinfo)]
    [InlineData("ssh://git@host/x", HyperlinkVerdict.Confirm, HyperlinkRisk.NonWebScheme)]
    [InlineData("mailto:a@b.test", HyperlinkVerdict.Confirm, HyperlinkRisk.NonWebScheme)]
    [InlineData("git://host/repo", HyperlinkVerdict.Confirm, HyperlinkRisk.NonWebScheme)]
    [InlineData("FILE:///tmp/a.TXT", HyperlinkVerdict.Confirm, HyperlinkRisk.LocalFile)]
    [InlineData("file://localhost/tmp/a.txt", HyperlinkVerdict.Confirm, HyperlinkRisk.LocalFile)]
    [InlineData("file://devbox/tmp/a.txt", HyperlinkVerdict.Confirm, HyperlinkRisk.LocalFile)]
    public void Evaluate_ClassifiesAllowedLinks(string url, HyperlinkVerdict verdict, HyperlinkRisk risks)
    {
        HyperlinkDecision decision = Policy().Evaluate(url, HyperlinkSource.Detected, null);

        Assert.Equal(verdict, decision.Verdict);
        Assert.Equal(risks, decision.Risks);
        Assert.NotNull(decision.Target);
        Assert.Null(decision.BlockReason);
    }

    [Theory]
    [InlineData("https://example.com:99999/", "not a valid URL")]
    [InlineData("https://\u202Eevil.test", "invisible")]
    [InlineData("javascript:alert(1)", "blocked")]
    [InlineData("data:text/html,<b>x</b>", "blocked")]
    [InlineData("vbscript:msgbox", "blocked")]
    [InlineData("ms-msdt:/id", "blocked")]
    [InlineData("vscode://file/tmp/x", "not allowed")]
    [InlineData("ftp://example.com/x", "not allowed")]
    [InlineData("file:///C:/Windows/System32/calc.exe", "executable")]
    [InlineData("file:///tmp/a.desktop", "executable")]
    [InlineData("file:///tmp/a.exe.txt", "executable")]
    [InlineData("file://evilhost/share/x.txt", "evilhost")]
    [InlineData("file://other/tmp/a.txt", "other")]
    [InlineData("file:///tmp/%0Ax", "control")]
    [InlineData("file:///tmp/%00x", "control")]
    [InlineData("\\\\host\\share\\x.exe", "not a URL")]
    [InlineData("/etc/passwd", "not a URL")]
    [InlineData("C:\\Windows\\System32\\calc.exe", "not a valid URL")]
    [InlineData("not a URL", "not a URL")]
    [InlineData("mailto:a@b.test?attach=/etc/passwd", "attachment")]
    [InlineData("mailto:a@b.test?subject=x&Attachment=/etc/passwd", "attachment")]
    public void Evaluate_BlocksUnsafeLinks(string url, string reasonFragment)
    {
        HyperlinkDecision decision = Policy().Evaluate(url, HyperlinkSource.Explicit, "text");

        Assert.Equal(HyperlinkVerdict.Block, decision.Verdict);
        Assert.Null(decision.Target);
        Assert.Contains(reasonFragment, decision.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_BlocksEmptyAndNull()
    {
        Assert.Equal(HyperlinkVerdict.Block, Policy().Evaluate(null, HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, Policy().Evaluate("", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, Policy().Evaluate("   ", HyperlinkSource.Detected, null).Verdict);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)]
    [InlineData(0x0A)]
    [InlineData(0x1B)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    public void Evaluate_BlocksControlCharactersInsteadOfStrippingThem(int code)
    {
        string url = "https://goo" + (char)code + "gle.com/";

        HyperlinkDecision decision = Policy().Evaluate(url, HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Block, decision.Verdict);
        Assert.Contains("control", decision.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_BlocksOverlongLinks()
    {
        string url = "https://e.test/" + new string('a', HyperlinkUri.MaxLength);

        HyperlinkDecision decision = Policy().Evaluate(url, HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Block, decision.Verdict);
        Assert.Contains("too long", decision.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_CanonicalizesToAsciiAndDoesNotAlterHosts()
    {
        HyperlinkDecision spaces = Policy().Evaluate("HTTP://Example.COM/a b", HyperlinkSource.Detected, null);
        Assert.Equal("http://example.com/a%20b", spaces.Target!.Canonical);
        Assert.Equal("http", spaces.Target.Scheme);
        Assert.Equal("example.com", spaces.Target.Host);

        HyperlinkDecision idn = Policy().Evaluate("https://b\u00FCcher.de/", HyperlinkSource.Detected, null);
        Assert.Equal("https://xn--bcher-kva.de/", idn.Target!.Canonical);
        Assert.Equal("xn--bcher-kva.de", idn.Target.Host);
    }

    [Theory]
    [InlineData("git@github.com:o/r.git", "https://github.com/o/r")]
    [InlineData("git@github.com:o/r", "https://github.com/o/r")]
    [InlineData("git@gitlab.example.com:group/sub/repo.git", "https://gitlab.example.com/group/sub/repo")]
    public void Evaluate_NormalizesScpStyleGitLinks(string url, string canonical)
    {
        HyperlinkDecision decision = Policy().Evaluate(url, HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Open, decision.Verdict);
        Assert.Equal(canonical, decision.Target!.Canonical);
    }

    [Fact]
    public void Evaluate_RejectsMalformedScpLinks()
    {
        Assert.Equal(HyperlinkVerdict.Block, Policy().Evaluate("git@github.com:", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, Policy().Evaluate("git@:o/r", HyperlinkSource.Detected, null).Verdict);
    }

    [Fact]
    public void Evaluate_FileLinkCarriesDecodedLocalPath()
    {
        HyperlinkDecision decision = Policy().Evaluate("file:///tmp/a%20b.txt", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Confirm, decision.Verdict);
        Assert.Equal("/tmp/a b.txt", decision.Target!.LocalPath);
        Assert.Equal("file:///tmp/a%20b.txt", decision.Target.Canonical);
    }

    [Theory]
    [InlineData("file://127.0.0.1/c$/Windows/System32/calc.exe")]
    [InlineData("file://evilhost/share/x.txt")]
    [InlineData("file:////host/share/x.txt")]
    [InlineData("file:///C:/x.txt:evil")]
    [InlineData("file:///C:/dir/x.exe.")]
    [InlineData("file:///C:/dir/CALC.EXE%20")]
    [InlineData("file:///C:/dir/x.lnk")]
    public void Evaluate_WindowsBlocksUncStreamsAndExecutables(string url)
    {
        HyperlinkDecision decision = Policy(isWindows: true).Evaluate(url, HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Block, decision.Verdict);
    }

    [Fact]
    public void Evaluate_WindowsFileLinkUsesBackslashLocalPath()
    {
        HyperlinkDecision decision = Policy(isWindows: true).Evaluate("file:///C:/dir/ok.txt", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Confirm, decision.Verdict);
        Assert.Equal("C:\\dir\\ok.txt", decision.Target!.LocalPath);
    }

    [Fact]
    public void Evaluate_WindowsRejectsOverlongShellLinks()
    {
        string url = "https://e.test/" + new string('a', 2100);

        Assert.Equal(HyperlinkVerdict.Block, Policy(isWindows: true).Evaluate(url, HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Open, Policy(isWindows: false).Evaluate(url, HyperlinkSource.Detected, null).Verdict);
    }

    [Fact]
    public void Evaluate_ConfirmNeverStillAppliesHardBlocks()
    {
        var policy = Policy(HyperlinkOptions.Default with { Confirm = HyperlinkConfirmMode.Never });

        Assert.Equal(HyperlinkVerdict.Open, policy.Evaluate("ssh://h/x", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Open, policy.Evaluate("file:///tmp/a.txt", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("javascript:alert(1)", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("file:///tmp/a.exe", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("https://a\u0000b/", HyperlinkSource.Detected, null).Verdict);
    }

    [Fact]
    public void Evaluate_ConfirmAlwaysPromptsPlainHttps()
    {
        var policy = Policy(HyperlinkOptions.Default with { Confirm = HyperlinkConfirmMode.Always });

        HyperlinkDecision decision = policy.Evaluate("https://example.com/", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Confirm, decision.Verdict);
        Assert.Equal(HyperlinkRisk.ConfirmAlways, decision.Risks);
    }

    [Fact]
    public void Evaluate_CustomSchemeRequiresAllowlistAndAlwaysConfirms()
    {
        Assert.Equal(HyperlinkVerdict.Block,
            Policy().Evaluate("vscode://file/tmp/x", HyperlinkSource.Detected, null).Verdict);

        HyperlinkDecision allowed = Policy(WithSchemes("https", "vscode"))
            .Evaluate("vscode://file/tmp/x", HyperlinkSource.Detected, null);

        Assert.Equal(HyperlinkVerdict.Confirm, allowed.Verdict);
        Assert.Equal(HyperlinkRisk.NonWebScheme, allowed.Risks);
    }

    [Fact]
    public void Evaluate_HardDenyBeatsAllowlist()
    {
        var policy = Policy(WithSchemes("https", "javascript", "data", "ms-msdt"));

        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("javascript:alert(1)", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("data:text/plain,hi", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("ms-msdt:/id", HyperlinkSource.Detected, null).Verdict);
    }

    [Fact]
    public void Evaluate_AllowlistIsCaseInsensitiveAndRestrictive()
    {
        var policy = Policy(WithSchemes("HTTPS"));

        Assert.Equal(HyperlinkVerdict.Open, policy.Evaluate("https://example.com/", HyperlinkSource.Detected, null).Verdict);
        Assert.Equal(HyperlinkVerdict.Block, policy.Evaluate("http://example.com/", HyperlinkSource.Detected, null).Verdict);
    }

    [Fact]
    public void Evaluate_ExplicitTextMismatchRequiresConfirmation()
    {
        HyperlinkDecision mismatch = Policy().Evaluate(
            "https://evil.test/", HyperlinkSource.Explicit, "https://paypal.com");
        Assert.Equal(HyperlinkVerdict.Confirm, mismatch.Verdict);
        Assert.Equal(HyperlinkRisk.TextMismatch, mismatch.Risks);

        HyperlinkDecision matching = Policy().Evaluate(
            "https://example.com/x", HyperlinkSource.Explicit, "https://example.com");
        Assert.Equal(HyperlinkVerdict.Open, matching.Verdict);

        HyperlinkDecision plainText = Policy().Evaluate(
            "https://example.com/x", HyperlinkSource.Explicit, "click here");
        Assert.Equal(HyperlinkVerdict.Open, plainText.Verdict);

        HyperlinkDecision detected = Policy().Evaluate(
            "https://evil.test/", HyperlinkSource.Detected, "https://paypal.com");
        Assert.Equal(HyperlinkVerdict.Open, detected.Verdict);
    }

    [Fact]
    public void TryCanonicalize_IsStructuralOnly()
    {
        var policy = Policy();

        Assert.True(policy.TryCanonicalize("dotty://handled", out string? canonical, out _));
        Assert.Equal("dotty://handled", canonical);

        Assert.False(policy.TryCanonicalize("javascript:alert(1)", out _, out string? reason));
        Assert.Contains("blocked", reason, StringComparison.OrdinalIgnoreCase);

        Assert.False(policy.TryCanonicalize("https://a\nb/", out _, out reason));
        Assert.False(policy.TryCanonicalize("/etc/passwd", out _, out reason));
        Assert.False(policy.TryCanonicalize(null, out _, out reason));
    }

    [Fact]
    public void Options_FromConfig_NormalizesConfirmAndFallsBackToRisky()
    {
        Assert.Equal(HyperlinkConfirmMode.Always, HyperlinkOptions.From(new() { Confirm = " Always " }).Confirm);
        Assert.Equal(HyperlinkConfirmMode.Never, HyperlinkOptions.From(new() { Confirm = "NEVER" }).Confirm);
        Assert.Equal(HyperlinkConfirmMode.Risky, HyperlinkOptions.From(new() { Confirm = "risky" }).Confirm);
        Assert.Equal(HyperlinkConfirmMode.Risky, HyperlinkOptions.From(new() { Confirm = "bogus" }).Confirm);
        Assert.Equal(HyperlinkConfirmMode.Risky, HyperlinkOptions.From(null).Confirm);
        Assert.False(HyperlinkOptions.From(new() { Osc8 = false }).Osc8);
        Assert.Equal(new[] { "https" }, HyperlinkOptions.From(new() { AllowedSchemes = ["https"] }).AllowedSchemes);
    }
}
