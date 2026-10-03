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
}
