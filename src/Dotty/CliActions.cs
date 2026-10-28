using Dotty.Runtime.Config;

namespace Dotty.Silk;

/// <summary>
/// Command-line actions (<c>dotty +action</c>). They print to the given writers and return the
/// process exit code; none of them loads <c>config.json</c> or creates a window.
/// </summary>
internal static class CliActions
{
    public static int Run(string action, IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        if (action == DottyLaunchOptionsParser.ExplainConfigAction)
            return ExplainConfig(arguments, output, error);
        throw new ArgumentException($"Unknown action '{action}'.");
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
}
