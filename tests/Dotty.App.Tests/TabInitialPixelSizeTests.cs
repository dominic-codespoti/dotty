using System.Collections.Generic;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// A new tab starts its PTY with the text-area pixel size of the latest layout, so the child's first
/// <c>TIOCGWINSZ</c> reports pixels (graphics clients derive image sizes from it).
/// </summary>
public sealed class TabInitialPixelSizeTests
{
    private static TerminalTabManager NewManager(List<SilentPty> ptys) =>
        new(() =>
        {
            var pty = new SilentPty();
            ptys.Add(pty);
            return pty;
        });

    [Fact]
    public void ATabCreatedBeforeAnyLayoutStartsItsPtyWithoutAPixelSize()
    {
        var ptys = new List<SilentPty>();
        using var tabs = NewManager(ptys);

        tabs.CreateTab(cols: 100, rows: 30);

        Assert.Equal((0, 0), ptys[0].StartedPixelSize);
    }

    [Fact]
    public void ALaterTabStartsItsPtyWithTheCellSizeOfTheLastLayoutTimesItsOwnGrid()
    {
        var ptys = new List<SilentPty>();
        using var tabs = NewManager(ptys);
        tabs.CreateTab(cols: 80, rows: 24);

        tabs.ResizeAll(80, 24, 80 * 9, 24 * 18); // cells of 9x18 physical pixels
        tabs.CreateTab(cols: 100, rows: 30);

        Assert.Equal((100 * 9, 30 * 18), ptys[1].StartedPixelSize);
    }

    [Fact]
    public void ALaterLayoutReplacesTheRememberedCellSize()
    {
        var ptys = new List<SilentPty>();
        using var tabs = NewManager(ptys);
        tabs.ResizeAll(80, 24, 80 * 9, 24 * 18);
        tabs.ResizeAll(80, 24, 80 * 12, 24 * 24); // zoomed

        tabs.CreateTab(cols: 80, rows: 24);

        Assert.Equal((80 * 12, 24 * 24), ptys[0].StartedPixelSize);
    }

    [Fact]
    public void ALayoutWithoutPixelSizesKeepsTheRememberedCellSize()
    {
        var ptys = new List<SilentPty>();
        using var tabs = NewManager(ptys);
        tabs.ResizeAll(80, 24, 80 * 9, 24 * 18);
        tabs.ResizeAll(80, 24, 0, 0);

        tabs.CreateTab(cols: 80, rows: 24);

        Assert.Equal((80 * 9, 24 * 18), ptys[0].StartedPixelSize);
    }

    [Fact]
    public void TheFirstLayoutResizeOfATabThatStartedWithTheSamePixelSizeIsNotForwardedAgain()
    {
        var ptys = new List<SilentPty>();
        using var tabs = NewManager(ptys);
        tabs.CreateTab(cols: 80, rows: 24);
        tabs.ResizeAll(80, 24, 80 * 9, 24 * 18);
        tabs.CreateTab(cols: 100, rows: 30);

        tabs.ResizeAll(100, 30, 100 * 9, 30 * 18);

        Assert.Empty(ptys[1].Resizes());      // it already started with exactly this size
        Assert.Equal(2, ptys[0].Resizes().Length); // the first tab learned its pixels and then its grid
    }
}

public sealed class SplitInitialPixelSizeTests
{
    [Fact]
    public void ASplitStartsItsPtyWithThePixelSizeOfTheTargetPane()
    {
        var ptys = new List<SilentPty>();
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: () =>
        {
            var pty = new SilentPty();
            ptys.Add(pty);
            return pty;
        });
        var target = tree.ActivePane;
        target.PixelWidth = 80 * 9;
        target.PixelHeight = 24 * 18;

        tree.Split(target, SplitDirection.Vertical, workingDirectory: "/", shell: "/bin/sh");

        var started = Assert.Single(ptys);
        Assert.Equal((80 * 9, 24 * 18), started.StartedPixelSize);
    }
}
