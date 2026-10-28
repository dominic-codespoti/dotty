using System;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkTextMismatchTests
{
    [Theory]
    [InlineData("https://paypal.com", "https://evil.test/", true)]
    [InlineData("www.paypal.com", "https://evil.test/", true)]
    [InlineData("Go to https://paypal.com now", "https://evil.test/", true)]
    [InlineData("https://x.com", "https://xx.com/", true)]
    [InlineData("https://x.com.evil.test", "https://x.com/", true)]
    [InlineData("https://docs.x.com/a", "https://x.com/", false)]
    [InlineData("https://x.com", "https://docs.x.com/", false)]
    [InlineData("www.example.com", "https://example.com/", false)]
    [InlineData("https://example.com:8080/path", "https://example.com/other", false)]
    [InlineData("HTTPS://EXAMPLE.COM", "https://example.com/", false)]
    [InlineData("README.md", "https://evil.test/", false)]
    [InlineData("main.rs", "https://evil.test/", false)]
    [InlineData("paypal.com", "https://evil.test/", false)]
    [InlineData("click here", "https://evil.test/", false)]
    [InlineData("", "https://evil.test/", false)]
    [InlineData("   ", "https://evil.test/", false)]
    [InlineData("https://paypal.com", "mailto:a@evil.test", false)]
    [InlineData("https://paypal.com", "file:///tmp/a.txt", false)]
    public void IsMismatch_ComparesNamedHostsOnly(string text, string target, bool expected)
    {
        Assert.Equal(expected, HyperlinkTextMismatch.IsMismatch(text, new Uri(target)));
    }

    [Fact]
    public void IsMismatch_NullTextIsNotAMismatch()
    {
        Assert.False(HyperlinkTextMismatch.IsMismatch(null, new Uri("https://evil.test/")));
    }
}
