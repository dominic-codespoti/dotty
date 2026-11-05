using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dotty.Runtime.Config;
using Dotty.Runtime.Hosting;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public sealed class VtHostPolicyTests
{
    private const string Visible = "\u001b[?999;1n";
    private const string Hidden = "\u001b[?999;2n";

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void IsSessionVisible_TruthTable(bool iconified, bool inActiveTab, bool expected) =>
        Assert.Equal(expected, VtHostPolicy.IsSessionVisible(iconified, inActiveTab));

    [Fact]
    public void TryComputeResize_AllowsASingleNormalPane()
    {
        Assert.True(VtHostPolicy.TryComputeResize(true, 1, 1, VtWindowState.Normal, 30, 100, 24, 80, out int rows, out int cols));
        Assert.Equal((30, 100), (rows, cols));
    }

    [Theory]
    [InlineData(30, 0, 30, 80)]
    [InlineData(0, 100, 24, 100)]
    public void TryComputeResize_ZeroKeepsTheCurrentDimension(int requestedRows, int requestedCols, int expectedRows, int expectedCols)
    {
        Assert.True(VtHostPolicy.TryComputeResize(true, 1, 1, VtWindowState.Normal, requestedRows, requestedCols, 24, 80, out int rows, out int cols));
        Assert.Equal((expectedRows, expectedCols), (rows, cols));
    }

    [Theory]
    [InlineData(false, 1, 1, VtWindowState.Normal)] // gate off (default)
    [InlineData(true, 2, 1, VtWindowState.Normal)] // several tabs
    [InlineData(true, 0, 1, VtWindowState.Normal)] // no tab
    [InlineData(true, 1, 2, VtWindowState.Normal)] // split panes
    [InlineData(true, 1, 1, VtWindowState.Maximized)]
    [InlineData(true, 1, 1, VtWindowState.Fullscreen)]
    [InlineData(true, 1, 1, VtWindowState.Minimized)]
    public void TryComputeResize_IgnoresGatedSituations(bool allowed, int tabs, int leaves, VtWindowState state)
    {
        Assert.False(VtHostPolicy.TryComputeResize(allowed, tabs, leaves, state, 30, 100, 24, 80, out int rows, out int cols));
        Assert.Equal((24, 80), (rows, cols));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(24, 80)] // already that size
    [InlineData(24, 0)]
    [InlineData(0, 80)]
    public void TryComputeResize_IgnoresEmptyAndNoOpRequests(int requestedRows, int requestedCols) =>
        Assert.False(VtHostPolicy.TryComputeResize(true, 1, 1, VtWindowState.Normal, requestedRows, requestedCols, 24, 80, out _, out _));

    [Fact]
    public void ResizeQueue_TakesTheLatestRequestOnce()
    {
        var queue = new VtResizeRequestQueue();
        Assert.False(queue.TryTake(out _, out _));

        queue.Post(30, 100);
        queue.Post(40, 120);
        Assert.True(queue.TryTake(out int rows, out int cols));
        Assert.Equal((40, 120), (rows, cols));
        Assert.False(queue.TryTake(out _, out _));
    }

    [Fact]
    public void ResizeQueue_MergesZeroAsKeep()
    {
        var queue = new VtResizeRequestQueue();
        queue.Post(30, 0);
        queue.Post(0, 100);
        Assert.True(queue.TryTake(out int rows, out int cols));
        Assert.Equal((30, 100), (rows, cols));
    }

    [Fact]
    public void ResizeQueue_IgnoresEmptyRequestsAndClears()
    {
        var queue = new VtResizeRequestQueue();
        queue.Post(0, 0);
        Assert.False(queue.TryTake(out _, out _));
        queue.Post(5, 5);
        queue.Clear();
        Assert.False(queue.TryTake(out _, out _));
    }

    [Fact]
    public async Task ResizeQueue_ConcurrentPostsAreNotLost()
    {
        var queue = new VtResizeRequestQueue();
        await Task.WhenAll(
            Task.Run(() => { for (int i = 1; i <= 1000; i++) queue.Post(i, 0); }, TestContext.Current.CancellationToken),
            Task.Run(() => { for (int i = 1; i <= 1000; i++) queue.Post(0, i); }, TestContext.Current.CancellationToken));
        Assert.True(queue.TryTake(out int rows, out int cols));
        Assert.Equal((1000, 1000), (rows, cols));
    }

    [Fact]
    public void ApplyVisibility_TabSwitchReportsHiddenThenVisible()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var first = manager.CreateTab();
        var second = manager.CreateTab();
        Assert.Same(second, manager.ActiveTab);

        var firstReplies = Record(first.Session.Adapter);
        var secondReplies = Record(second.Session.Adapter);
        first.Session.Adapter.OnSetVisibilityReports(true);
        second.Session.Adapter.OnSetVisibilityReports(true);
        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);
        firstReplies.Clear();
        secondReplies.Clear();
        Assert.False(first.Session.Adapter.IsVisible);
        Assert.True(second.Session.Adapter.IsVisible);

        manager.SelectTab(first);
        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);

        Assert.Equal(new[] { Visible }, firstReplies);
        Assert.Equal(new[] { Hidden }, secondReplies);
    }

    [Fact]
    public void ApplyVisibility_IsEdgeTriggered()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab();
        var replies = Record(tab.Session.Adapter);
        tab.Session.Adapter.OnSetVisibilityReports(true);
        replies.Clear();

        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);
        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);
        Assert.Empty(replies);

        VtHostPolicy.ApplyVisibility(manager, windowIconified: true);
        VtHostPolicy.ApplyVisibility(manager, windowIconified: true);
        Assert.Equal(new[] { Hidden }, replies);

        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);
        Assert.Equal(new[] { Hidden, Visible }, replies);
    }

    [Fact]
    public void ApplyVisibility_CoversEveryPaneOfTheActiveTab()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab();
        var second = tab.PaneTree.Split(tab.PaneTree.ActivePane, SplitDirection.Vertical);

        VtHostPolicy.ApplyVisibility(manager, windowIconified: true);
        foreach (var leaf in tab.PaneTree.Leaves)
            Assert.False(leaf.Session.Adapter.IsVisible);

        VtHostPolicy.ApplyVisibility(manager, windowIconified: false);
        foreach (var leaf in tab.PaneTree.Leaves)
            Assert.True(leaf.Session.Adapter.IsVisible);
        Assert.True(second.Session.Adapter.IsVisible);
    }

    [Fact]
    public void ApplyVisibility_AcceptsNoManager() => VtHostPolicy.ApplyVisibility(null, windowIconified: true);

    [Fact]
    public void WindowResizeAllowed_DefaultsOffAndRoundTripsThroughJson()
    {
        Assert.False(new DottyUserConfig().Vt.WindowResizeAllowed);
        var config = JsonSerializer.Deserialize<DottyUserConfig>("{\"vt\":{\"windowResizeAllowed\":true}}")!;
        Assert.True(config.Vt.WindowResizeAllowed);
    }

    [Fact]
    public void WindowResizeAllowed_IsSettableFromLua()
    {
        var config = new DottyUserConfig();
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var host = new LuaScriptHost(new FakeLuaHostServices(manager), manager);
        Assert.True(host.Evaluate(config, null));

        Assert.True(host.ExecuteString("dotty.config.vt.window_resize_allowed=true; assert(dotty.config.vt.window_resize_allowed==true)"));
        Assert.True(config.Vt.WindowResizeAllowed);
        Assert.True(host.ExecuteString("dotty.config.apply_table({vt={window_resize_allowed=false}})"));
        Assert.False(config.Vt.WindowResizeAllowed);
    }

    private static List<string> Record(Dotty.Terminal.Adapter.TerminalAdapter adapter)
    {
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return replies;
    }
}
