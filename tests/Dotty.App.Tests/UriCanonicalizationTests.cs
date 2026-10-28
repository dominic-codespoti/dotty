using System;
using System.Collections.Generic;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Validates the platform <see cref="Uri"/> behavior the hyperlink policy relies on: the canonical
/// string handed to openers is ASCII and has no characters that could break out of a Windows
/// handler command line.
/// </summary>
public sealed class UriCanonicalizationTests
{
    [Fact]
    public void AbsoluteUri_EscapesCommandLineBreakingCharactersInPaths()
    {
        const string forbidden = " \"<>\\^`{|}";
        var failures = new List<string>();

        for (char c = ' '; c <= '~'; c++)
        {
            if (c is '#' or '?' or '%')
                continue;

            if (!Uri.TryCreate("https://example.com/a" + c + "b", UriKind.Absolute, out Uri? uri))
                continue;

            string canonical = uri.AbsoluteUri;
            foreach (char bad in forbidden)
            {
                if (canonical.Contains(bad))
                    failures.Add($"U+{(int)c:X4} left '{bad}' unescaped: {canonical}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void AbsoluteUri_CanonicalizesIdnHostToPunycode()
    {
        var uri = new Uri("https://b\u00FCcher.de/p");

        Assert.Equal("https://xn--bcher-kva.de/p", uri.AbsoluteUri);
        Assert.Equal("xn--bcher-kva.de", uri.IdnHost);
    }

    [Fact]
    public void AbsoluteUri_IsAlwaysAscii()
    {
        var uri = new Uri("https://example.com/\u4E2D\u6587?q=\u00E9#\u00F1");

        foreach (char c in uri.AbsoluteUri)
            Assert.True(c < 0x7F, $"non-ASCII char U+{(int)c:X4} in {uri.AbsoluteUri}");
    }
}
