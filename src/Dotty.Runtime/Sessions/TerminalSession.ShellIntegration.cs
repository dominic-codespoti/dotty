namespace Dotty.Runtime.Sessions;

public partial class TerminalSession
{
    /// <summary>Current shell directory when reported, otherwise the initial launch directory.</summary>
    public string? CurrentWorkingDirectory => Adapter.CurrentWorkingDirectory ?? LaunchWorkingDirectory;
}
