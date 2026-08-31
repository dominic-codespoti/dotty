using System.Threading.Tasks;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkHandlerTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("file:///tmp/example.txt")]
    [InlineData("mailto:user@example.com")]
    public void CanOpen_AllowsSupportedSchemes(string url)
    {
        var handler = new DefaultHyperlinkHandler();

        Assert.True(handler.CanOpen(url));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a URL")]
    [InlineData("")]
    public void CanOpen_RejectsUnsupportedOrMalformedSchemes(string url)
    {
        var handler = new DefaultHyperlinkHandler();

        Assert.False(handler.CanOpen(url));
    }

    [Fact]
    public async Task OpenUrlAsync_RejectsInvalidInputWithDiagnostic()
    {
        var handler = new DefaultHyperlinkHandler();

        Assert.False(await handler.OpenUrlAsync("javascript:alert(1)"));
        Assert.NotNull(handler.LastError);
    }

    [Fact]
    public void SanitizeUrl_RemovesControlCharacters()
    {
        Assert.Equal("https://example.com/path", DefaultHyperlinkHandler.SanitizeUrl("https://example.com/\u0000path"));
    }
}
