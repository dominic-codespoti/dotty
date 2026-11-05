using System.Text;

namespace Dotty.Runtime.Config;

/// <summary>
/// The starter <c>config.json</c> written by <c>dotty +init-config</c>. Every setting in it is commented
/// out, so the file parses to the defaults and later default changes still reach the user.
/// </summary>
public static class ConfigTemplate
{
    /// <summary>Options shown as commented examples, with their current defaults.</summary>
    private static readonly string[] ExampleOptions = ["theme", "font.family", "font.size"];

    public static string Render()
    {
        var sb = new StringBuilder();
        sb.Append("// Dotty configuration (JSON with comments and trailing commas).\n");
        sb.Append("// Only the options set here override the built-in defaults; everything else\n");
        sb.Append("// follows Dotty's defaults, including changes in later versions.\n");
        sb.Append("//\n");
        sb.Append("//   dotty +explain-config           list every option\n");
        sb.Append("//   dotty +explain-config font.size document one option\n");
        sb.Append("//   dotty +validate-config          check this file\n");
        sb.Append("//   dotty +show-config              print the effective configuration\n");
        sb.Append("//   dotty +list-themes              list theme names\n");
        sb.Append("//\n");
        sb.Append("// Remove the leading // of a line to set that option. The values shown are the defaults.\n");
        sb.Append("{\n");
        foreach (string path in ExampleOptions)
        {
            ConfigOption option = ConfigOptionCatalog.Find(path)
                ?? throw new InvalidOperationException($"Template option '{path}' is not in the catalog.");
            int dot = option.JsonPath.IndexOf('.');
            if (dot < 0)
                sb.Append("  // \"").Append(option.JsonPath).Append("\": ").Append(option.Default()).Append(",\n");
            else
                sb.Append("  // \"").Append(option.JsonPath[..dot]).Append("\": { \"")
                    .Append(option.JsonPath[(dot + 1)..]).Append("\": ").Append(option.Default()).Append(" },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }
}
