using System.Text;
using Dotty.Runtime.Config;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ConfigFileValidatorTests
{
    private const string ConfigPath = "/cfg/config.json";

    private static IReadOnlyList<ConfigDiagnostic> Validate(string json) =>
        ConfigFileValidator.ValidateJson(ConfigPath, Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"theme\": \"Dracula\", // comment\n  \"font\": { \"size\": 13, }, }")]
    [InlineData("/* leading */ { \"window\": { \"padding\": { \"left\": 2 } } }")]
    [InlineData("{ \"FONT\": { \"Size\": 12 }, \"TabBar\": { \"show\": false } }")]
    [InlineData("{ \"keybindings\": { \"ctrl+shift+x\": \"Copy\", \"anything.goes\": \"None\" } }")]
    [InlineData("{ \"font\": { \"symbolMap\": [\"U+E000-U+F8FF:Symbols Nerd Font\"] } }")]
    [InlineData("{ \"selectionColor\": null }")]
    [InlineData("\uFEFF{ \"theme\": \"DarkPlus\" }")]
    public void ValidFilesProduceNoDiagnostics(string json)
    {
        Assert.Empty(Validate(json));
    }

    [Theory]
    [InlineData("{\"fnt\": {}}", 1, 2, "unknown option 'fnt'")]
    [InlineData("{\n  \"font\": { \"sise\": 12 }\n}", 2, 13, "unknown option 'font.sise'")]
    [InlineData("{ \"font\": { \"sizee\": 12 } }", 1, 13, "did you mean 'font.size'?")]
    [InlineData("{ \"font\": 12 }", 1, 3, "'font' is a group of options and must be an object")]
    [InlineData("{ \"font\": null }", 1, 3, "'font' is a group of options and must be an object")]
    [InlineData("[]", 1, 1, "the top level must be a JSON object")]
    [InlineData("", 0, 0, "the file is empty")]
    [InlineData("{ \"theme\": }", 1, 0, "")]
    [InlineData("{ \"theme\": \"x\" }\n{}", 2, 0, "")]
    [InlineData("{ \"font\": { \"size\": \"big\" } }", 1, 0, "invalid value for 'font.size'")]
    [InlineData("{\n\n  \"cursor\": { \"blink\": 3 }\n}", 3, 0, "invalid value for 'cursor.blink'")]
    public void ProblemsAreReportedWithTheirPosition(string json, int line, int column, string fragment)
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Validate(json);

        ConfigDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ConfigPath, diagnostic.File);
        Assert.Equal(line, diagnostic.Line);
        if (column > 0)
            Assert.Equal(column, diagnostic.Column);
        Assert.Contains(fragment, diagnostic.Message);
        Assert.DoesNotContain("LineNumber:", diagnostic.Message);
    }

    [Fact]
    public void EveryUnknownKeyIsReported()
    {
        IReadOnlyList<ConfigDiagnostic> diagnostics = Validate("{ \"a\": 1,\n \"b\": { \"c\": 2 },\n \"theme\": \"x\" }");

        Assert.Equal(new[] { "unknown option 'a'", "unknown option 'b'" }, diagnostics.Select(d => d.Message[..d.Message.IndexOf(';')]));
        Assert.Equal(new[] { 1, 2 }, diagnostics.Select(d => d.Line));
    }

    [Theory]
    [InlineData(3, 7, "m", "/cfg/config.json:3:7: m")]
    [InlineData(3, 0, "m", "/cfg/config.json:3: m")]
    [InlineData(0, 0, "m", "/cfg/config.json: m")]
    public void DiagnosticsFormatAsFileLineColumn(int line, int column, string message, string expected)
    {
        Assert.Equal(expected, new ConfigDiagnostic(ConfigPath, line, column, message).ToString());
    }

    [Theory]
    [InlineData("config:3: unexpected symbol near 'x'", 3, "unexpected symbol near 'x'")]
    [InlineData("config:12: 'end' expected (to close 'function' at line 1) near <eof>", 12, "'end' expected (to close 'function' at line 1) near <eof>")]
    [InlineData("not enough memory", 0, "not enough memory")]
    [InlineData("config:x: odd", 0, "config:x: odd")]
    [InlineData("config:", 0, "config:")]
    public void LuaMessagesAreSplitIntoLineAndText(string message, int line, string text)
    {
        ConfigDiagnostic diagnostic = ConfigFileValidator.ParseLuaError("/cfg/config.lua", message);

        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(text, diagnostic.Message);
        Assert.Equal("/cfg/config.lua", diagnostic.File);
    }

    [Fact]
    public void LuaIsCompiledButNotRun()
    {
        Assert.Empty(ConfigFileValidator.ValidateLua("/cfg/config.lua", "error('runtime errors are not syntax errors')\n"));

        ConfigDiagnostic diagnostic = Assert.Single(
            ConfigFileValidator.ValidateLua("/cfg/config.lua", "local x = 1\nlocal y = = 2\n"));
        Assert.Equal(2, diagnostic.Line);
        Assert.Contains("near", diagnostic.Message);
    }

    [Fact]
    public void TheInitTemplateIsValidAndParsesToTheDefaults()
    {
        string template = ConfigTemplate.Render();

        Assert.Empty(Validate(template));
        string path = Path.Combine(Path.GetTempPath(), "dotty-template-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, template);
            DottyUserConfig parsed = UserConfigService.ReadFile(path);
            Assert.Equal(UserConfigService.Serialize(new DottyUserConfig()), UserConfigService.Serialize(parsed));
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Contains("+validate-config", template);
        Assert.Contains("// \"font\": { \"size\": " + ConfigOptionCatalog.Find("font.size")!.Default() + " },", template);
    }
}
