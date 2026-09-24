using System;
using System.IO;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public sealed class LuaPaneApiTests : IDisposable
{
    private readonly TerminalTabManager _tabManager = new();
    private readonly LuaScriptHost _host;

    public LuaPaneApiTests()
    {
        var services = new FakeLuaHostServices(_tabManager);
        _host = new LuaScriptHost(services, _tabManager);
        Assert.True(_host.Evaluate(new DottyUserConfig(), null));
    }

    public void Dispose()
    {
        _host.Dispose();
        _tabManager.Dispose();
    }

    [Fact]
    public void TabsAllAndGetExposeOneBasedTabHandles()
    {
        _tabManager.CreateTab();
        _tabManager.CreateTab();

        Assert.True(_host.ExecuteString(@"
            local tabs = dotty.tabs.all()
            assert(#tabs == 2)
            assert(tabs[1].id == dotty.tabs.get(1).id)
            assert(tabs[2].id == dotty.tabs.get(2).id)
            assert(dotty.tabs.get(0) == nil)
            assert(dotty.tabs.get(3) == nil)
            assert(tabs[1].index == 1 and tabs[2].index == 2)
        "));
    }

    [Fact]
    public void ClosedTabHandleDegradesSafely()
    {
        _tabManager.CreateTab();

        Assert.True(_host.ExecuteString(@"
            local tab = dotty.tabs.active
            assert(tab:close())
            assert(not tab.is_valid)
            assert(tab:close() == false)
            assert(tab:select() == false)
            assert(tab:send('x') == false)
            assert(tab:panes() == nil)
            assert(tab.index == nil and tab.title == nil and tab.cwd == nil)
            assert(tab.active_pane == nil)
        "));
    }

    [Fact]
    public void PaneSplitUsesHostServiceAndPanesFollowLeavesOrder()
    {
        var tab = _tabManager.CreateTab();

        Assert.True(_host.ExecuteString(@"
            local tab = dotty.tabs.active
            local first = tab.active_pane
            assert(type(tab.id) == 'string' and tab.is_valid and tab.index == 1)
            assert(tab.is_active and not tab.has_bell)
            local second = first:split('right')
            local panes = tab:panes()
            assert(#panes == 2)
            assert(panes[1].id == first.id and panes[2].id == second.id)
            assert(panes[1].index == 1 and panes[2].index == 2)
            assert(second.tab.id == tab.id)
            assert(second.cols > 0 and second.rows > 0 and second.is_valid)
            assert(second.is_active and second.is_alt_screen == false)
            assert(second.cursor.row >= 1 and second.cursor.col >= 1)
            assert(second.scroll_offset == 0 and second:selection() == nil)
        "));
        Assert.Equal(2, tab.PaneTree.Leaves.Count);
        Assert.True(tab.PaneTree.ActivePane.Columns > 0);
    }

    [Fact]
    public void PaneFocusAndCloseUpdateTreeAndInvalidateClosedHandle()
    {
        var tab = _tabManager.CreateTab();
        var firstPane = tab.PaneTree.ActivePane;

        Assert.True(_host.ExecuteString(@"
            local tab = dotty.tabs.active
            local first = tab.active_pane
            local second = first:split('down')
            assert(first:focus())
            assert(tab.active_pane.id == first.id)
            assert(second:close())
            assert(not second.is_valid)
            assert(#tab:panes() == 1)
            assert(second:close() == false)
        "));
        Assert.Single(tab.PaneTree.Leaves);
        Assert.Same(firstPane, tab.PaneTree.ActivePane);
    }

    [Fact]
    public void InvalidPaneSplitDirectionRaisesLuaErrorListingDirections()
    {
        var tab = _tabManager.CreateTab();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            _host.ExecuteString("dotty.tabs.active.active_pane:split('left')"));

        Assert.Contains("right", exception.Message);
        Assert.Contains("down", exception.Message);
        Assert.Single(tab.PaneTree.Leaves);
    }

    [Fact]
    public void PaneTextReturnsVisibleAdapterTextWithoutTrailingBlankRows()
    {
        var tab = _tabManager.CreateTab(rows: 3, cols: 8);
        TestShellEnvironment.WaitForStartupOutput(tab.Session);
        tab.Session.Parser.Feed(Encoding.UTF8.GetBytes("hello   \r\nworld  "));

        Assert.True(_host.ExecuteString(@"
            local screen = dotty.tabs.active.active_pane:text()
            assert(screen == 'hello\nworld')
        "));
    }

    [Fact]
    public void PaneMutationsAreUnavailableDuringConfigEvaluation()
    {
        _tabManager.CreateTab();
        string path = Path.Combine(Path.GetTempPath(), $"dotty-pane-api-{Guid.NewGuid():N}.lua");
        File.WriteAllText(path, @"
            local pane = dotty.tabs.active.active_pane
            local calls = {
                function() pane:send('x') end,
                function() pane:split('right') end,
                function() pane:focus() end,
                function() pane:close() end
            }
            for _, call in ipairs(calls) do
                local ok, message = pcall(call)
                assert(not ok and message:find('is not available while config.lua is being evaluated'))
            end
        ");
        try
        {
            Assert.True(_host.Evaluate(new DottyUserConfig(), path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
