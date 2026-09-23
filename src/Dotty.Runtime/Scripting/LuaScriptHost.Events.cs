using System;
using System.Collections.Generic;
using System.Threading;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private readonly Dictionary<TerminalTab, (Action<LeafPane, LeafPane> ActivePaneChanged, Action TopologyChanged)> _tabEventHandlers = new();
    private Action<TerminalTab>? _tabAddedHandler;
    private Action<TerminalTab>? _tabClosedHandler;
    private Action<TerminalTab?>? _activeTabChangedHandler;
    private Action<TerminalTab, string>? _tabTitleChangedHandler;
    private Action<TerminalTab, LeafPane, int>? _processExitedHandler;
    private Action<TerminalTab, LeafPane>? _bellRungHandler;
    private long _eventGeneration;
    private readonly object _eventQueueLock = new();
    private readonly Queue<LuaEventRecord> _pendingEvents = new(64);
    private bool _eventDrainScheduled;

    private readonly struct LuaEventRecord
    {
        internal LuaEventRecord(string eventName, long generation, int count, LuaValue arg0, LuaValue arg1, LuaValue arg2)
        {
            EventName = eventName;
            Generation = generation;
            Count = count;
            Arg0 = arg0;
            Arg1 = arg1;
            Arg2 = arg2;
        }

        internal string EventName { get; }
        internal long Generation { get; }
        internal int Count { get; }
        internal LuaValue Arg0 { get; }
        internal LuaValue Arg1 { get; }
        internal LuaValue Arg2 { get; }
    }

    partial void RegisterEvents(KeraLua.Lua lua, int dottyIndex)
    {
    }

    partial void AttachEvents()
    {
        _tabAddedHandler = OnTabAdded;
        _tabClosedHandler = OnTabClosed;
        _activeTabChangedHandler = OnActiveTabChanged;
        _tabTitleChangedHandler = OnTabTitleChanged;
        _processExitedHandler = OnProcessExited;
        _bellRungHandler = OnBellRung;

        _tabManager.TabAdded += _tabAddedHandler;
        _tabManager.TabClosed += _tabClosedHandler;
        _tabManager.ActiveTabChanged += _activeTabChangedHandler;
        _tabManager.TabTitleChanged += _tabTitleChangedHandler;
        _tabManager.ProcessExited += _processExitedHandler;
        _tabManager.BellRung += _bellRungHandler;

        foreach (var tab in _tabManager.Tabs)
        {
            AttachTabEvents(tab);
        }
    }

    partial void DetachEvents()
    {
        Interlocked.Increment(ref _eventGeneration);
        if (_tabAddedHandler != null)
        {
            _tabManager.TabAdded -= _tabAddedHandler;
        }

        if (_tabClosedHandler != null)
        {
            _tabManager.TabClosed -= _tabClosedHandler;
        }

        if (_activeTabChangedHandler != null)
        {
            _tabManager.ActiveTabChanged -= _activeTabChangedHandler;
        }

        if (_tabTitleChangedHandler != null)
        {
            _tabManager.TabTitleChanged -= _tabTitleChangedHandler;
        }

        if (_processExitedHandler != null)
        {
            _tabManager.ProcessExited -= _processExitedHandler;
        }

        if (_bellRungHandler != null)
        {
            _tabManager.BellRung -= _bellRungHandler;
        }

        foreach (var pair in _tabEventHandlers)
        {
            pair.Key.PaneTree.ActivePaneChanged -= pair.Value.ActivePaneChanged;
            pair.Key.PaneTree.TopologyChanged -= pair.Value.TopologyChanged;
        }

        _tabEventHandlers.Clear();
    }

    partial void ResetEvents()
    {
        Interlocked.Increment(ref _eventGeneration);
        lock (_eventQueueLock)
        {
            _pendingEvents.Clear();
        }
    }

    private void AttachTabEvents(TerminalTab tab)
    {
        if (_tabEventHandlers.ContainsKey(tab))
        {
            return;
        }

        Action<LeafPane, LeafPane> activePaneChanged = (previous, current) =>
            QueueEvent("pane_focused", LuaValue.FromTab(tab), LuaValue.FromPane(current, tab));
        Action topologyChanged = () => QueueEvent("pane_layout_changed", LuaValue.FromTab(tab));
        _tabEventHandlers.Add(tab, (activePaneChanged, topologyChanged));
        tab.PaneTree.ActivePaneChanged += activePaneChanged;
        tab.PaneTree.TopologyChanged += topologyChanged;
    }

    private void DetachTabEvents(TerminalTab tab)
    {
        if (!_tabEventHandlers.Remove(tab, out var handlers))
        {
            return;
        }

        tab.PaneTree.ActivePaneChanged -= handlers.ActivePaneChanged;
        tab.PaneTree.TopologyChanged -= handlers.TopologyChanged;
    }

    private void OnTabAdded(TerminalTab tab)
    {
        AttachTabEvents(tab);
        QueueEvent("tab_added", LuaValue.FromTab(tab));
    }

    private void OnTabClosed(TerminalTab tab)
    {
        QueueEvent("tab_closed", LuaValue.FromTab(tab));
        DetachTabEvents(tab);
    }

    private void OnActiveTabChanged(TerminalTab? tab)
    {
        if (tab != null)
        {
            QueueEvent("tab_activated", LuaValue.FromTab(tab));
        }
    }

    private void OnTabTitleChanged(TerminalTab tab, string title)
    {
        QueueEvent("tab_title_changed", LuaValue.FromTab(tab), LuaValue.From(title));
    }

    private void OnProcessExited(TerminalTab tab, LeafPane pane, int exitCode)
    {
        QueueEvent("process_exited", LuaValue.FromTab(tab), LuaValue.FromPane(pane, tab), LuaValue.From((double)exitCode));
    }

    private void OnBellRung(TerminalTab tab, LeafPane pane)
    {
        QueueEvent("bell", LuaValue.FromTab(tab), LuaValue.FromPane(pane, tab));
    }

    private void QueueEvent(string eventName, LuaValue arg0)
    {
        QueueEvent(eventName, 1, arg0, default, default);
    }

    private void QueueEvent(string eventName, LuaValue arg0, LuaValue arg1)
    {
        QueueEvent(eventName, 2, arg0, arg1, default);
    }

    private void QueueEvent(string eventName, LuaValue arg0, LuaValue arg1, LuaValue arg2)
    {
        QueueEvent(eventName, 3, arg0, arg1, arg2);
    }

    private void QueueEvent(string eventName, int count, LuaValue arg0, LuaValue arg1, LuaValue arg2)
    {
        if (!Hooks.HasHandlers(eventName))
        {
            return;
        }

        bool postDrain = false;
        var record = new LuaEventRecord(eventName, Interlocked.Read(ref _eventGeneration), count, arg0, arg1, arg2);
        lock (_eventQueueLock)
        {
            _pendingEvents.Enqueue(record);
            if (!_eventDrainScheduled)
            {
                _eventDrainScheduled = true;
                postDrain = true;
            }
        }

        if (postDrain)
        {
            _services.Post(_drainEventsAction);
        }
    }

    private void DrainEvents()
    {
        while (true)
        {
            LuaEventRecord record;
            lock (_eventQueueLock)
            {
                if (_pendingEvents.Count == 0)
                {
                    _eventDrainScheduled = false;
                    return;
                }

                record = _pendingEvents.Dequeue();
            }

            if (_disposed || record.Generation != Interlocked.Read(ref _eventGeneration) ||
                _lua == null || !Hooks.HasHandlers(record.EventName))
            {
                continue;
            }

            switch (record.Count)
            {
                case 1:
                    Emit(record.EventName, record.Arg0);
                    break;
                case 2:
                    Emit(record.EventName, record.Arg0, record.Arg1);
                    break;
                default:
                    Emit(record.EventName, record.Arg0, record.Arg1, record.Arg2);
                    break;
            }
        }
    }
}
