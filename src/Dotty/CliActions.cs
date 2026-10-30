using System.Runtime.InteropServices;
using System.Text;
using Dotty.NativePty.Unix;
using Dotty.Rendering.Gpu;
using Dotty.Runtime;
using Dotty.Runtime.Config;
using Dotty.Runtime.Diagnostics;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Themes;

namespace Dotty.Silk;

/// <summary>
/// Command-line actions (<c>dotty +action</c>). They print to the given writers and return the
/// process exit code. None of them creates a terminal window or starts the config watcher; only
/// <c>+diagnose --gl</c> creates a hidden OpenGL window.
/// </summary>
internal static class CliActions
{
    public static int Run(DottyLaunchOptions options, TextWriter output, TextWriter error)
    {
        string configFile = PlatformPaths.ConfigFile;
        return options.Action switch
        {
            DottyLaunchOptionsParser.ExplainConfigAction => ExplainConfig(options.ActionArguments, output, error),
            DottyLaunchOptionsParser.InitConfigAction => InitConfig(configFile, options.HasActionFlag(DottyLaunchOptionsParser.ForceFlag), output, error),
            DottyLaunchOptionsParser.ShowConfigAction => ShowConfig(configFile, LuaScriptHost.GetConfigLuaPath(), options.HasActionFlag(DottyLaunchOptionsParser.DefaultFlag), output, error),
            DottyLaunchOptionsParser.ValidateConfigAction => ValidateConfig(configFile, LuaScriptHost.GetConfigLuaPath(), output, error),
            DottyLaunchOptionsParser.ListThemesAction => ListThemes(new ThemeRegistry(), output),
            DottyLaunchOptionsParser.DiagnoseAction => Diagnose(options.HasActionFlag(DottyLaunchOptionsParser.GlFlag), output),
            _ => throw new ArgumentException($"Unknown action '{options.Action}'."),
        };
    }

    /// <summary>
    /// Lists every option (no argument), documents one option, or lists a group such as <c>mouse</c>.
    /// Exit code 1 and a message on <paramref name="error"/> when nothing matches.
    /// </summary>
    public static int ExplainConfig(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Count == 0)
        {
            output.Write(ConfigOptionCatalog.RenderList(ConfigOptionCatalog.All));
            return 0;
        }

        string query = arguments[0];
        ConfigOption? option = ConfigOptionCatalog.Find(query);
        if (option is not null)
        {
            output.Write(ConfigOptionCatalog.Render(option));
            output.Write('\n');
            return 0;
        }

        IReadOnlyList<ConfigOption> group = ConfigOptionCatalog.FindGroup(query);
        if (group.Count > 0)
        {
            output.Write(ConfigOptionCatalog.RenderList(group));
            return 0;
        }

