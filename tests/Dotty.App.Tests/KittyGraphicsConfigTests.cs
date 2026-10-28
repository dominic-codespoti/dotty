using System.Text.Json;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>The <c>graphics</c> config section: defaults, JSON, normalisation, mapping and Lua keys.</summary>
public sealed class KittyGraphicsConfigTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void Defaults_MatchDottyDefaultsAndOptionRecord()
    {
        var g = new DottyUserConfig().Graphics;
        Assert.Equal(DottyDefaults.KittyGraphicsEnabled, g.KittyProtocol);
        Assert.Equal(320, g.ImageStorageLimitMiB);
        Assert.Equal(65, g.MaxPayloadMiB);
        Assert.True(g.AllowFileTransfer && g.AllowTempFileTransfer && g.AllowSharedMemory);

        KittyGraphicsOptions o = new DottyUserConfig().ToKittyGraphicsOptions();
        Assert.Equal(KittyGraphicsOptions.DefaultOptions.Enabled, o.Enabled);
        Assert.Equal(320 * MiB, o.StorageLimitBytes);
        Assert.Equal(65 * MiB, o.MaxPayloadBytes);
        Assert.Equal(10000, o.MaxImageDimension);
        Assert.Equal(8192, o.MaxPlacementsPerScreen);
        Assert.Equal(KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory, o.Media);
    }

    [Fact]
    public void RecordDefaults_AreDirectOnly()
    {
        Assert.Equal(KittyMediaFlags.Direct, new KittyGraphicsOptions().Media);
    }

    [Fact]
    public void Json_ReadsEveryKeyAndKeepsOmittedOnes()
    {
        var c = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"graphics\":{\"kittyProtocol\":true,\"imageStorageLimitMiB\":64,\"maxPayloadMiB\":8,\"allowFileTransfer\":false,\"allowTempFileTransfer\":false,\"allowSharedMemory\":false}}")!;
        Assert.True(c.Graphics.KittyProtocol);
        Assert.Equal(64, c.Graphics.ImageStorageLimitMiB);
        Assert.Equal(8, c.Graphics.MaxPayloadMiB);
        Assert.False(c.Graphics.AllowFileTransfer);
        Assert.False(c.Graphics.AllowTempFileTransfer);
        Assert.False(c.Graphics.AllowSharedMemory);

        var partial = JsonSerializer.Deserialize<DottyUserConfig>("{\"graphics\":{\"maxPayloadMiB\":3}}")!;
        Assert.Equal(3, partial.Graphics.MaxPayloadMiB);
        Assert.Equal(320, partial.Graphics.ImageStorageLimitMiB);
        Assert.True(partial.Graphics.AllowFileTransfer);
    }

    [Fact]
    public void Json_RoundTripsUsingDocumentedNames()
    {
        var c = new DottyUserConfig();
        c.Graphics.KittyProtocol = true;
        c.Graphics.ImageStorageLimitMiB = 11;
        string json = JsonSerializer.Serialize(c);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement g = doc.RootElement.GetProperty("graphics");
        Assert.True(g.GetProperty("kittyProtocol").GetBoolean());
        Assert.Equal(11, g.GetProperty("imageStorageLimitMiB").GetInt32());
        Assert.True(g.TryGetProperty("allowSharedMemory", out _));
        Assert.Equal(11, JsonSerializer.Deserialize<DottyUserConfig>(json)!.Graphics.ImageStorageLimitMiB);
    }

    [Theory]
    [InlineData(0, 320)]
    [InlineData(-5, 320)]
    [InlineData(int.MinValue, 320)]
    [InlineData(1, 1)]
    [InlineData(4096, 4096)]
    [InlineData(4097, 4096)]
    [InlineData(int.MaxValue, 4096)]
    public void StorageLimit_NormalisesWithoutOverflow(int mib, long expectedMiB)
    {
        var c = new DottyUserConfig();
        c.Graphics.ImageStorageLimitMiB = mib;
        Assert.Equal(expectedMiB * MiB, c.ToKittyGraphicsOptions().StorageLimitBytes);
    }

    [Theory]
    [InlineData(0, 65)]
    [InlineData(-1, 65)]
    [InlineData(1, 1)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 1024)]
    [InlineData(int.MaxValue, 1024)]
    public void MaxPayload_NormalisesWithoutOverflow(int mib, long expectedMiB)
    {
        var c = new DottyUserConfig();
        c.Graphics.MaxPayloadMiB = mib;
        Assert.Equal(expectedMiB * MiB, c.ToKittyGraphicsOptions().MaxPayloadBytes);
    }

    [Theory]
    [InlineData(false, false, false, KittyMediaFlags.Direct)]
    [InlineData(true, false, false, KittyMediaFlags.Direct | KittyMediaFlags.File)]
    [InlineData(false, true, false, KittyMediaFlags.Direct | KittyMediaFlags.TempFile)]
    [InlineData(false, false, true, KittyMediaFlags.Direct | KittyMediaFlags.SharedMemory)]
    [InlineData(true, true, true, KittyMediaFlags.Direct | KittyMediaFlags.File | KittyMediaFlags.TempFile | KittyMediaFlags.SharedMemory)]
    public void MediaFlags_FollowAllowBooleans(bool file, bool temp, bool shm, KittyMediaFlags expected)
    {
        var c = new DottyUserConfig();
        c.Graphics.AllowFileTransfer = file;
        c.Graphics.AllowTempFileTransfer = temp;
        c.Graphics.AllowSharedMemory = shm;
        Assert.Equal(expected, c.ToKittyGraphicsOptions().Media);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enabled_IsCarriedThrough(bool enabled)
    {
        var c = new DottyUserConfig();
        c.Graphics.KittyProtocol = enabled;
        Assert.Equal(enabled, c.ToKittyGraphicsOptions().Enabled);
    }

    [Fact]
    public void Normalize_FixesRecordValuesAndForcesDirect()
    {
        var o = new KittyGraphicsOptions
        {
            StorageLimitBytes = -1, MaxPayloadBytes = 0, MaxImageDimension = -3, MaxPlacementsPerScreen = 0,
            Media = KittyMediaFlags.None,
        }.Normalize();
        Assert.Equal(DottyDefaults.KittyImageStorageLimitBytes, o.StorageLimitBytes);
        Assert.Equal(DottyDefaults.KittyMaxPayloadBytes, o.MaxPayloadBytes);
        Assert.Equal(10000, o.MaxImageDimension);
        Assert.Equal(8192, o.MaxPlacementsPerScreen);
        Assert.Equal(KittyMediaFlags.Direct, o.Media);

        var big = new KittyGraphicsOptions { StorageLimitBytes = long.MaxValue, MaxPayloadBytes = long.MaxValue, MaxImageDimension = int.MaxValue }.Normalize();
        Assert.Equal(KittyGraphicsOptions.HardMaxStorageLimitBytes, big.StorageLimitBytes);
        Assert.Equal(KittyGraphicsOptions.HardMaxPayloadBytes, big.MaxPayloadBytes);
        Assert.Equal(KittyGraphicsOptions.HardMaxImageDimension, big.MaxImageDimension);
    }

    [Fact]
    public void Lua_SetsAndReadsGraphicsKeys()
    {
        var config = new DottyUserConfig();
        using var tabManager = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(tabManager), tabManager);
        Assert.True(host.Evaluate(config, null));

        Assert.True(host.ExecuteString("dotty.config.graphics.kitty_protocol=true; assert(dotty.config.graphics.kitty_protocol==true)"));
        Assert.True(config.Graphics.KittyProtocol);
        Assert.True(host.ExecuteString("dotty.config.graphics.image_storage_limit_mib=128; assert(dotty.config.graphics.image_storage_limit_mib==128)"));
        Assert.Equal(128, config.Graphics.ImageStorageLimitMiB);
        Assert.True(host.ExecuteString("dotty.config.graphics.max_payload_mib=9; assert(dotty.config.graphics.max_payload_mib==9)"));
        Assert.Equal(9, config.Graphics.MaxPayloadMiB);
        Assert.True(host.ExecuteString("dotty.config.graphics.allow_file_transfer=false; assert(dotty.config.graphics.allow_file_transfer==false)"));
        Assert.False(config.Graphics.AllowFileTransfer);

        Assert.True(host.ExecuteString("dotty.config.apply_table({graphics={allow_temp_file_transfer=false,allow_shared_memory=false,max_payload_mib=2}})"));
        Assert.False(config.Graphics.AllowTempFileTransfer);
        Assert.False(config.Graphics.AllowSharedMemory);
        Assert.Equal(2, config.Graphics.MaxPayloadMiB);
        Assert.Equal(KittyMediaFlags.Direct, config.ToKittyGraphicsOptions().Media);
    }

    [Fact]
    public void Catalog_ListsAllSevenGraphicsOptions()
    {
        Assert.Equal(7, ConfigOptionCatalog.FindGroup("graphics").Count);
        Assert.Equal("graphics.max_payload_mib", ConfigOptionCatalog.Find("graphics.maxPayloadMiB")!.LuaKey);
        Assert.Equal("320", ConfigOptionCatalog.Find("graphics.imageStorageLimitMiB")!.Default());
        Assert.Equal("graphics.windows_passthrough", ConfigOptionCatalog.Find("graphics.windowsPassthrough")!.LuaKey);
    }

    [Fact]
    public void WindowsPassthrough_DefaultsFalse_AndRoundTripsJson()
    {
        Assert.False(DottyDefaults.GraphicsWindowsPassthrough);
        Assert.False(new DottyUserConfig().Graphics.WindowsPassthrough);

        var c = JsonSerializer.Deserialize<DottyUserConfig>("{\"graphics\":{\"windowsPassthrough\":true}}")!;
        Assert.True(c.Graphics.WindowsPassthrough);
        Assert.Equal(320, c.Graphics.ImageStorageLimitMiB);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(c));
        Assert.True(doc.RootElement.GetProperty("graphics").GetProperty("windowsPassthrough").GetBoolean());
        Assert.False(JsonSerializer.Deserialize<DottyUserConfig>("{\"graphics\":{}}")!.Graphics.WindowsPassthrough);
    }

    [Fact]
    public void WindowsPassthrough_LuaKeyRoundTrips()
    {
        var config = new DottyUserConfig();
        using var tabManager = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(tabManager), tabManager);
        Assert.True(host.Evaluate(config, null));

        Assert.True(host.ExecuteString("dotty.config.graphics.windows_passthrough=true; assert(dotty.config.graphics.windows_passthrough==true)"));
        Assert.True(config.Graphics.WindowsPassthrough);
        Assert.True(host.ExecuteString("dotty.config.apply_table({graphics={windows_passthrough=false}})"));
        Assert.False(config.Graphics.WindowsPassthrough);
    }
}
