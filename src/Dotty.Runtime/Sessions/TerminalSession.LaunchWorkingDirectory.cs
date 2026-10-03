using System;
using System.IO;

namespace Dotty.Runtime.Sessions;

public partial class TerminalSession
{
    /// <summary>Absolute initial PTY working directory, before shell integration reports live cwd.</summary>
    public string LaunchWorkingDirectory { get; private set; } = Path.GetFullPath(GetDefaultLaunchWorkingDirectory());

    /// <summary>Resolved shell used for this session and inherited by new tabs/panes.</summary>
    public string? LaunchShell { get; private set; }
    public bool LaunchShellIsExecutable { get; private set; }

    private static string GetDefaultLaunchWorkingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Environment.CurrentDirectory;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home;
    }
}
