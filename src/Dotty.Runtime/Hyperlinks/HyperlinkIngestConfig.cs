using Dotty.Runtime.Config;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>Applies the <c>hyperlinks</c> config to terminal adapters (OSC 8 ingest switch).</summary>
public static class HyperlinkIngestConfig
{
    /// <summary>Sets whether the adapter stores OSC 8 hyperlinks (<c>hyperlinks.osc8</c>).</summary>
    public static void Apply(TerminalAdapter adapter, HyperlinksUserConfig? config)
    {
        if (adapter == null) return;
        adapter.Osc8HyperlinksEnabled = (config ?? new HyperlinksUserConfig()).Osc8;
    }
}
