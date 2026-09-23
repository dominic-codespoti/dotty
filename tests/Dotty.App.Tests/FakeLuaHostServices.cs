using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;

namespace Dotty.App.Tests;

internal sealed class FakeLuaHostServices : ILuaHostServices
{
    private readonly TerminalTabManager _tabs;
    private readonly Queue<Action> _posted = new();

    internal FakeLuaHostServices(TerminalTabManager tabs)
    {
        _tabs = tabs;
    }

    internal int CreateTabCount { get; private set; }
    internal List<TerminalAction> ExecutedActions { get; } = new();
    internal List<(LuaMessageLevel Level, string Message)> Messages { get; } = new();
    internal int ApplyConfigCount { get; private set; }
    internal bool ActionResult { get; set; } = true;
    internal int InvalidateCount { get; private set; }

    /// <summary>Creates and selects a tab through the manager.</summary>
    public TerminalTab CreateTab(string? workingDirectory, string? shell)
    {
        CreateTabCount++;
        TerminalTab tab = _tabs.CreateTab(workingDirectory: workingDirectory, shell: shell);
        _tabs.SelectTab(tab);
        return tab;
    }

    /// <summary>Queues host work for execution when the test drains the queue.</summary>
    public void Post(Action action)
    {
        lock (_posted)
        {
            _posted.Enqueue(action);
        }
    }

    internal void Drain()
    {
        while (true)
        {
            Action? action;
            lock (_posted)
            {
                if (_posted.Count == 0)
                {
                    return;
                }

                action = _posted.Dequeue();
            }

            action();
        }
    }

    /// <summary>Splits the target pane through the tab's pane tree.</summary>
    public LeafPane SplitPane(TerminalTab tab, LeafPane target, SplitDirection direction, string? workingDirectory, string? shell)
    {
        return tab.PaneTree.Split(target, direction, workingDirectory, shell);
    }

    /// <summary>Records the action and returns the configured result.</summary>
    public bool TryExecuteAction(TerminalAction action)
    {
        ExecutedActions.Add(action);
        return ActionResult;
    }

    /// <summary>Records that the host should apply updated configuration.</summary>
    public void ApplyConfig()
    {
        ApplyConfigCount++;
    }

    /// <summary>Records an invalidation request.</summary>
    public void Invalidate()
    {
        InvalidateCount++;
    }

    /// <summary>Records a Lua host message.</summary>
    public void Log(LuaMessageLevel level, string message)
    {
        Messages.Add((level, message));
    }
}
