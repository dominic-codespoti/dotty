using System;
using System.Collections.Generic;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Validates the canonical string the hyperlink policy hands to openers: it is ASCII and has no
/// characters that could break out of a Windows handler command line. The policy percent-encodes
/// those characters itself, so the first test does not depend on <see cref="Uri"/> escaping details.
/// The IDN/ASCII tests pin platform <see cref="Uri"/> behavior (verified on .NET 11): <see cref="Uri.AbsoluteUri"/>
/// keeps an internationalized host in Unicode, so the policy builds its canonical form from <see cref="Uri.IdnHost"/>.
/// </summary>
public sealed class UriCanonicalizationTests
{
    [Fact]
    public void PolicyCanonical_EscapesCommandLineBreakingCharacters()
    {
        var policy = new HyperlinkPolicy(HyperlinkOptions.Default, "devbox", isWindows: false);
        const string forbidden = " \"<>\\^`{|}";
        var failures = new List<string>();

        for (char c = ' '; c <= '~'; c++)
        {
            if (c is '#' or '?' or '%')
                continue;

            if (!policy.TryCanonicalize("https://example.com/a" + c + "b", out string? canonical, out _))
                continue;

            foreach (char bad in forbidden)
            {
                if (canonical.Contains(bad))
                    failures.Add($"U+{(int)c:X4} left '{bad}' unescaped: {canonical}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void AbsoluteUri_KeepsIdnHostInUnicodeAndIdnHostIsPunycode()
    {
        var uri = new Uri("https://b\u00FCcher.de/p");

        Assert.Equal("https://b\u00FCcher.de/p", uri.AbsoluteUri);
        Assert.Equal("xn--bcher-kva.de", uri.IdnHost);
    }

    [Fact]
    public void PolicyCanonical_UsesThePunycodeHost()
    {
        var policy = new HyperlinkPolicy(HyperlinkOptions.Default, "devbox", isWindows: false);

        Assert.True(policy.TryCanonicalize("https://user@b\u00FCcher.de:8443/a b?q=\u00E9", out string? canonical, out _));
        Assert.Equal("https://user@xn--bcher-kva.de:8443/a%20b?q=%C3%A9", canonical);
    }

    [Fact]
    public void AbsoluteUri_IsAlwaysAscii()
    {
        // needs-runtime-verification
        var uri = new Uri("https://example.com/\u4E2D\u6587?q=\u00E9#\u00F1");

        foreach (char c in uri.AbsoluteUri)
            Assert.True(c < 0x7F, $"non-ASCII char U+{(int)c:X4} in {uri.AbsoluteUri}");
    }
}
