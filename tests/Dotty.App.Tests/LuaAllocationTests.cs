using System;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Text;
using Xunit;

namespace Dotty.App.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class LuaAllocationTests : IDisposable
{
    private readonly DottyUserConfig _config = new();
    private readonly TerminalTabManager _tabManager = new();
    private readonly FakeLuaHostServices _services;
    private readonly LuaScriptHost _host;

    public LuaAllocationTests()
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
    public void TryFormatStatusDoesNotAllocateAfterWarmup()
    {
        Assert.True(_host.ExecuteString("dotty.on('update_status', function() return 'fixed status' end)"));
        var buffer = new ReusableTextBuffer();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(_host.Hooks.TryFormatStatus(buffer));
        }

        string expected = buffer.Span.ToString();
        bool available = true;
        AllocationAssert.NoAllocations(
            () =>
            {
                for (int i = 0; i < 20; i++)
                    available &= _host.Hooks.TryFormatStatus(buffer);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 4);

        Assert.True(available);
        Assert.Equal(expected, buffer.Span.ToString());
    }

    [Fact]
    public void TryFormatTabTitleDoesNotAllocateAfterWarmup()
    {
        TerminalTab tab = _tabManager.CreateTab();
        Assert.True(_host.ExecuteString("dotty.on('format_tab_title', function() return 'fixed title' end)"));
        var buffer = new ReusableTextBuffer();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(_host.Hooks.TryFormatTabTitle(tab, 0, buffer));
        }

        string expected = buffer.Span.ToString();
        bool available = true;
        AllocationAssert.NoAllocations(
            () =>
            {
                for (int i = 0; i < 20; i++)
                    available &= _host.Hooks.TryFormatTabTitle(tab, 0, buffer);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 4);

        Assert.True(available);
        Assert.Equal(expected, buffer.Span.ToString());
    }

    [Fact]
    public void EventQueueAndEmitDoNotAllocateAfterWarmup()
    {
        TerminalTab tab = _tabManager.CreateTab();
        Assert.True(_host.ExecuteString("dotty.on('tab_title_changed', function() end)"));
        _services.Drain();

        const string first = "first event title";
        const string second = "second event title";
        for (int i = 0; i < 20; i++)
        {
            tab.Title = (i & 1) == 0 ? first : second;
            _services.Drain();
        }

        int titleChange = 0;
        AllocationAssert.NoAllocations(
            () =>
            {
                tab.Title = (titleChange++ & 1) == 0 ? first : second;
                _services.Drain();
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 4);
    }

    [Fact]
    public void HandlersAndEventsWithoutHandlersDoNotAllocateAfterWarmup()
    {
        TerminalTab tab = _tabManager.CreateTab();
        _services.Drain();
        Assert.False(_host.Hooks.HasHandlers("tab_title_changed"));
        Assert.True(_host.ExecuteString("dotty.on('tab_title_changed', function() end)"));

        for (int i = 0; i < 20; i++)
        {
            _host.Hooks.HasHandlers("tab_title_changed");
            _host.Hooks.HasHandlers("missing_event");
        }

        bool hasHandler = false;
        bool hasMissingHandler = false;
        AllocationAssert.NoAllocations(
            () =>
            {
                hasHandler = _host.Hooks.HasHandlers("tab_title_changed");
                hasMissingHandler = _host.Hooks.HasHandlers("missing_event");
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 4);

        Assert.True(hasHandler);
        Assert.False(hasMissingHandler);

        Assert.True(_host.ExecuteString("dotty.on('temporary', function() end)"));
        _host.Hooks.Clear();
        for (int i = 0; i < 20; i++)
        {
            tab.Title = (i & 1) == 0 ? "no handler A" : "no handler B";
            _services.Drain();
        }

        int noHandlerTitleChange = 0;
        AllocationAssert.NoAllocations(
            () => tab.Title = (noHandlerTitleChange++ & 1) == 0 ? "no handler A" : "no handler B",
            warmupIterations: 0,
            measuredIterationsPerWindow: 4);
    }
}
