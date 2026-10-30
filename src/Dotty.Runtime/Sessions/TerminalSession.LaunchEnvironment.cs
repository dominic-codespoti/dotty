namespace Dotty.Runtime.Sessions;

public partial class TerminalSession
{
    /// <summary>
    /// Stable identifier of the terminal surface this session backs. Exported to the child as
    /// <c>DOTTY_SURFACE_ID</c>; <see cref="Panes.LeafPane"/> adopts it as its <c>Id</c>.
    /// Only meaningful to change before the session is started.
    /// </summary>
    public string SurfaceId { get; internal set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Builds the environment passed to the PTY: Dotty identification variables first, then the
    /// shell-integration variables, then the caller's variables (which win). The caller's dictionary is never mutated.
    /// </summary>
    private Dictionary<string, string> BuildLaunchEnvironment(
        IDictionary<string, string>? caller,
        IReadOnlyDictionary<string, string>? integration = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer)
        {
            ["TERM_PROGRAM"] = "dotty",
            ["TERM_PROGRAM_VERSION"] = DottyVersion.Describe(),
            ["DOTTY_SURFACE_ID"] = SurfaceId,
        };
        if (integration is not null)
        {
            foreach (KeyValuePair<string, string> pair in integration)
                environment[pair.Key] = pair.Value;
        }
        if (caller is not null)
        {
            foreach (KeyValuePair<string, string> pair in caller)
                environment[pair.Key] = pair.Value;
        }
        return environment;
    }
}
