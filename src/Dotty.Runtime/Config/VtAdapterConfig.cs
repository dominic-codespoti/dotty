using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Config;

/// <summary>Applies the <c>vt</c> config to terminal adapters.</summary>
public static class VtAdapterConfig
{
    /// <summary>Sets whether the adapter answers DECRQCRA (<c>vt.checksumReportEnabled</c>).</summary>
    public static void Apply(TerminalAdapter adapter, VtUserConfig? config)
    {
        if (adapter == null) return;
        adapter.ChecksumReportEnabled = (config ?? new VtUserConfig()).ChecksumReportEnabled;
    }
}
