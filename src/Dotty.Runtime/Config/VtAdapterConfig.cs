using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Config;

/// <summary>Applies the <c>vt</c> config to terminal adapters.</summary>
public static class VtAdapterConfig
{
    /// <summary>Applies VT report permissions from configuration to an adapter.</summary>
    public static void Apply(TerminalAdapter adapter, VtUserConfig? config)
    {
        if (adapter == null) return;
        VtUserConfig vt = config ?? new VtUserConfig();
        adapter.ChecksumReportEnabled = vt.ChecksumReportEnabled;
        adapter.TitleReportEnabled = vt.TitleReportEnabled;
    }
}
