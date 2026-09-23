using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Xunit;

namespace Dotty.App.Tests;

public sealed class LuaActionsUtilitiesTests : IDisposable
{
    private readonly DottyUserConfig _config = new();
    private readonly TerminalTabManager _tabs = new();
    private readonly FakeLuaHostServices _services;
    private readonly LuaScriptHost _host;

    public LuaActionsUtilitiesTests()
    {
        _services = new FakeLuaHostServices(_tabs);
        _host = new LuaScriptHost(_services, _tabs);
        Assert.True(_host.Evaluate(_config, null));
    }

    public void Dispose()
    {
        _host.Dispose();
        _tabs.Dispose();
    }

    [Fact]
    public void Action_ExecutesNamedActionAndReturnsServiceResult()
    {
        _services.ActionResult = false;

        Assert.True(_host.ExecuteString("assert(dotty.action('splitvertical') == false)"));
        Assert.Equal(new[] { TerminalAction.SplitVertical }, _services.ExecutedActions);
    }

    [Fact]
    public void Action_RejectsUnknownNoneAndNumericNames()
    {
        Assert.True(_host.ExecuteString(@"
            assert(not pcall(function() dotty.action('not-an-action') end))
            assert(not pcall(function() dotty.action('None') end))
            assert(not pcall(function() dotty.action('1') end))"));
    }

    [Fact]
    public void Actions_ListsNamesExceptNone()
    {
        Assert.True(_host.ExecuteString(@"
            local found_new_tab = false
            for _, name in ipairs(dotty.actions) do
                assert(name ~= 'None')
                if name == 'NewTab' then found_new_tab = true end
            end
            assert(found_new_tab)"));
    }

    [Fact]
    public void Action_IsForbiddenDuringEvaluation()
    {
        using var script = new TempLuaScript("dotty.action('NewTab')");

        Assert.False(_host.Evaluate(_config, script.Path));
        Assert.Contains("dotty.action is not available while config.lua is being evaluated", _host.LastError);
    }

    [Fact]
    public void After_RunsOnlyWhenPostedWorkIsDrained()
    {
        Assert.True(_host.ExecuteString("dotty.after(0, function() dotty.log('after-ran') end)"));
        Assert.DoesNotContain(_services.Messages, message => message.Message == "after-ran");

        DrainUntil(() => _services.Messages.Exists(message => message.Message == "after-ran"));

        Assert.Contains(_services.Messages, message => message.Message == "after-ran");
    }

    [Fact]
    public void Cancel_PreventsExecutionAndReportsWhetherTimerExisted()
    {
        Assert.True(_host.ExecuteString(@"
            local id = dotty.after(0, function() dotty.log('cancelled-timer-ran') end)
            assert(dotty.cancel(id))
            assert(not dotty.cancel(id))
            assert(not dotty.cancel(-1))"));

        Thread.Sleep(30);
        _services.Drain();
        Assert.DoesNotContain(_services.Messages, message => message.Message == "cancelled-timer-ran");
    }

    [Fact]
    public void ReEvaluate_DisposesOldTimersAndQueuedCallbacks()
    {
        Assert.True(_host.ExecuteString("dotty.after(0, function() dotty.log('old-timer-ran') end)"));
        Assert.True(_host.Evaluate(_config, null));
        Thread.Sleep(30);
        _services.Drain();

        Assert.DoesNotContain(_services.Messages, message => message.Message == "old-timer-ran");
    }

    [Fact]
    public void Spawn_CreatesTabWithProgramAsShell()
    {
        var services = new RecordingLuaHostServices(_tabs);
        using var host = new LuaScriptHost(services, _tabs);
        Assert.True(host.Evaluate(_config, null));

        Assert.True(host.ExecuteString("local tab = dotty.spawn('htop'); assert(tab and tab.is_valid)"));

        Assert.Equal("htop", services.Shell);
        Assert.Null(services.WorkingDirectory);
        Assert.Equal(1, services.CreateTabCount);
    }

    private void DrainUntil(Func<bool> condition)
    {
        // Liveness bound only: the loop exits as soon as the condition holds.
        // Timer callbacks run on the thread pool, which can be slow to schedule
        // on loaded CI runners.
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            _services.Drain();
            Thread.Sleep(2);
        }

        _services.Drain();
        Assert.True(condition(), "The posted timer callback did not run before the timeout.");
    }

    private sealed class RecordingLuaHostServices : ILuaHostServices
    {
        private readonly TerminalTabManager _tabs;

        internal RecordingLuaHostServices(TerminalTabManager tabs)
        {
            _tabs = tabs;
        }

        internal string? Shell { get; private set; }
        internal string? WorkingDirectory { get; private set; }
        internal int CreateTabCount { get; private set; }

        public void Post(Action action)
        {
            action();
        }

        public TerminalTab CreateTab(string? workingDirectory, string? shell)
        {
            CreateTabCount++;
            WorkingDirectory = workingDirectory;
            Shell = shell;
            TerminalTab tab = _tabs.CreateTab(workingDirectory: workingDirectory);
            _tabs.SelectTab(tab);
            return tab;
        }

        public Dotty.Runtime.Panes.LeafPane SplitPane(
            TerminalTab tab,
            Dotty.Runtime.Panes.LeafPane target,
            Dotty.Runtime.Panes.SplitDirection direction,
            string? workingDirectory,
            string? shell)
        {
            return tab.PaneTree.Split(target, direction, workingDirectory, shell);
        }

        public bool TryExecuteAction(TerminalAction action)
        {
            return true;
        }

        public void ApplyConfig()
        {
        }

        public void Invalidate()
        {
        }

        public void Log(LuaMessageLevel level, string message)
        {
        }
    }

    private sealed class TempLuaScript : IDisposable
    {
        internal TempLuaScript(string source)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".lua");
            File.WriteAllText(Path, source);
        }

        internal string Path { get; }

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
