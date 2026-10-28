using System.Text.Json;
using Dotty.Runtime.Config;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinksUserConfigTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var config = new DottyUserConfig().Hyperlinks;

        Assert.True(config.Osc8);
        Assert.Equal("risky", config.Confirm);
        Assert.Equal(new[] { "http", "https", "mailto", "file", "ssh", "git" }, config.AllowedSchemes);
        Assert.Equal(HyperlinkConfirmMode.Risky, HyperlinkOptions.From(config).Confirm);
    }

    [Fact]
    public void Json_RoundTripsAllSettings()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"hyperlinks\":{\"osc8\":false,\"confirm\":\"Always\",\"allowedSchemes\":[\"https\",\"vscode\"]}}")!;

        Assert.False(config.Hyperlinks.Osc8);
        Assert.Equal("Always", config.Hyperlinks.Confirm);
        Assert.Equal(new[] { "https", "vscode" }, config.Hyperlinks.AllowedSchemes);

        HyperlinkOptions options = HyperlinkOptions.From(config.Hyperlinks);
        Assert.False(options.Osc8);
        Assert.Equal(HyperlinkConfirmMode.Always, options.Confirm);
    }

    [Fact]
    public void Json_UnknownConfirmFallsBackToRisky()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"hyperlinks\":{\"confirm\":\"sometimes\"}}")!;

        Assert.Equal(HyperlinkConfirmMode.Risky, HyperlinkOptions.From(config.Hyperlinks).Confirm);
    }
}
