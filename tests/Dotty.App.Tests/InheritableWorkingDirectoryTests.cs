using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

public sealed class InheritableWorkingDirectoryTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string MakeDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "dotty-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static void Osc7(TerminalSession session, string directory) =>
        session.Parser.Feed(Encoding.UTF8.GetBytes("\u001b]7;" + new Uri(directory).AbsoluteUri + "\u001b\\"));

    private static string MissingPath() =>
        Path.Combine(Path.GetTempPath(), "dotty-missing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingOsc7DirectoryIsInherited()
    {
        string launch = MakeDir();
        string live = MakeDir();
        using var session = new TerminalSession(2, 8, SilentPty.Create);
        session.StartWithOptions(workingDirectory: launch);
        Osc7(session, live);
        Assert.Equal(Path.GetFullPath(live), session.InheritableWorkingDirectory);
    }

    [Fact]
    public void DeletedOsc7DirectoryFallsBackToLaunchDirectory()
    {
        string launch = MakeDir();
        string live = MakeDir();
        using var session = new TerminalSession(2, 8, SilentPty.Create);
        session.StartWithOptions(workingDirectory: launch);
        Osc7(session, live);
        Directory.Delete(live);
        Assert.Equal(session.LaunchWorkingDirectory, session.InheritableWorkingDirectory);
    }

    [Fact]
    public void NeverExistingOsc7DirectoryFallsBackToLaunchDirectory()
    {
        string launch = MakeDir();
        using var session = new TerminalSession(2, 8, SilentPty.Create);
        session.StartWithOptions(workingDirectory: launch);
        Osc7(session, MissingPath());
        Assert.Equal(session.LaunchWorkingDirectory, session.InheritableWorkingDirectory);
    }

    [Fact]
    public void MissingLaunchDirectoryFallsBackToDefaultDirectory()
    {
        string launch = MakeDir();
        using var session = new TerminalSession(2, 8, SilentPty.Create);
        session.StartWithOptions(workingDirectory: launch);
        Directory.Delete(launch);
        string resolved = session.InheritableWorkingDirectory;
        Assert.False(string.IsNullOrEmpty(resolved));
        Assert.Equal(TerminalSession.GetDefaultLaunchWorkingDirectory(), resolved);
    }

    [Fact]
    public void LaunchDirectoryIsKeptWithoutOsc7()
    {
        string launch = MakeDir();
        using var session = new TerminalSession(2, 8, SilentPty.Create);
        session.StartWithOptions(workingDirectory: launch);
        Assert.Equal(Path.GetFullPath(launch), session.InheritableWorkingDirectory);
    }

    [Fact]
    public void SplitWithDeletedOsc7DirectoryStartsInLaunchDirectory()
    {
        string launch = MakeDir();
        string live = MakeDir();
        var ptys = new List<SilentPty>();
        using var tree = new PaneTree(() => { var pty = new SilentPty(); ptys.Add(pty); return pty; }, workingDirectory: launch);
        var root = tree.Leaves[0];
        Osc7(root.Session, live);
        Directory.Delete(live);

        var pane = tree.Split(root, SplitDirection.Vertical);

        Assert.Equal(2, ptys.Count);
        Assert.Equal(root.Session.LaunchWorkingDirectory, ptys[1].StartedWorkingDirectory);
        Assert.Equal(root.Session.LaunchWorkingDirectory, pane.Session.LaunchWorkingDirectory);
    }
}
