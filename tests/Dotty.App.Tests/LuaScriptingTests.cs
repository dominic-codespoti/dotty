using System;
using System.IO;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public class LuaScriptingTests : IDisposable
{
    private readonly DottyUserConfig _config;
    private readonly TerminalTabManager _tabManager;
    private readonly LuaScriptHost _host;

    public LuaScriptingTests()
    {
        _config = new DottyUserConfig();
        _tabManager = new TerminalTabManager();
        _host = new LuaScriptHost();
        _host.Initialize(_config, _tabManager);
    }

    public void Dispose()
    {
        _host.Dispose();
        _tabManager.Dispose();
    }

    [Fact]
    public void Initialize_ExposesDottyGlobalAndRequiresDottyModule()
    {
        bool executed = _host.ExecuteString(@"
            local d = require('dotty')
            assert(d ~= nil, 'dotty module should be available')
            assert(d.config ~= nil, 'dotty.config should be available')
            assert(d.tabs ~= nil, 'dotty.tabs should be available')
        ");
        Assert.True(executed);
    }

    [Fact]
    public void LuaConfig_MutatesDottyUserConfig()
    {
        bool executed = _host.ExecuteString(@"
            local dotty = require('dotty')
            dotty.config.theme = 'Dracula'
            dotty.config.apply_table({
                font = {
                    family = 'Fira Code, monospace',
                    size = 16.0,
                    line_height = 1.3
                },
                window = {
                    padding = { left = 20, top = 12, right = 20, bottom = 12 }
                }
            })
        ");

        Assert.True(executed);
        Assert.Equal("Dracula", _config.Theme);
        Assert.Equal("Fira Code, monospace", _config.Font.Family);
        Assert.Equal(16.0, _config.Font.Size);
        Assert.Equal(1.3, _config.Font.LineHeight);
        Assert.Equal(20.0, _config.Window.Padding.Left);
        Assert.Equal(12.0, _config.Window.Padding.Top);
    }

    [Fact]
    public void LuaTabs_CreatesAndSelectsTabs()
    {
        _tabManager.CreateTab(cols: 80, rows: 24);

        string luaDirectory = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/');

        bool executed = _host.ExecuteString($@"
            local dotty = require('dotty')
            assert(dotty.tabs.count >= 1)
            assert(dotty.tabs.active_index == 1)

            local new_tab = dotty.tabs.new({{ cwd = '{luaDirectory}' }})
            assert(new_tab ~= nil)
            assert(dotty.tabs.count == 2)
            assert(dotty.tabs.active_index == 2)

            dotty.tabs.select(1)
            assert(dotty.tabs.active_index == 1)
        ");

        Assert.True(executed);
        Assert.Equal(2, _tabManager.Count);
        Assert.Equal(0, _tabManager.ActiveIndex);
    }

    [Fact]
    public void LuaKeybinds_RegistersAndExecutesChords()
    {
        _host.ExecuteString(@"
            local dotty = require('dotty')
            dotty.bind('ctrl+shift+g', function()
                dotty.log('Triggered git status keybind')
            end)
        ");

        bool handled = _host.Keybinds.TryExecute(ctrl: true, shift: true, alt: false, super: false, keyName: "G");
        Assert.True(handled);

        bool notHandled = _host.Keybinds.TryExecute(ctrl: true, shift: false, alt: false, super: false, keyName: "G");
        Assert.False(notHandled);
    }

    [Fact]
    public void LuaHooks_ExecutesFormatTabTitleHook()
    {
        var tab = _tabManager.CreateTab(cols: 80, rows: 24);
        tab.Title = "nvim src/main.rs";

        _host.ExecuteString(@"
            local dotty = require('dotty')
            dotty.on('format_tab_title', function(t)
                return ' ' .. t.index .. ': ' .. t.title
            end)
        ");

        string? formatted = _host.Hooks.FormatTabTitle(tab, 0);
        Assert.NotNull(formatted);
        Assert.StartsWith(" 1:", formatted);
    }
}
