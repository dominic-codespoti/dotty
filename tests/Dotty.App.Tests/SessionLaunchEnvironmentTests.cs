using System.Collections.Generic;
using Dotty.Runtime;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public sealed class SessionLaunchEnvironmentTests
{
    private static (TerminalSession Session, List<SilentPty> Ptys) CreateSession()
    {
        var ptys = new List<SilentPty>();
        var session = new TerminalSession(24, 80, () =>
        {
            var pty = new SilentPty();
            ptys.Add(pty);
            return pty;
        });
        return (session, ptys);
    }

    [Theory]
    [InlineData("0.3.0+abc123", "0.3.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.1")]
    [InlineData("0.4.0-beta.1+sha.5114f85", "0.4.0-beta.1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripBuildMetadata_RemovesOnlyBuildMetadata(string? input, string expected) =>
        Assert.Equal(expected, DottyVersion.StripBuildMetadata(input));

    [Fact]
    public void Current_HasNoBuildMetadataAndIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(DottyVersion.Current));
        Assert.DoesNotContain('+', DottyVersion.Current);
    }

    [Fact]
    public void DefaultConfig_LaunchesWithoutShellIntegration()
    {
        var (session, ptys) = CreateSession();
        using var leaf = new LeafPane(session);

        session.StartWithOptions(shell: "/bin/bash");

        SilentPty pty = Assert.Single(ptys);
        Assert.Null(pty.StartedCommand);
        Assert.False(pty.StartedEnvironment!.ContainsKey("DOTTY_SHELL_INTEGRATION"));
        Assert.False(pty.StartedEnvironment!.ContainsKey("ZDOTDIR"));
    }

    [Fact]
    public void Start_SetsTermProgramVersionAndSurfaceId()
    {
        var (session, ptys) = CreateSession();
        using var leaf = new LeafPane(session);

        session.Start();

        IDictionary<string, string> env = Assert.Single(ptys).StartedEnvironment!;
        Assert.Equal("dotty", env["TERM_PROGRAM"]);
        Assert.Equal(DottyVersion.Current, env["TERM_PROGRAM_VERSION"]);
        Assert.Equal(leaf.Id, env["DOTTY_SURFACE_ID"]);
        Assert.Matches("^[0-9a-f]{32}$", env["DOTTY_SURFACE_ID"]);
    }

    [Fact]
    public void StartWithOptions_SetsBaseEnvironmentAndCallerOverridesWin()
    {
        var (session, ptys) = CreateSession();
        using var leaf = new LeafPane(session);
        var caller = new Dictionary<string, string>
        {
            ["TERM_PROGRAM"] = "custom",
            ["FOO"] = "bar",
        };

        session.StartWithOptions(shell: "/bin/sh", environmentVariables: caller);

        IDictionary<string, string> env = Assert.Single(ptys).StartedEnvironment!;
        Assert.Equal("custom", env["TERM_PROGRAM"]);
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal(DottyVersion.Current, env["TERM_PROGRAM_VERSION"]);
        Assert.Equal(leaf.Id, env["DOTTY_SURFACE_ID"]);
        Assert.Equal(2, caller.Count);
        Assert.Equal("custom", caller["TERM_PROGRAM"]);
    }

    [Fact]
    public void SurfaceId_IsDistinctPerPaneAndStableAcrossSplits()
    {
        using var manager = new TerminalTabManager(() => new SilentPty());
        var tab = manager.CreateTab(shell: "/bin/sh");
        LeafPane first = tab.PaneTree.ActivePane;
        string firstId = first.Id;
        Assert.Equal(firstId, first.Session.SurfaceId);

        LeafPane second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        LeafPane third = tab.PaneTree.Split(second, SplitDirection.Horizontal);
        tab.PaneTree.Layout(800, 600, 8, 16);

        Assert.Equal(firstId, first.Id);
        Assert.Equal(firstId, first.Session.SurfaceId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(second.Id, third.Id);
        Assert.NotEqual(first.Id, third.Id);
        Assert.Equal(second.Id, second.Session.SurfaceId);
    }

    [Fact]
    public void LeafPane_ExplicitIdIsExportedAsSurfaceId()
    {
        var (session, ptys) = CreateSession();
        using var leaf = new LeafPane(session, "pane-explicit");

        session.Start();

        Assert.Equal("pane-explicit", ptys[0].StartedEnvironment!["DOTTY_SURFACE_ID"]);
    }
}
