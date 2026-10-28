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
}
