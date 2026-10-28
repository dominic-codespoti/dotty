using System.Text;
using Dotty.Runtime.Config;
using Dotty.Runtime.Hyperlinks;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkIngestConfigTests
{
    private static int LinkIdAfterOsc8(TerminalAdapter adapter)
    {
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes("\u001b]8;;https://example.test/\u001b\\hi\u001b]8;;\u001b\\"));
        return adapter.Buffer.GetColdCell(0, 0).HyperlinkId;
    }

    [Fact]
    public void Apply_Disabled_DoesNotStoreOsc8Links()
    {
        var adapter = new TerminalAdapter(rows: 5, columns: 40);
        HyperlinkIngestConfig.Apply(adapter, new HyperlinksUserConfig { Osc8 = false });

        Assert.False(adapter.Osc8HyperlinksEnabled);
        Assert.Equal(0, LinkIdAfterOsc8(adapter));
    }

    [Fact]
    public void Apply_Enabled_StoresOsc8Links()
    {
        var adapter = new TerminalAdapter(rows: 5, columns: 40);
        HyperlinkIngestConfig.Apply(adapter, new HyperlinksUserConfig { Osc8 = false });
        HyperlinkIngestConfig.Apply(adapter, new HyperlinksUserConfig { Osc8 = true });

        Assert.True(adapter.Osc8HyperlinksEnabled);
        Assert.NotEqual(0, LinkIdAfterOsc8(adapter));
    }
}
