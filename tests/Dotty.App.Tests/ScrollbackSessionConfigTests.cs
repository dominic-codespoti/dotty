using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

[CollectionDefinition("Scrollback config", DisableParallelization = true)]
public sealed class ScrollbackConfigCollection
{
}

/// <summary>The <c>scrollback</c> config section: JSON, defaults, Lua and session plumbing.</summary>
[Collection("Scrollback config")]
public sealed class ScrollbackSessionConfigTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dotty-scrollback-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        UserConfigService.Shutdown();
        Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", _originalHome);
        UserConfigService.Load();
        UserConfigService.Shutdown();
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    private void LoadConfig(string? json)
    {
        UserConfigService.Shutdown();
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", _home);
        if (json is not null)
            File.WriteAllText(Path.Combine(_home, "config.json"), json);
        UserConfigService.Load();
    }

    private static int CapacityOfNewSession(int rows = 24, int columns = 80)
    {
        using var session = new TerminalSession(rows, columns, SilentPty.Create);
        return session.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity;
    }

    [Fact]
    public void Defaults_AreFiveThousandLinesAnd64MiB()
    {
        ScrollbackUserConfig defaults = new DottyUserConfig().Scrollback;
        Assert.Equal(5000, defaults.LimitLines);
        Assert.Equal(67_108_864L, defaults.LimitBytes);
        Assert.Equal(ScrollbackPolicy.Default, defaults.ToPolicy());
    }

    [Fact]
    public void Json_RoundTrips()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"scrollback\":{\"limitLines\":1000,\"limitBytes\":2097152}}")!;
        Assert.Equal(1000, config.Scrollback.LimitLines);
        Assert.Equal(2_097_152L, config.Scrollback.LimitBytes);

        DottyUserConfig missing = JsonSerializer.Deserialize<DottyUserConfig>("{}")!;
        Assert.Equal(5000, missing.Scrollback.LimitLines);
    }

    [Fact]
    public void NewSession_UsesConfiguredLimitLines()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":1000}}");
        Assert.Equal(1000, CapacityOfNewSession());
    }

    [Fact]
    public void NewSession_WithZeroLines_HasNoHistoryAndDoesNotThrow()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":0}}");
        using var session = new TerminalSession(4, 20, SilentPty.Create);
        Assert.Equal(0, session.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);

        var text = new StringBuilder();
        for (int i = 0; i < 50; i++)
            text.Append("line ").Append(i).Append("\r\n");
        session.Parser.Feed(Encoding.ASCII.GetBytes(text.ToString()));
        Assert.Equal(0, session.Adapter.Buffer.ScrollbackCount);
    }

    [Fact]
    public void NewSession_NegativeOrTooSmallValues_FallBackToDefaults()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":-5,\"limitBytes\":-1}}");
        Assert.Equal(DottyDefaults.ScrollbackLines, CapacityOfNewSession());

        LoadConfig("{\"scrollback\":{\"limitBytes\":1000}}");
        Assert.Equal(DottyDefaults.ScrollbackLines, CapacityOfNewSession());
    }

    [Fact]
    public void NewSession_ByteLimitCapsCapacityByColumns()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":100000,\"limitBytes\":1048576}}");
        Assert.Equal(1068, CapacityOfNewSession(24, 80));
        Assert.Equal(1048576 / (200 * 12) - 24, CapacityOfNewSession(24, 200));
    }

    [Fact]
    public void ExistingSession_CapacityFollowsColumnsAfterResizeUnderTheByteCap()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":100000,\"limitBytes\":1048576}}");
        using var session = new TerminalSession(24, 80, SilentPty.Create);
        Assert.Equal(1068, session.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);

        session.Resize(200, 24); // TerminalSession.Resize takes (columns, rows)
        Assert.Equal(200, session.Adapter.Buffer.Columns);
        Assert.Equal(1048576 / (200 * 12) - 24, session.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);

        session.Resize(80, 24);
        Assert.Equal(1068, session.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);
    }

    [Fact]
    public void NewSession_ZeroBytesMeansNoByteCap_AndLinesAreCappedAtOneMillion()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":2000000,\"limitBytes\":0}}");
        Assert.Equal(ScrollbackPolicy.HardMaxLines, new ScrollbackUserConfig { LimitLines = 2_000_000, LimitBytes = 0 }.ToPolicy().CapacityFor(24, 80));
    }

    [Fact]
    public void ExistingSession_KeepsItsCapacityAfterReload()
    {
        LoadConfig("{\"scrollback\":{\"limitLines\":1000}}");
        using var first = new TerminalSession(24, 80, SilentPty.Create);

        LoadConfig("{\"scrollback\":{\"limitLines\":2000}}");
        using var second = new TerminalSession(24, 80, SilentPty.Create);

        Assert.Equal(1000, first.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Equal(2000, second.Adapter.Buffer.ActiveBuffer.ScrollbackCapacity);
    }

    [Fact]
    public void GeneratedDefaultConfigFile_ContainsScrollbackBlock()
    {
        LoadConfig(null);
        string path = Path.Combine(_home, "config.json");
        Assert.True(File.Exists(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement block = document.RootElement.GetProperty("scrollback");
        Assert.Equal(5000, block.GetProperty("limitLines").GetInt32());
        Assert.Equal(67_108_864L, block.GetProperty("limitBytes").GetInt64());
        Assert.False(block.TryGetProperty("compression", out _));
    }

    [Fact]
    public void Lua_SetsScrollbackLimits()
    {
        var config = new DottyUserConfig();
        using var tabManager = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(tabManager), tabManager);
        Assert.True(host.Evaluate(config, null));

        Assert.True(host.ExecuteString("dotty.config.scrollback.limit_lines=123; assert(dotty.config.scrollback.limit_lines==123)"));
        Assert.Equal(123, config.Scrollback.LimitLines);

        Assert.True(host.ExecuteString("dotty.config.scrollback.limit_bytes=3000000000; assert(dotty.config.scrollback.limit_bytes==3000000000)"));
        Assert.Equal(3_000_000_000L, config.Scrollback.LimitBytes);

        Assert.True(host.ExecuteString("dotty.config.apply_table({scrollback={limit_lines=7,limit_bytes=0}})"));
        Assert.Equal(7, config.Scrollback.LimitLines);
        Assert.Equal(0L, config.Scrollback.LimitBytes);
    }
}
