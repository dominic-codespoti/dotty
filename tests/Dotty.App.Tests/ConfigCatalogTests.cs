using System.Reflection;
using System.Text.Json.Serialization;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Drift guard and rendering tests for <see cref="ConfigOptionCatalog"/>. Reflection is fine here
/// (test project); the product code stays reflection-free.
/// </summary>
public sealed class ConfigCatalogTests
{
    private const string CatalogFile = "src/Dotty.Runtime/Config/ConfigOptionCatalog.cs";

    private sealed record Leaf(string Path, object? Value);

    private static List<Leaf> ReflectLeaves()
    {
        var leaves = new List<Leaf>();
        Walk(new DottyUserConfig(), "", leaves);
        return leaves;
    }

    private static bool IsGroup(Type type) =>
        type.IsClass && type != typeof(string) && HasJsonProperties(type);

    private static bool HasJsonProperties(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
                return true;
        }
        return false;
    }

    private static void Walk(object instance, string prefix, List<Leaf> leaves)
    {
        foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            JsonPropertyNameAttribute? name = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (name is null)
                continue;
            string path = prefix.Length == 0 ? name.Name : prefix + "." + name.Name;
            object? value = property.GetValue(instance);
            if (IsGroup(property.PropertyType))
            {
                Assert.True(value is not null, $"Config group '{path}' defaults to null; the catalog guard needs a default instance.");
                Walk(value!, path, leaves);
            }
            else
            {
                leaves.Add(new Leaf(path, value));
            }
        }
    }

    private static string Squash(string key) => key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    [Fact]
    public void EveryConfigPropertyHasACatalogEntry()
    {
        var catalogPaths = new HashSet<string>(ConfigOptionCatalog.All.Select(o => o.JsonPath), StringComparer.Ordinal);
        string[] missing = ReflectLeaves().Select(l => l.Path).Where(p => !catalogPaths.Contains(p)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"DottyUserConfig has option(s) without documentation: {string.Join(", ", missing)}. " +
            $"Add an Opt(...) entry per option to CreateLive() in {CatalogFile} (and make sure it has a Lua key).");
    }

    [Fact]
    public void EveryCatalogEntryNamesARealConfigProperty()
    {
        var real = new HashSet<string>(ReflectLeaves().Select(l => l.Path), StringComparer.Ordinal);
        string[] stale = ConfigOptionCatalog.All
            .Select(o => o.JsonPath)
            .Where(p => !real.Contains(p))
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"Catalog entries without a matching [JsonPropertyName] path in DottyUserConfig: {string.Join(", ", stale)}. " +
            $"Remove them from {CatalogFile} or fix the path.");
    }

    [Fact]
    public void CatalogDefaultsEqualTheLiveDefaultConfig()
    {
        Dictionary<string, Leaf> real = ReflectLeaves().ToDictionary(l => l.Path, StringComparer.Ordinal);
        foreach (ConfigOption option in ConfigOptionCatalog.All)
            Assert.Equal(ConfigOptionCatalog.FormatDefault(real[option.JsonPath].Value), option.Default());
    }

    [Fact]
    public void EveryLuaConfigKeyHasACatalogEntry()
    {
        var luaKeys = new HashSet<string>(ConfigOptionCatalog.All.Select(o => o.LuaKey), StringComparer.Ordinal);
        string[] missing = LuaScriptHost.ConfigFieldKeys.Where(k => !luaKeys.Contains(k)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"LuaScriptHost.ConfigFields key(s) without a catalog entry: {string.Join(", ", missing)}. " +
            $"Add or fix the LuaKey of the matching option in {CatalogFile}.");
    }

    [Fact]
    public void EveryCatalogLuaKeyIsARealLuaField()
    {
        var fields = new HashSet<string>(LuaScriptHost.ConfigFieldKeys, StringComparer.Ordinal);
        string[] stale = ConfigOptionCatalog.All
            .Where(o => !fields.Contains(o.LuaKey))
            .Select(o => $"{o.JsonPath} -> {o.LuaKey}")
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"Catalog Lua key(s) not registered in LuaScriptHost.ConfigFields: {string.Join(", ", stale)}.");
    }

    [Fact]
    public void JsonPathAndLuaKeyDescribeTheSameOption()
    {
        foreach (ConfigOption option in ConfigOptionCatalog.All)
            Assert.True(Squash(option.JsonPath) == Squash(option.LuaKey), $"'{option.JsonPath}' and Lua key '{option.LuaKey}' differ by more than case and underscores.");
    }

    [Fact]
    public void CatalogEntriesAreWellFormedUniqueAndSorted()
    {
        IReadOnlyList<ConfigOption> all = ConfigOptionCatalog.All;
        Assert.Equal(all.Count, all.Select(o => o.JsonPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(o => o.LuaKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Select(o => o.JsonPath).OrderBy(p => p, StringComparer.Ordinal).ToArray(), all.Select(o => o.JsonPath).ToArray());

        string[] types = ["string", "number", "integer", "boolean", "string[]", "map"];
        foreach (ConfigOption option in all)
        {
            Assert.Contains(option.Type, types);
            Assert.False(string.IsNullOrWhiteSpace(option.Description), option.JsonPath);
            Assert.DoesNotContain("\n", option.Description);
            if (option.Scope is not null)
                Assert.DoesNotContain("\n", option.Scope);
            if (option.Values is not null)
                Assert.NotEmpty(option.Values);
            Assert.False(string.IsNullOrEmpty(option.Default()), option.JsonPath);
        }
    }

    [Fact]
    public void VtShellIntegrationAndMouseOptionsCarryTheirLiveDefaults()
    {
        Assert.Equal("false", ConfigOptionCatalog.Find("vt.windowResizeAllowed")!.Default());
        Assert.Equal("false", ConfigOptionCatalog.Find("vt.checksumReportEnabled")!.Default());
        Assert.Equal("\"none\"", ConfigOptionCatalog.Find("shellIntegration")!.Default());
        Assert.Equal("\"none\"", ConfigOptionCatalog.Find("mouse.copyOnSelect")!.Default());
        Assert.Equal("\"clipboard-paste\"", ConfigOptionCatalog.Find("mouse.middleClickAction")!.Default());
        Assert.Equal("vt.window_resize_allowed", ConfigOptionCatalog.Find("vt.windowResizeAllowed")!.LuaKey);
        Assert.Equal("shell_integration", ConfigOptionCatalog.Find("shellIntegration")!.LuaKey);
    }

    [Theory]
    [InlineData("font.size", "font.size")]
    [InlineData("FONT.SIZE", "font.size")]
    [InlineData("font.line_height", "font.lineHeight")]
    [InlineData("font.line-height", "font.lineHeight")]
    [InlineData("tab_bar.show", "tabBar.show")]
    [InlineData("tab-bar.show", "tabBar.show")]
    [InlineData("clipboard.osc52_read_limit_bytes", "clipboard.osc52ReadLimitBytes")]
    [InlineData("copy-on-select", "mouse.copyOnSelect")]
    [InlineData("mouse.copy_on_select", "mouse.copyOnSelect")]
    [InlineData("MOUSE.COPYONSELECT", "mouse.copyOnSelect")]
    [InlineData("keybindings", "keybindings")]
    [InlineData("theme", "theme")]
    [InlineData("  font.size ", "font.size")]
    public void FindAcceptsJsonPathLuaKeyAndLeafInAnyCase(string query, string expectedPath)
    {
        Assert.Equal(expectedPath, ConfigOptionCatalog.Find(query)?.JsonPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("font.nope")]
    [InlineData("mouse")]
    [InlineData("font")]
    public void FindReturnsNullForUnknownOrGroupQueries(string query)
    {
        Assert.Null(ConfigOptionCatalog.Find(query));
    }

    [Fact]
    public void FindReturnsNullWhenALeafNameIsAmbiguous()
    {
        string? duplicate = ConfigOptionCatalog.All
            .GroupBy(o => o.JsonPath[(o.JsonPath.LastIndexOf('.') + 1)..], StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is null)
            return; // no ambiguous leaf names in the current catalog

        Assert.Null(ConfigOptionCatalog.Find(duplicate));
    }

    [Fact]
    public void FindGroupListsEveryOptionBelowThePrefix()
    {
        IReadOnlyList<ConfigOption> padding = ConfigOptionCatalog.FindGroup("window.padding");
        Assert.Equal(
            new[] { "window.padding.bottom", "window.padding.left", "window.padding.right", "window.padding.top" },
            padding.Select(o => o.JsonPath).ToArray());

        IReadOnlyList<ConfigOption> mouse = ConfigOptionCatalog.FindGroup("mouse");
        Assert.NotEmpty(mouse);
        Assert.All(mouse, o => Assert.StartsWith("mouse.", o.JsonPath));

        IReadOnlyList<ConfigOption> luaSpelledGroup = ConfigOptionCatalog.FindGroup("tab_bar.");
        Assert.Equal(3, luaSpelledGroup.Count);

        Assert.Empty(ConfigOptionCatalog.FindGroup("nope"));
        Assert.Empty(ConfigOptionCatalog.FindGroup(""));
        Assert.Empty(ConfigOptionCatalog.FindGroup("font.size")); // a leaf is not a group
    }

    [Fact]
    public void RenderPrintsLabelledFieldsThenTheDescription()
    {
        ConfigOption size = ConfigOptionCatalog.Find("font.size")!;
        Assert.Equal(
            "font.size\n" +
            "Lua key: font.size\n" +
            "Type:    number\n" +
            "Default: 14\n" +
            "\n" +
            size.Description,
            ConfigOptionCatalog.Render(size));

        ConfigOption confirm = ConfigOptionCatalog.Find("hyperlinks.confirm")!;
        Assert.Equal(
            "hyperlinks.confirm\n" +
            "Lua key: hyperlinks.confirm\n" +
            "Type:    string\n" +
            "Default: \"risky\"\n" +
            "Values:  risky, always, never\n" +
            "\n" +
            confirm.Description,
            ConfigOptionCatalog.Render(confirm));

        ConfigOption read = ConfigOptionCatalog.Find("clipboard.osc52Read")!;
        string rendered = ConfigOptionCatalog.Render(read);
        Assert.Contains("\nDefault: \"deny\"\n", rendered);
        Assert.Contains("\nValues:  deny, ask, allow\n", rendered);
        Assert.Contains("\nScope:   ", rendered);
    }

    [Fact]
    public void DefaultsAreFormattedFromTheLiveConfig()
    {
        Assert.Equal("14", ConfigOptionCatalog.Find("font.size")!.Default());
        Assert.Equal("1.25", ConfigOptionCatalog.Find("font.lineHeight")!.Default());
        Assert.Equal("true", ConfigOptionCatalog.Find("clipboard.confirmPaste")!.Default());
        Assert.Equal("false", ConfigOptionCatalog.Find("clipboard.allowOsc52Write")!.Default());
        Assert.Equal("1048576", ConfigOptionCatalog.Find("clipboard.osc52ReadLimitBytes")!.Default());
        Assert.Equal("(unset)", ConfigOptionCatalog.Find("themeLight")!.Default());
        Assert.Equal("\"DarkPlus\"", ConfigOptionCatalog.Find("theme")!.Default());
        Assert.Equal("{}", ConfigOptionCatalog.Find("keybindings")!.Default());
        Assert.Equal("[]", ConfigOptionCatalog.Find("font.symbolMap")!.Default());
        Assert.Equal(
            "[\"http\", \"https\", \"mailto\", \"file\", \"ssh\", \"git\"]",
            ConfigOptionCatalog.Find("hyperlinks.allowedSchemes")!.Default());
    }

    [Fact]
    public void FormatDefaultEscapesAndRejectsUnsupportedTypes()
    {
        Assert.Equal("\"a\\\"b\\\\c\"", ConfigOptionCatalog.FormatDefault("a\"b\\c"));
        Assert.Equal("{ \"k\": \"v\" }", ConfigOptionCatalog.FormatDefault(new Dictionary<string, string> { ["k"] = "v" }));
        Assert.Throws<ArgumentException>(() => ConfigOptionCatalog.FormatDefault(1L));
    }

    [Fact]
    public void ExplainConfigWithoutArgumentsListsEveryOption()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int code = CliActions.ExplainConfig([], output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ConfigOptionCatalog.All.Count, lines.Length);
        for (int i = 0; i < lines.Length; i++)
            Assert.StartsWith(ConfigOptionCatalog.All[i].JsonPath + " ", lines[i]);
    }

    [Fact]
    public void ExplainConfigWithAnOptionPrintsItsDocumentation()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int code = CliActions.ExplainConfig(["font.size"], output, error);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(ConfigOptionCatalog.Render(ConfigOptionCatalog.Find("font.size")!) + "\n", output.ToString());
    }

    [Fact]
    public void ExplainConfigWithAGroupListsTheGroup()
    {
        var output = new StringWriter();

        int code = CliActions.ExplainConfig(["window.padding"], output, new StringWriter());

        Assert.Equal(0, code);
        Assert.Equal(4, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ExplainConfigUnknownOptionExitsOneWithSuggestions()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int code = CliActions.ExplainConfig(["font.sizee"], output, error);

        Assert.Equal(1, code);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("dotty: no such option 'font.sizee'\n", error.ToString());
        Assert.Contains("Did you mean:\n  font.size\n", error.ToString());
    }

    [Fact]
    public void ExplainConfigUnknownOptionWithoutSuggestionsStillExitsOne()
    {
        var error = new StringWriter();

        int code = CliActions.ExplainConfig(["zzz"], new StringWriter(), error);

        Assert.Equal(1, code);
        Assert.DoesNotContain("Did you mean", error.ToString());
        Assert.Contains("dotty +explain-config", error.ToString());
    }

    [Fact]
    public void RunDispatchesTheKnownActionAndRejectsOthers()
    {
        var output = new StringWriter();
        Assert.Equal(0, CliActions.Run("+explain-config", ["theme"], output, new StringWriter()));
        Assert.StartsWith("theme\n", output.ToString());

        Assert.Throws<ArgumentException>(() => CliActions.Run("+bogus", [], new StringWriter(), new StringWriter()));
    }
}
