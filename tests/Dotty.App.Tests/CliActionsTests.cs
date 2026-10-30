using Dotty.NativePty.Unix;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Themes;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>File-backed config actions (<c>+init-config</c>, <c>+show-config</c>, <c>+validate-config</c>, <c>+list-themes</c>) against a temporary directory.</summary>
public sealed class CliActionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dotty-cli-" + Guid.NewGuid().ToString("N"));

    private string ConfigFile => Path.Combine(_directory, "config.json");
    private string LuaFile => Path.Combine(_directory, "config.lua");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void InitConfigWritesTheTemplateAndRefusesToOverwriteWithoutForce()
    {
        var output = new StringWriter();
        Assert.Equal(0, CliActions.InitConfig(ConfigFile, force: false, output, new StringWriter()));
        Assert.Equal(ConfigTemplate.Render(), File.ReadAllText(ConfigFile));
        Assert.Equal($"Wrote {ConfigFile}\n", output.ToString());

        File.WriteAllText(ConfigFile, "{ \"theme\": \"Dracula\" }");
        var error = new StringWriter();
        Assert.Equal(1, CliActions.InitConfig(ConfigFile, force: false, new StringWriter(), error));
        Assert.Contains("already exists", error.ToString());
        Assert.Contains("--force", error.ToString());
        Assert.Equal("{ \"theme\": \"Dracula\" }", File.ReadAllText(ConfigFile));

        Assert.Equal(0, CliActions.InitConfig(ConfigFile, force: true, new StringWriter(), new StringWriter()));
        Assert.Equal(ConfigTemplate.Render(), File.ReadAllText(ConfigFile));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ShowConfigDefaultPrintsTheBuiltInDefaultsEvenWithAFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigFile, "{ \"theme\": \"Dracula\" }");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, CliActions.ShowConfig(ConfigFile, LuaFile, defaults: true, output, error));

        Assert.Equal(UserConfigService.Serialize(new DottyUserConfig()) + "\n", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ShowConfigMergesTheFileOverTheDefaultsAndNotesLua()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigFile, "{ \"theme\": \"Dracula\", \"font\": { \"size\": 17 } }");
        File.WriteAllText(LuaFile, "-- empty");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, CliActions.ShowConfig(ConfigFile, LuaFile, defaults: false, output, error));

        var expected = new DottyUserConfig { Theme = "Dracula" };
        expected.Font.Size = 17;
        Assert.Equal(UserConfigService.Serialize(expected) + "\n", output.ToString());
        Assert.Contains(LuaFile, error.ToString());
        Assert.Contains("not shown", error.ToString());
    }

    [Fact]
    public void ShowConfigFailsOnMalformedJson()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigFile, "{ \"theme\": ");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, CliActions.ShowConfig(ConfigFile, LuaFile, defaults: false, output, error));

        Assert.Equal("", output.ToString());
        Assert.Contains("+validate-config", error.ToString());
    }

    [Fact]
    public void ValidateConfigWithoutFilesSucceeds()
    {
        var output = new StringWriter();

        Assert.Equal(0, CliActions.ValidateConfig(ConfigFile, LuaFile, output, new StringWriter()));

        Assert.Contains("built-in defaults apply", output.ToString());
    }

    [Fact]
    public void ValidateConfigReportsEveryProblemInBothFilesAndExitsOne()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigFile, "{\n  \"thme\": \"Dracula\"\n}");
        File.WriteAllText(LuaFile, "dotty.config.theme = = 'x'\n");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, CliActions.ValidateConfig(ConfigFile, LuaFile, output, error));

        string[] lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith($"{ConfigFile}:2:3: unknown option 'thme'", lines[0]);
        Assert.StartsWith($"{LuaFile}:1: ", lines[1]);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void ValidateConfigAcceptsAValidFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigFile, "{ \"theme\": \"Dracula\" }");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, CliActions.ValidateConfig(ConfigFile, LuaFile, output, error));

        Assert.Equal("Configuration is valid.\n", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ListThemesPrintsBuiltInAndUserThemes()
    {
        string themes = Path.Combine(_directory, "themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "Mine.json"), """
            {
                "canonicalName": "Mine",
                "displayName": "Mine",
                "colors": {
                    "background": "#1E1E1E",
                    "foreground": "#D4D4D4",
                    "opacity": 1.0,
                    "ansi": [
                        "#000000", "#CD3131", "#0DBC79", "#E5E510",
                        "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                        "#666666", "#F14C4C", "#23D18B", "#F5F543",
                        "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
                    ]
                }
            }
            """);
        var output = new StringWriter();

        Assert.Equal(0, CliActions.ListThemes(new ThemeRegistry(new UserThemeLoader(themes)), output));

        string text = output.ToString();
        Assert.StartsWith("Built-in themes:\n", text);
        Assert.Contains("  DarkPlus\n", text);
        Assert.EndsWith($"User themes ({themes}):\n  Mine\n", text);
    }

    [Fact]
    public void ListThemesSaysWhenThereAreNoUserThemes()
    {
        string themes = Path.Combine(_directory, "missing-themes");
        var output = new StringWriter();

        CliActions.ListThemes(new ThemeRegistry(new UserThemeLoader(themes)), output);

        Assert.EndsWith($"User themes ({themes}):\n  (none)\n", output.ToString());
    }
}

public sealed class DiagnoseReportTests
{
    [Fact]
    public void RowsAreAlignedOnePerLine()
    {
        string text = DiagnoseReport.Render([("Dotty", "0.3.0"), ("Config directory", "/c"), ("OS", "Linux")]);

        Assert.Equal(
            "Dotty:             0.3.0\n" +
            "Config directory:  /c\n" +
            "OS:                Linux\n",
            text);
    }

    [Theory]
    [InlineData(false, 0, "/c/config.json (missing; built-in defaults apply)")]
    [InlineData(true, 0, "/c/config.json (valid)")]
    [InlineData(true, 1, "/c/config.json (1 problem; run 'dotty +validate-config')")]
    [InlineData(true, 3, "/c/config.json (3 problems; run 'dotty +validate-config')")]
    public void ConfigFileStatus(bool exists, int problems, string expected)
    {
        Assert.Equal(expected, DiagnoseReport.DescribeConfigFile("/c/config.json", exists, problems));
    }

    [Theory]
    [InlineData(HostPlatform.Linux, null, null, "none (neither WAYLAND_DISPLAY nor DISPLAY is set; no window can open)")]
    [InlineData(HostPlatform.Linux, "", ":0", "DISPLAY=:0")]
    [InlineData(HostPlatform.Linux, "wayland-1", null, "WAYLAND_DISPLAY=wayland-1")]
    [InlineData(HostPlatform.Linux, "wayland-1", ":0", "WAYLAND_DISPLAY=wayland-1, DISPLAY=:0")]
    [InlineData(HostPlatform.MacOS, null, null, null)]
    [InlineData(HostPlatform.Windows, "wayland-1", ":0", null)]
    public void DisplayStatus(HostPlatform platform, string? wayland, string? x11, string? expected)
    {
        string? actual = DiagnoseReport.DescribeDisplay(platform, name => name switch
        {
            "WAYLAND_DISPLAY" => wayland,
            "DISPLAY" => x11,
            _ => null,
        });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("xterm-256color", "xterm-256color", null, "xterm-256color (no bundled terminfo)")]
    [InlineData("xterm-dotty", "xterm-dotty", "/opt/dotty/terminfo", "xterm-dotty (bundled terminfo in /opt/dotty/terminfo)")]
    [InlineData("xterm-dotty", "xterm-256color", null, "xterm-256color (configured 'xterm-dotty' has no terminfo entry here; no bundled terminfo)")]
    public void TermStatus(string configured, string resolved, string? bundled, string expected)
    {
        Assert.Equal(expected, DiagnoseReport.DescribeTerm(configured, resolved, bundled));
    }

    [Fact]
    public void PtyHelperStatusNamesThePathOrTheProbes()
    {
        Assert.Equal(
            "/usr/lib/dotty/pty-helper (BesideExecutable)",
            DiagnoseReport.DescribePtyHelper(new PtyHelperResolution("/usr/lib/dotty/pty-helper", PtyHelperSource.BesideExecutable, ["/usr/lib/dotty/pty-helper"])));
        Assert.Equal(
            "not found; probed /a/pty-helper, /b/pty-helper",
            DiagnoseReport.DescribePtyHelper(new PtyHelperResolution(null, PtyHelperSource.NotFound, ["/a/pty-helper", "/b/pty-helper"])));
    }

    [Fact]
    public void GraphicsStatusIncludesTheClassifierSummaryAndVersionVerdict()
    {
        var info = new GlInfo("Mesa", "llvmpipe (LLVM 17.0.6, 256 bits)", "3.0 Mesa 24.0.5");
        GraphicsAdapterClassification classification = GraphicsCapabilities.Classify(info.Vendor, info.Renderer, info.Version);

        string text = DiagnoseReport.DescribeGraphics(info, classification);

        Assert.StartsWith("3.0 Mesa 24.0.5; Mesa; llvmpipe (LLVM 17.0.6, 256 bits) (" + classification.Summary, text);
        Assert.Contains(GraphicsCapabilities.DescribeUnsupportedVersion(info.Version), text);
    }
}