        error.Write($"dotty: no such option '{query}'\n");
        IReadOnlyList<ConfigOption> suggestions = ConfigOptionCatalog.Suggest(query);
        if (suggestions.Count > 0)
        {
            error.Write("Did you mean:\n");
            foreach (ConfigOption suggestion in suggestions)
                error.Write($"  {suggestion.JsonPath}\n");
        }
        error.Write("Run 'dotty +explain-config' to list every option.\n");
        return 1;
    }

    /// <summary>
    /// Writes <see cref="ConfigTemplate"/> to <paramref name="configFile"/>. Refuses (exit 1) when the file
    /// exists unless <paramref name="force"/>; the write is atomic (temporary file, then rename).
    /// </summary>
    public static int InitConfig(string configFile, bool force, TextWriter output, TextWriter error)
    {
        if (File.Exists(configFile) && !force)
        {
            error.Write($"dotty: {configFile} already exists; use '+init-config --force' to replace it\n");
            return 1;
        }

        string? directory = Path.GetDirectoryName(configFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = configFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, ConfigTemplate.Render(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, configFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            error.Write($"dotty: cannot write {configFile}: {ex.Message}\n");
            return 1;
        }

        output.Write($"Wrote {configFile}\n");
        return 0;
    }

    /// <summary>
    /// Prints the configuration as JSON: the built-in defaults with <paramref name="defaults"/>, otherwise
    /// <c>config.json</c> merged over them. Exit 1 when <c>config.json</c> cannot be read. A note on
    /// <paramref name="error"/> says when a Lua config exists, because its overrides are not applied here.
    /// </summary>
    public static int ShowConfig(string configFile, string luaFile, bool defaults, TextWriter output, TextWriter error)
    {
        DottyUserConfig config;
        if (defaults || !File.Exists(configFile))
        {
            config = new DottyUserConfig();
        }
        else
        {
            try
            {
                config = UserConfigService.ReadFile(configFile);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                error.Write($"dotty: cannot read {configFile}: {ex.Message}\n");
                error.Write("Run 'dotty +validate-config' for details.\n");
                return 1;
            }
        }

        output.Write(UserConfigService.Serialize(config));
        output.Write('\n');
        if (!defaults && File.Exists(luaFile))
            error.Write($"dotty: note: {luaFile} also runs at launch; its overrides are not shown\n");
        return 0;
    }

    /// <summary>
    /// Checks <c>config.json</c> and the Lua config. Prints one <c>file:line:column: message</c> per problem on
    /// <paramref name="error"/> and returns 1, or prints a short confirmation and returns 0. Missing files are fine.
    /// </summary>
    public static int ValidateConfig(string configFile, string luaFile, TextWriter output, TextWriter error)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        int checkedFiles = 0;
        try
        {
            if (File.Exists(configFile))
            {
                checkedFiles++;
                diagnostics.AddRange(ConfigFileValidator.ValidateJson(configFile, File.ReadAllBytes(configFile)));
            }
            if (File.Exists(luaFile))
            {
                checkedFiles++;
                diagnostics.AddRange(ConfigFileValidator.ValidateLua(luaFile, File.ReadAllText(luaFile)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.Write($"dotty: {ex.Message}\n");
            return 1;
        }

        if (diagnostics.Count > 0)
        {
            foreach (ConfigDiagnostic diagnostic in diagnostics)
                error.Write($"{diagnostic}\n");
            return 1;
        }

        output.Write(checkedFiles == 0
            ? $"No config file at {configFile}; built-in defaults apply.\n"
            : "Configuration is valid.\n");
        return 0;
    }

    public static int ListThemes(ThemeRegistry registry, TextWriter output)
    {
        output.Write("Built-in themes:\n");
        foreach (string name in registry.GetBuiltInThemeNames())
            output.Write($"  {name}\n");

        IReadOnlyList<string> userThemes = registry.GetUserThemeNames();
        output.Write($"User themes ({registry.UserThemesDirectory}):\n");
        if (userThemes.Count == 0)
            output.Write("  (none)\n");
        foreach (string name in userThemes)
            output.Write($"  {name}\n");
        return 0;
    }

    /// <summary>Prints what a bug report needs. Always exit 0; problems are reported in the text.</summary>
    public static int Diagnose(bool probeGl, TextWriter output)
    {
        HostPlatform platform = PlatformPaths.CurrentPlatform;
        PlatformPathSet paths = PlatformPaths.Current;
        string configFile = Path.Combine(paths.ConfigRoot, "config.json");
        string luaFile = LuaScriptHost.GetConfigLuaPath();
        string crashLog = Path.Combine(CrashLog.DirectoryFor(paths.StateRoot), CrashLog.FileName);

        DottyUserConfig config = new();
        int jsonProblems = 0;
        int luaProblems = 0;
        bool hasJson = File.Exists(configFile);
        bool hasLua = File.Exists(luaFile);
        try
        {
            if (hasJson)
            {
                jsonProblems = ConfigFileValidator.ValidateJson(configFile, File.ReadAllBytes(configFile)).Count;
                if (jsonProblems == 0)
                    config = UserConfigService.ReadFile(configFile);
            }
            if (hasLua)
                luaProblems = ConfigFileValidator.ValidateLua(luaFile, File.ReadAllText(luaFile)).Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            jsonProblems = Math.Max(jsonProblems, 1);
        }

        var rows = new List<(string Label, string Value)>
        {
            ("Dotty", DottyVersion.Describe()),
            (".NET", $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})"),
            ("OS", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
            ("Config directory", paths.ConfigRoot),
            ("config.json", DiagnoseReport.DescribeConfigFile(configFile, hasJson, jsonProblems)),
            ("Lua config", DiagnoseReport.DescribeConfigFile(luaFile, hasLua, luaProblems)),
            ("State directory", paths.StateRoot),
            ("Crash log", DiagnoseReport.DescribeCrashLog(crashLog, File.Exists(crashLog))),
            ("PTY helper", OperatingSystem.IsWindows() ? "not used (ConPTY)" : DiagnoseReport.DescribePtyHelper(PtyHelperLocator.ResolveForCurrentProcess())),
            ("Shell integration", ShellIntegrationLocator.Find() ?? "not found"),
            ("TERM", DiagnoseReport.DescribeTerm(
                config.Term,
                TerminalIdentity.ResolveTerm(config.Term, TerminalIdentity.TerminfoEntryExists),
                TerminalIdentity.BundledTerminfoDirectory)),
        };

        string? display = DiagnoseReport.DescribeDisplay(platform, Environment.GetEnvironmentVariable);
        if (display is not null)
            rows.Add(("Display", display));

        if (probeGl)
        {
            string graphics;
            try
            {
                GlInfo info = GlInfoProbe.Query();
                graphics = DiagnoseReport.DescribeGraphics(info, GraphicsCapabilities.Classify(info.Vendor, info.Renderer, info.Version));
            }
            catch (Exception ex)
            {
                graphics = $"probe failed: {ex.Message}";
            }
            rows.Add(("OpenGL", graphics));
        }

        output.Write(DiagnoseReport.Render(rows));
        return 0;
    }
}
