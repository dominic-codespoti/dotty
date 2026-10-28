using System;
using System.IO;

namespace Dotty.Runtime.Sessions;

public partial class TerminalSession
{
    /// <summary>Current shell directory when reported, otherwise the initial launch directory.</summary>
    public string? CurrentWorkingDirectory => Adapter.CurrentWorkingDirectory ?? LaunchWorkingDirectory;

    /// <summary>
    /// Directory a new tab/pane should start in: the OSC 7 directory, else the launch directory,
    /// else the default directory, taking the first that exists. Never null, never throws, at most
    /// two filesystem probes. Call on the UI thread at action time, never from the parser.
    /// </summary>
    public string InheritableWorkingDirectory
    {
        get
        {
            string? live = Adapter.CurrentWorkingDirectory;
            if (live is not null && DirectoryExists(live)) return live;
            string launch = LaunchWorkingDirectory;
            if (DirectoryExists(launch)) return launch;
            return GetDefaultLaunchWorkingDirectory();
        }
    }

    private static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
