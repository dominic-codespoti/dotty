using Dotty.Runtime.Clipboard;
using System.Text.Json;
using Dotty.Runtime.Config;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClipboardUserConfigTests
{
    [Fact]
    public void Osc52WritePermissionDefaultsToDeniedAndUsesJsonSetting()
    {
        var defaults = new DottyUserConfig();
        Assert.False(defaults.Clipboard.AllowOsc52Write);

        DottyUserConfig enabled = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"allowOsc52Write\":true}}")!;
        Assert.True(enabled.Clipboard.AllowOsc52Write);
    }

    [Fact]
    public void PasteSafetySettingsDefaultToTrueAndDeserialize()
    {
        var defaults = new DottyUserConfig();
        Assert.True(defaults.Clipboard.ConfirmPaste);
        Assert.True(defaults.Clipboard.BracketedPasteIsSafe);

        DottyUserConfig missing = JsonSerializer.Deserialize<DottyUserConfig>("{}")!;
        Assert.True(missing.Clipboard.ConfirmPaste);
        Assert.True(missing.Clipboard.BracketedPasteIsSafe);

        DottyUserConfig off = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"confirmPaste\":false,\"bracketedPasteIsSafe\":false}}")!;
        Assert.False(off.Clipboard.ConfirmPaste);
        Assert.False(off.Clipboard.BracketedPasteIsSafe);
    }

    [Fact]
    public void Osc52ReadDefaultsToDenyWithOneMebibyteLimit()
    {
        ClipboardUserConfig defaults = new DottyUserConfig().Clipboard;
        Assert.Equal("deny", defaults.Osc52Read);
        Assert.Equal(ClipboardReadMode.Deny, ClipboardReadModes.Parse(defaults.Osc52Read));
        Assert.Equal(1_048_576, defaults.Osc52ReadLimitBytes);
        Assert.Equal(ClipboardUserConfig.DefaultOsc52ReadLimitBytes, defaults.Osc52ReadLimitBytes);

        ClipboardUserConfig missing = JsonSerializer.Deserialize<DottyUserConfig>("{\"clipboard\":{}}")!.Clipboard;
        Assert.Equal("deny", missing.Osc52Read);
        Assert.Equal(1_048_576, missing.Osc52ReadLimitBytes);
    }

    [Fact]
    public void Osc52ReadSettingsDeserializeAndRoundTrip()
    {
        ClipboardUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"osc52Read\":\"ask\",\"osc52ReadLimitBytes\":4096}}")!.Clipboard;
        Assert.Equal("ask", config.Osc52Read);
        Assert.Equal(4096, config.Osc52ReadLimitBytes);

        string json = JsonSerializer.Serialize(config);
        Assert.Contains("\"osc52Read\":\"ask\"", json);
        Assert.Contains("\"osc52ReadLimitBytes\":4096", json);

        ClipboardUserConfig again = JsonSerializer.Deserialize<ClipboardUserConfig>(json)!;
        Assert.Equal("ask", again.Osc52Read);
        Assert.Equal(4096, again.Osc52ReadLimitBytes);
    }

    [Theory]
    [InlineData("deny", ClipboardReadMode.Deny)]
    [InlineData("ask", ClipboardReadMode.Ask)]
    [InlineData("allow", ClipboardReadMode.Allow)]
    [InlineData("ASK", ClipboardReadMode.Ask)]
    [InlineData("Allow", ClipboardReadMode.Allow)]
    [InlineData("DENY", ClipboardReadMode.Deny)]
    [InlineData("  ask  ", ClipboardReadMode.Ask)]
    [InlineData("", ClipboardReadMode.Deny)]
    [InlineData("   ", ClipboardReadMode.Deny)]
    [InlineData("yes", ClipboardReadMode.Deny)]
    [InlineData("true", ClipboardReadMode.Deny)]
    [InlineData("asks", ClipboardReadMode.Deny)]
    [InlineData("allow ask", ClipboardReadMode.Deny)]
    [InlineData(null, ClipboardReadMode.Deny)]
    public void ReadModeParsingIsCaseInsensitiveAndFailsClosed(string? text, ClipboardReadMode expected)
    {
        Assert.Equal(expected, ClipboardReadModes.Parse(text));
    }

    [Fact]
    public void Osc52ReadUnknownOrNullJsonValueMeansDeny()
    {
        ClipboardUserConfig unknown = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"osc52Read\":\"maybe\"}}")!.Clipboard;
        Assert.Equal(ClipboardReadMode.Deny, ClipboardReadModes.Parse(unknown.Osc52Read));

        ClipboardUserConfig nulled = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"osc52Read\":null}}")!.Clipboard;
        Assert.Equal(ClipboardReadMode.Deny, ClipboardReadModes.Parse(nulled.Osc52Read));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(4096, 4096)]
    [InlineData(8 * 1024 * 1024, 8 * 1024 * 1024)]
    [InlineData(8 * 1024 * 1024 + 1, 8 * 1024 * 1024)]
    [InlineData(int.MaxValue, 8 * 1024 * 1024)]
    public void Osc52ReadLimitIsClampedToSupportedRange(int configured, int expected)
    {
        var config = new ClipboardUserConfig { Osc52ReadLimitBytes = configured };
        Assert.Equal(expected, config.Osc52ReadLimitBytes);

        ClipboardUserConfig fromJson = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"clipboard\":{\"osc52ReadLimitBytes\":" + configured + "}}")!.Clipboard;
        Assert.Equal(expected, fromJson.Osc52ReadLimitBytes);
    }
}
