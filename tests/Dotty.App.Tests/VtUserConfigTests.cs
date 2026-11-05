using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
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
        VtUserConfig defaults = new DottyUserConfig().Vt;
        Assert.False(defaults.ChecksumReportEnabled);
        Assert.False(defaults.TitleReportEnabled);
    }

    [Fact]
    public void Json_RoundTripsChecksumSwitch()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"vt\":{\"checksumReportEnabled\":true}}")!;
        Assert.True(config.Vt.ChecksumReportEnabled);

        string json = JsonSerializer.Serialize(config);
        using (JsonDocument document = JsonDocument.Parse(json))
            Assert.True(document.RootElement.GetProperty("vt").GetProperty("checksumReportEnabled").GetBoolean());
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

    [Fact]
    public void TitleReportConfigAppliesToQueriesAndCanBeRevokedOnReload()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"vt\":{\"titleReportEnabled\":true,\"checksumReportEnabled\":true}}")!;
        string json = JsonSerializer.Serialize(config);
        using (JsonDocument document = JsonDocument.Parse(json))
            Assert.True(document.RootElement.GetProperty("vt").GetProperty("titleReportEnabled").GetBoolean());
        Assert.True(JsonSerializer.Deserialize<DottyUserConfig>(json)!.Vt.TitleReportEnabled);
        var adapter = new TerminalAdapter(rows: 2, columns: 4);
        VtAdapterConfig.Apply(adapter, config.Vt);
        var parser = new BasicAnsiParser { Handler = adapter };
        var titleReplies = new List<byte[]>();
        var otherReplies = new List<string>();
        adapter.RawReplyBytesRequested += reply => titleReplies.Add(reply.ToArray());
        adapter.ReplyRequested += reply => otherReplies.Add(reply.ToString());

        Feed(parser, "\u001b]1;Icon title\u0007\u001b]2;Window title\u0007");
        Feed(parser, "\u001b[20t\u001b[21t\u001b[2J\u001b[HA\u001b[1;1;1;1;1;1*y\u001b[18t");

        Assert.Equal(2, titleReplies.Count);
        Assert.Equal(Encoding.Latin1.GetBytes("\u001b]LIcon title\u001b\\"), titleReplies[0]);
        Assert.Equal(Encoding.Latin1.GetBytes("\u001b]lWindow title\u001b\\"), titleReplies[1]);
        Assert.Equal(new[] { "\u001bP1!~0041\u001b\\", "\u001b[8;2;4t" }, otherReplies);

        _config.Vt.ChecksumReportEnabled = true;
        Assert.True(_host.ExecuteString("dotty.config.vt.title_report_enabled=true"));
        VtAdapterConfig.Apply(adapter, _config.Vt);
        Assert.True(_host.ExecuteString("dotty.config.apply_table({vt={title_report_enabled=false}})"));
        VtAdapterConfig.Apply(adapter, _config.Vt);

        titleReplies.Clear();
        otherReplies.Clear();
        Feed(parser, "\u001b[20t\u001b[21t\u001b[1;1;1;1;1;1*y\u001b[18t");
        Assert.Empty(titleReplies);
        Assert.Equal(new[] { "\u001bP1!~0041\u001b\\", "\u001b[8;2;4t" }, otherReplies);
    }

    private static void Feed(BasicAnsiParser parser, string input) =>
        parser.Feed(Encoding.ASCII.GetBytes(input));
}
