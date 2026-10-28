using System;
using System.Text.Json;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>The <c>vt</c> config section: defaults, JSON, Lua and adapter application.</summary>
public sealed class VtUserConfigTests : IDisposable
{
    private readonly DottyUserConfig _config = new();
    private readonly TerminalTabManager _tabManager = new(SilentPty.Create);
    private readonly LuaScriptHost _host;

    public VtUserConfigTests()
    {
        _host = new LuaScriptHost(new FakeLuaHostServices(_tabManager), _tabManager);
        Assert.True(_host.Evaluate(_config, null));
    }

    public void Dispose()
    {
        _host.Dispose();
        _tabManager.Dispose();
    }

    [Fact]
    public void Defaults_AreOff()
    {
        Assert.False(new DottyUserConfig().Vt.ChecksumReportEnabled);
    }

    [Fact]
    public void Json_RoundTripsChecksumSwitch()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>("{\"vt\":{\"checksumReportEnabled\":true}}")!;
        Assert.True(config.Vt.ChecksumReportEnabled);

        string json = JsonSerializer.Serialize(config);
        Assert.Contains("\"vt\":{\"checksumReportEnabled\":true}", json);
        Assert.True(JsonSerializer.Deserialize<DottyUserConfig>(json)!.Vt.ChecksumReportEnabled);
    }

    [Fact]
    public void Lua_SetsChecksumSwitch()
    {
        Assert.True(_host.ExecuteString("dotty.config.vt.checksum_report_enabled=true; assert(dotty.config.vt.checksum_report_enabled==true)"));
        Assert.True(_config.Vt.ChecksumReportEnabled);

        Assert.True(_host.ExecuteString("dotty.config.apply_table({vt={checksum_report_enabled=false}})"));
        Assert.False(_config.Vt.ChecksumReportEnabled);
    }

    [Fact]
    public void Apply_TogglesAdapterGate()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 4);
        Assert.False(adapter.ChecksumReportEnabled);

        VtAdapterConfig.Apply(adapter, new VtUserConfig { ChecksumReportEnabled = true });
        Assert.True(adapter.ChecksumReportEnabled);

        VtAdapterConfig.Apply(adapter, null);
        Assert.False(adapter.ChecksumReportEnabled);
    }
}
