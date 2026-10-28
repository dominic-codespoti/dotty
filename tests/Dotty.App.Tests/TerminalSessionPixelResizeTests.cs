using System.Collections.Generic;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Pixel sizes travel from the host through <see cref="TerminalSession.Resize"/> to <see cref="Dotty.Abstractions.Pty.IPty.Resize"/>.</summary>
public sealed class TerminalSessionPixelResizeTests
{
    private static (TerminalSession Session, SilentPty Pty, LeafPane Leaf) CreateStartedSession()
    {
        SilentPty? pty = null;
        var session = new TerminalSession(24, 80, () => pty = new SilentPty());
        var leaf = new LeafPane(session);
        session.Start();
        return (session, pty!, leaf);
    }

    [Fact]
    public void PixelOnlyChange_IsForwarded()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(100, 30, 1000, 600);
            session.Resize(100, 30, 1100, 660);

            Assert.Equal(
                new[] { (100, 30, 1000, 600), (100, 30, 1100, 660) },
                pty.Resizes());
        }
    }

    [Fact]
    public void IdenticalGridAndPixels_IsNotForwardedAgain()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(100, 30, 1000, 600);
            session.Resize(100, 30, 1000, 600);
            session.Resize(100, 30, 1000, 600);

            Assert.Single(pty.Resizes());
        }
    }

    [Fact]
    public void FirstResizeEqualToStartupGridButWithPixels_IsForwarded()
    {
        // The PTY started with the 80x24 grid and unknown (0) pixels: learning the pixel size is a real change.
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(80, 24, 640, 384);

            Assert.Equal(new[] { (80, 24, 640, 384) }, pty.Resizes());
        }
    }

    [Fact]
    public void FirstResizeEqualToStartupGridAndUnknownPixels_IsNotForwarded()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(80, 24, 0, 0);

            Assert.Empty(pty.Resizes());
        }
    }

    [Fact]
    public void GridOnlyChange_KeepsPixelArguments()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(100, 30, 1000, 600);
            session.Resize(101, 30, 1000, 600);

            Assert.Equal(
                new[] { (100, 30, 1000, 600), (101, 30, 1000, 600) },
                pty.Resizes());
        }
    }

    [Fact]
    public void NegativePixels_AreTreatedAsUnknown()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(80, 24, -5, -1);
            Assert.Empty(pty.Resizes());

            session.Resize(100, 30, -5, 600);
            Assert.Equal(new[] { (100, 30, 0, 600) }, pty.Resizes());
        }
    }

    [Fact]
    public void DroppingPixelsBackToUnknown_IsForwarded()
    {
        var (session, pty, leaf) = CreateStartedSession();
        using (leaf)
        {
            session.Resize(100, 30, 1000, 600);
            session.Resize(100, 30, 0, 0);

            Assert.Equal(
                new[] { (100, 30, 1000, 600), (100, 30, 0, 0) },
                pty.Resizes());
        }
    }

    [Fact]
    public void ResizeAll_PassesPixelsToEverySession()
    {
        var ptys = new List<SilentPty>();
        using var manager = new TerminalTabManager(() => { var p = new SilentPty(); ptys.Add(p); return p; });
        manager.CreateTab(cols: 80, rows: 24);
        manager.CreateTab(cols: 80, rows: 24);

        manager.ResizeAll(120, 40, 1200, 800);

        Assert.Equal(2, ptys.Count);
        foreach (var pty in ptys)
            Assert.Equal(new[] { (120, 40, 1200, 800) }, pty.Resizes());
    }

    [Fact]
    public void PaneLayout_ForwardsGridTimesRoundedCellPixels()
    {
        var ptys = new List<SilentPty>();
        using var manager = new TerminalTabManager(() => { var p = new SilentPty(); ptys.Add(p); return p; });
        var tab = manager.CreateTab(cols: 80, rows: 24);

        tab.PaneTree.Layout(800, 600, 10, 20);

        Assert.Equal(new[] { (80, 30, 800, 600) }, Assert.Single(ptys).Resizes());
    }

    [Fact]
    public void PaneLayout_PixelOnlyChangeWithSameGridIsForwardedOnce()
    {
        var ptys = new List<SilentPty>();
        using var manager = new TerminalTabManager(() => { var p = new SilentPty(); ptys.Add(p); return p; });
        var tab = manager.CreateTab(cols: 80, rows: 24);
        SilentPty pty = Assert.Single(ptys);

        // 1000 / 10.49 and 1000 / 10.51 both floor to 95 columns, but the rounded cell width goes 10 -> 11 px.
        tab.PaneTree.Layout(1000, 600, 10.49f, 20);
        tab.PaneTree.Layout(1000, 600, 10.51f, 20);
        tab.PaneTree.Layout(1000, 600, 10.51f, 20);

        Assert.Equal(
            new[] { (95, 30, 950, 600), (95, 30, 1045, 600) },
            pty.Resizes());
    }
}
