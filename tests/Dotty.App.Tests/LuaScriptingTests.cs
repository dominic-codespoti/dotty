using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Input;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Text;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Exercises Lua scripting integration with the runtime host.</summary>
public class LuaScriptingTests : IDisposable
{
    private readonly DottyUserConfig _config = new();
    private readonly TerminalTabManager _tabManager = new();
    private readonly FakeLuaHostServices _services;
    private readonly LuaScriptHost _host;

    /// <summary>Creates and initializes a Lua scripting test fixture.</summary>
    public LuaScriptingTests()
    {
        _services = new FakeLuaHostServices(_tabManager);
        _host = new LuaScriptHost(_services, _tabManager);
        Assert.True(_host.Evaluate(_config, null));
    }

    /// <summary>Releases the host and tab manager.</summary>
    public void Dispose()
    {
        _host.Dispose();
        _tabManager.Dispose();
    }

    /// <summary>Verifies the dotty module can be required from Lua.</summary>
    [Fact]
    public void Evaluate_ExposesDottyGlobalAndRequiresDottyModule()
    {
        Assert.True(_host.ExecuteString(@"local d=require('dotty'); assert(d and d.config and d.tabs)"));
    }

    /// <summary>Verifies Lua writes update the managed user configuration.</summary>
    [Fact]
    public void LuaConfig_MutatesDottyUserConfig()
    {
        Assert.True(_host.ExecuteString(@"
            local d=require('dotty'); d.config.theme='Dracula'
            d.config.apply_table({font={family='Fira Code, monospace',size=16.0,line_height=1.3},window={padding={left=20,top=12,right=20,bottom=12}}})"));
        Assert.Equal("Dracula", _config.Theme);
        Assert.Equal("Fira Code, monospace", _config.Font.Family);
        Assert.Equal(16.0, _config.Font.Size);
        Assert.Equal(1.3, _config.Font.LineHeight);
        Assert.Equal(20.0, _config.Window.Padding.Left);
        Assert.Equal(12.0, _config.Window.Padding.Top);
    }

    /// <summary>Verifies Lua can create and select tabs.</summary>
    [Fact]
    public void LuaTabs_CreatesAndSelectsTabs()
    {
        _tabManager.CreateTab(cols: 80, rows: 24);
        string cwd = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/');
        Assert.True(_host.ExecuteString($@"local d=require('dotty'); assert(d.tabs.count==1 and d.tabs.active_index==1); local t=d.tabs.new({{cwd='{cwd}'}}); assert(t and d.tabs.count==2 and d.tabs.active_index==2); d.tabs.select(1); assert(d.tabs.active_index==1)"));
        Assert.Equal(2, _tabManager.Count);
        Assert.Equal(0, _tabManager.ActiveIndex);
    }

    /// <summary>Verifies registered Lua keybind callbacks execute for matching chords.</summary>
    [Fact]
    public void LuaKeybinds_RegistersAndExecutesChords()
    {
        Assert.True(_host.ExecuteString(@"dotty.bind('ctrl+shift+g',function() dotty.log('Triggered git status keybind') end)"));
        Assert.True(_host.Keybinds.TryExecute(true, true, false, false, "G"));
        Assert.False(_host.Keybinds.TryExecute(true, false, false, false, "G"));
        Assert.Contains(_services.Messages, message => message.Message == "Triggered git status keybind");
    }

    /// <summary>Verifies chord normalization is shared by JSON and Lua keybindings.</summary>
    [Fact]
    public void KeybindingChordNormalization_IsSharedByJsonAndLua()
    {
        const string input = " Shift + CTRL + G ";
        Assert.True(KeybindingManager.TryNormalizeChord(input, out string jsonChord));
        Assert.Equal(jsonChord, LuaKeybindRegistry.NormalizeChord(input));
        var manager = new KeybindingManager();
        manager.ApplyCustomBindings(new Dictionary<string, string> { { input, nameof(TerminalAction.Copy) } });
        Assert.True(manager.TryGetAction(true, true, false, false, "g", out TerminalAction action));
        Assert.Equal(TerminalAction.Copy, action);
        Assert.True(_host.ExecuteString(@"dotty.bind(' Shift + CTRL + G ',function() end)"));
        Assert.True(_host.Keybinds.TryExecute(true, true, false, false, "g"));
    }

    /// <summary>Verifies malformed chords are rejected and the None action unbinds.</summary>
    [Fact]
    public void KeybindingParser_RejectsMalformedChordsAndNoneUnbinds()
    {
        var manager = new KeybindingManager();
        manager.ApplyCustomBindings(new Dictionary<string, string>
        {
            { "ctrl+g+h", nameof(TerminalAction.Copy) },
            { "ctrl+hyper+g", nameof(TerminalAction.Copy) },
            { "ctrl+shift+f", nameof(TerminalAction.None) },
            { "ctrl+shift+g", "NotAnAction" }
        });
        Assert.False(manager.TryGetAction(true, false, false, false, "g", out _));
        Assert.False(manager.TryGetAction(true, true, false, false, "f", out _));
        Assert.True(_host.ExecuteString(@"local ok=pcall(function() dotty.bind('ctrl+g+h',function() end) end); assert(not ok); ok=pcall(function() dotty.bind('ctrl+shift',function() end) end); assert(not ok)"));
        Assert.False(_host.Keybinds.TryExecute(true, false, false, false, "g"));
    }

    /// <summary>Verifies a value hook can format a tab title.</summary>
    [Fact]
    public void LuaHooks_ExecutesFormatTabTitleHook()
    {
        TerminalTab tab = _tabManager.CreateTab(cols: 80, rows: 24);
        tab.Title = "nvim src/main.rs";
        Assert.True(_host.ExecuteString(@"dotty.on('format_tab_title',function(t) return 'vim '..t.index..': '..t.title end)"));
        var title = new ReusableTextBuffer();
        Assert.True(_host.Hooks.TryFormatTabTitle(tab, 0, title));
        Assert.StartsWith("vim 1:", title.Span.ToString());
    }

    [Fact]
    public void TabAndPaneHandleTablesAreReused()
    {
        _tabManager.CreateTab();
        Assert.True(_host.ExecuteString(@"
            local tab = dotty.tabs.get(1)
            assert(tab == dotty.tabs.get(1))
            assert(tab.active_pane == tab:panes()[1])"));
    }

    /// <summary>Verifies URL hooks handle only explicitly accepted URLs.</summary>
    [Fact]
    public void LuaHooks_HandlesOpenUrlOnlyWhenCallbackAcceptsIt()
    {
        Assert.True(_host.ExecuteString(@"dotty.on('open_url',function(url) return url=='dotty://handled' end)"));
        Assert.True(_host.Hooks.TryOpenUrl("dotty://handled"));
        Assert.False(_host.Hooks.TryOpenUrl("dotty://unhandled"));
    }

    /// <summary>Verifies config.lua changes apply before evaluation returns.</summary>
    [Fact]
    public void Evaluate_AppliesConfigLuaBeforeReturning()
    {
        using var script = TempScript("dotty.config.theme='LuaTheme'; dotty.config.font.family='LuaFont'; dotty.config.font.size=19");
        var config = new DottyUserConfig();
        Assert.True(_host.Evaluate(config, script.Path));
        Assert.Equal("LuaTheme", config.Theme);
        Assert.Equal("LuaFont", config.Font.Family);
        Assert.Equal(19, config.Font.Size);
    }

    /// <summary>Verifies reevaluation replaces previously registered value hooks.</summary>
    [Fact]
    public void Evaluate_ReplacesOldValueHooks()
    {
        using var first = TempScript("dotty.on('format_tab_title',function() return 'first' end)");
        using var second = TempScript("dotty.on('format_tab_title',function() return 'second' end)");
        Assert.True(_host.Evaluate(_config, first.Path));
        TerminalTab tab = _tabManager.CreateTab();
        var title = new ReusableTextBuffer();
        Assert.True(_host.Hooks.TryFormatTabTitle(tab, 0, title));
        Assert.Equal("first", title.Span.ToString());
        Assert.True(_host.Evaluate(_config, second.Path));
        Assert.True(_host.Hooks.TryFormatTabTitle(tab, 0, title));
        Assert.Equal("second", title.Span.ToString());
    }

    /// <summary>Verifies failed evaluation clears live keybinds and timer callbacks.</summary>
    [Fact]
    public async Task Evaluate_FailureClearsKeybindsAndTimers()
    {
        using var script = TempScript(@"
            dotty.bind('ctrl+k', 'Clear')
            dotty.every(16, function() dotty.log('timer-fired') end)
            error('evaluation failed')");

        Assert.False(_host.Evaluate(_config, script.Path));
        Assert.False(_host.Keybinds.TryExecute(true, false, false, false, "K"));
        Assert.Empty(_services.ExecutedActions);

        await Task.Delay(TimeSpan.FromMilliseconds(64), TestContext.Current.CancellationToken);
        _services.Drain();
        Assert.DoesNotContain(_services.Messages, message => message.Message == "timer-fired");
    }

    /// <summary>Verifies None consumes its chord without invoking a built-in action.</summary>
    [Fact]
    public void LuaBind_NoneSuppressesBuiltInAction()
    {
        Assert.True(_host.ExecuteString(@"dotty.bind('ctrl+shift+t', 'None')"));

        Assert.True(_host.Keybinds.TryExecute(true, true, false, false, "T"));
        Assert.Empty(_services.ExecutedActions);
    }

    /// <summary>Verifies tab creation is blocked during evaluation and uses host services at runtime.</summary>
    [Fact]
    public void TabsNew_IsForbiddenDuringEvaluationAndUsesHostServiceAtRuntime()
    {
        using var script = TempScript("dotty.tabs.new{}");
        Assert.False(_host.Evaluate(_config, script.Path));
        Assert.Contains("dotty.tabs.new is not available while config.lua is being evaluated", _host.LastError);
        Assert.True(_host.Evaluate(_config, null));
        Assert.True(_host.ExecuteString("dotty.tabs.new{}"));
        Assert.Equal(1, _tabManager.Count);
        Assert.Equal(1, _tabManager.ActiveIndex + 1);
        Assert.Equal(1, _services.CreateTabCount);
    }

    /// <summary>Verifies number-key normalization and false-return fallback for Lua keybinds.</summary>
    [Fact]
    public void LuaBind_NormalizesSilkNumberKeysAndFalseFallsThrough()
    {
        Assert.True(_host.ExecuteString(@"dotty.bind('alt+1',function() return true end); dotty.bind('alt+2',function() return false end)"));
        Assert.True(_host.Keybinds.TryExecute(false, false, true, false, "Number1"));
        Assert.False(_host.Keybinds.TryExecute(false, false, true, false, "Number2"));
    }

    /// <summary>Verifies action-name bindings invoke the host action executor.</summary>
    [Fact]
    public void LuaBind_ActionNameUsesHostActionExecutor()
    {
        Assert.True(_host.ExecuteString(@"dotty.bind('ctrl+k','Clear')"));
        Assert.True(_host.Keybinds.TryExecute(true, false, false, false, "K"));
        Assert.Equal(new[] { TerminalAction.Clear }, _services.ExecutedActions);
    }

    /// <summary>Verifies nested config access and type errors identify the invalid path.</summary>
    [Fact]
    public void LuaConfig_NestedReadWriteAndTypeErrorsNamePath()
    {
        Assert.True(_host.ExecuteString(@"dotty.config.font.size=16; assert(dotty.config.font.size==16)"));
        Assert.Equal(16, _config.Font.Size);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => _host.ExecuteString("dotty.config.font.size='large'"));
        Assert.Contains("font.size", error.Message);
    }

    /// <summary>Verifies runtime config writes coalesce application until posted work is drained.</summary>
    [Fact]
    public void RuntimeConfigWrites_CoalesceApplyUntilPostDrain()
    {
        Assert.True(_host.ExecuteString(@"dotty.config.theme='A'; dotty.config.font.size=18"));
        Assert.Equal(0, _services.ApplyConfigCount);
        _services.Drain();
        Assert.Equal(1, _services.ApplyConfigCount);
    }

    private static TempLuaScript TempScript(string source)
    {
        return new TempLuaScript(source);
    }

    private sealed class TempLuaScript : IDisposable
    {
        internal TempLuaScript(string source)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".lua");
            File.WriteAllText(Path, source);
        }

        internal string Path { get; }

        /// <summary>Deletes the temporary Lua script.</summary>
        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
            }
        }
    }
}
