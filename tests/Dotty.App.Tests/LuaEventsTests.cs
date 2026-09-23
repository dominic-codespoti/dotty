using System;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public sealed class LuaEventsTests : IDisposable
{
    private readonly DottyUserConfig _config = new();
    private readonly TerminalTabManager _tabManager = new();
    private readonly FakeLuaHostServices _services;
    private readonly LuaScriptHost _host;

    public LuaEventsTests()
    {
        _services = new FakeLuaHostServices(_tabManager);
        _host = new LuaScriptHost(_services, _tabManager);
        Assert.True(_host.Evaluate(_config, null));
    }

    public void Dispose()
    {
        _host.Dispose();
        _tabManager.Dispose();
    }

    [Fact]
    public void TabAddedAndActivatedDeliverReadableTabHandles()
    {
        Assert.True(_host.ExecuteString(@"
            dotty.on('tab_added', function(tab)
                dotty.log('added:' .. type(tab.title) .. ':' .. tab.index)
            end)
            dotty.on('tab_activated', function(tab)
                dotty.log('activated:' .. type(tab.title) .. ':' .. tab.index)
            end)"));

        var tab = _tabManager.CreateTab();
        tab.Title = "new tab";
        _services.Drain();

        Assert.Contains(_services.Messages, message => message.Message == "added:string:1");
        Assert.Contains(_services.Messages, message => message.Message == "activated:string:1");
    }

    [Fact]
    public void TabTitleChangedDeliversTitleCapturedWhenQueued()
    {
        var tab = _tabManager.CreateTab();
        Assert.True(_host.ExecuteString("dotty.on('tab_title_changed', function(tab, title) dotty.log(title) end)"));

        tab.Title = "first explicit title";
        tab.Title = "second explicit title";
        _services.Drain();

        Assert.Contains(_services.Messages, message => message.Message == "first explicit title");
        Assert.Contains(_services.Messages, message => message.Message == "second explicit title");
    }

    [Fact]
    public void BellFromSplitPaneReachesLuaAndTabManager()
    {
        var tab = _tabManager.CreateTab();
        var splitPane = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Horizontal);
        _tabManager.CreateTab();
        _tabManager.SelectTab(_tabManager.Tabs[1]);
        Assert.True(_host.ExecuteString("dotty.on('bell', function(eventTab, pane) dotty.log(eventTab.title .. ':' .. pane.id) end)"));
        bool bellRung = false;
        _tabManager.BellRung += (eventTab, pane) => bellRung = ReferenceEquals(eventTab, tab) && ReferenceEquals(pane, splitPane);

        splitPane.Session.Adapter.OnBell();
        _services.Drain();

        Assert.Contains(_services.Messages, message => message.Message == $"{tab.Title}:{splitPane.Id}");
        Assert.True(bellRung);
        Assert.True(tab.HasBellAlert);
    }

    [Fact]
    public void EventsWithoutHandlersDoNotQueueLuaWork()
    {
        var tab = _tabManager.CreateTab();
        tab.Title = "no handler";

        Assert.True(_host.ExecuteString("dotty.on('tab_title_changed', function(_, title) dotty.log(title) end)"));
        _services.Drain();
        Assert.Empty(_services.Messages);
    }

    [Fact]
    public void ReevaluateDetachesHandlersAndInvalidatesQueuedEvents()
    {
        Assert.True(_host.ExecuteString("dotty.on('tab_added', function() dotty.log('stale') end)"));
        _tabManager.CreateTab();

        Assert.True(_host.Evaluate(_config, null));
        _services.Drain();
        Assert.DoesNotContain(_services.Messages, message => message.Message == "stale");

        _tabManager.CreateTab();
        _services.Drain();
        Assert.DoesNotContain(_services.Messages, message => message.Message == "stale");
    }
}
