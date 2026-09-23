using System;
using System.Collections.Generic;
using Dotty.Runtime.Panes;

namespace Dotty.Runtime.Tabs;

public sealed class TerminalTabManager : IDisposable
{
    private readonly List<TerminalTab> _tabs = new();
    private readonly Dictionary<TerminalTab, Action<LeafPane, int>> _exitHandlers = new();
    private readonly Dictionary<TerminalTab, Action> _topologyHandlers = new();
    private readonly Dictionary<TerminalTab, Dictionary<LeafPane, Action>> _bellHandlers = new();
    private int _activeIndex = -1;
    private bool _isDisposed;

    public IReadOnlyList<TerminalTab> Tabs => _tabs;

    public TerminalTab? ActiveTab => (_activeIndex >= 0 && _activeIndex < _tabs.Count) ? _tabs[_activeIndex] : null;

    public int ActiveIndex => _activeIndex;

    public int Count => _tabs.Count;

    public event Action<TerminalTab>? TabAdded;
    public event Action<TerminalTab>? TabClosed;
    public event Action<TerminalTab?>? ActiveTabChanged;
    public event Action<TerminalTab, string>? TabTitleChanged;
    public event Action<TerminalTab, LeafPane, int>? ProcessExited;
    public event Action<TerminalTab, LeafPane>? BellRung;

    public TerminalTab CreateTab(int cols = 80, int rows = 24, string? workingDirectory = null, string? shell = null)
    {
        ThrowIfDisposed();

        var tab = new TerminalTab(
            workingDirectory: workingDirectory,
            rows: rows,
            columns: cols,
            shell: shell,
            deferStart: true);

        Action<LeafPane, int> exitHandler = (leaf, exitCode) => OnTabProcessExited(tab, leaf, exitCode);
        _exitHandlers.Add(tab, exitHandler);
        tab.ProcessExited += exitHandler;
        tab.TitleChanged += title => OnTabTitleChanged(tab, title);
        AttachBellHandlers(tab);
        Action topologyHandler = () => AttachBellHandlers(tab);
        _topologyHandlers.Add(tab, topologyHandler);
        tab.PaneTree.TopologyChanged += topologyHandler;
        _tabs.Add(tab);
        TabAdded?.Invoke(tab);

        SelectTab(tab);
        tab.Session.StartWithOptions(shell: shell, workingDirectory: workingDirectory);
        return tab;
    }

    public bool CloseTab(TerminalTab tab)
    {
        ThrowIfDisposed();
        if (tab == null)
        {
            return false;
        }

        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return false;
        }

        return CloseTabAt(index);
    }

    /// <summary>
    /// Closes the pane whose process exited. This policy is intended to be
    /// called by the UI thread after handling <see cref="ProcessExited"/>;
    /// the native PTY callback only reports the event and never mutates the
    /// tab collection.
    /// </summary>
    public bool CloseExitedPane(TerminalTab tab, LeafPane leaf)
    {
        ThrowIfDisposed();
        if (tab == null || leaf == null)
        {
            return false;
        }

        if (_tabs.IndexOf(tab) < 0)
        {
            return false;
        }

        var leaves = tab.PaneTree.Leaves;
        bool ownsLeaf = false;
        foreach (var candidate in leaves)
        {
            if (ReferenceEquals(candidate, leaf))
            {
                ownsLeaf = true;
                break;
            }
        }

        if (!ownsLeaf)
        {
            return false;
        }

        if (leaves.Count == 1)
        {
            return CloseTab(tab);
        }

        return tab.PaneTree.Close(leaf);
    }

    public bool CloseTabAt(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _tabs.Count)
        {
            return false;
        }

        var tabToClose = _tabs[index];
        bool wasActive = (_activeIndex == index);
        DetachBellHandlers(tabToClose);
        if (_topologyHandlers.Remove(tabToClose, out var topologyHandler))
        {
            tabToClose.PaneTree.TopologyChanged -= topologyHandler;
        }

        _tabs.RemoveAt(index);
        tabToClose.IsActive = false;

        if (_tabs.Count == 0)
        {
            _activeIndex = -1;
            if (wasActive)
            {
                ActiveTabChanged?.Invoke(null);
            }
        }
        else if (wasActive)
        {
            var nextIndex = Math.Min(index, _tabs.Count - 1);
            _activeIndex = -1; // Reset before selecting so event/state transitions cleanly
            SelectTab(nextIndex);
        }
        else if (_activeIndex > index)
        {
            _activeIndex--;
        }

        TabClosed?.Invoke(tabToClose);
        if (_exitHandlers.Remove(tabToClose, out var exitHandler))
        {
            tabToClose.ProcessExited -= exitHandler;
        }
        tabToClose.Dispose();

        return true;
    }

    public void SelectTab(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        if (_activeIndex == index && _tabs[index].IsActive)
        {
            return;
        }

        if (_activeIndex >= 0 && _activeIndex < _tabs.Count)
        {
            _tabs[_activeIndex].IsActive = false;
        }

        _activeIndex = index;
        var activeTab = _tabs[index];
        activeTab.IsActive = true;
        activeTab.HasBellAlert = false;
        ActiveTabChanged?.Invoke(activeTab);
    }

    public void SelectTab(TerminalTab tab)
    {
        ThrowIfDisposed();
        if (tab == null)
        {
            return;
        }

        var index = _tabs.IndexOf(tab);
        if (index >= 0)
        {
            SelectTab(index);
        }
    }

    public void SelectNextTab()
    {
        ThrowIfDisposed();
        if (_tabs.Count <= 1)
        {
            return;
        }

        var nextIndex = (_activeIndex + 1) % _tabs.Count;
        SelectTab(nextIndex);
    }

    public void SelectPreviousTab()
    {
        ThrowIfDisposed();
        if (_tabs.Count <= 1)
        {
            return;
        }

        var prevIndex = (_activeIndex - 1 + _tabs.Count) % _tabs.Count;
        SelectTab(prevIndex);
    }

    public void ResizeAll(int cols, int rows)
    {
        ThrowIfDisposed();
        foreach (var tab in _tabs)
        {
            tab.Session.Resize(cols, rows);
        }
    }

    private void AttachBellHandlers(TerminalTab tab)
    {
        if (!_bellHandlers.TryGetValue(tab, out var handlers))
        {
            handlers = new Dictionary<LeafPane, Action>();
            _bellHandlers.Add(tab, handlers);
        }

        var leaves = tab.PaneTree.Leaves;
        foreach (var leaf in leaves)
        {
            if (handlers.ContainsKey(leaf))
            {
                continue;
            }

            Action handler = () =>
            {
                if (ActiveTab != tab)
                {
                    tab.HasBellAlert = true;
                }

                BellRung?.Invoke(tab, leaf);
            };
            handlers.Add(leaf, handler);
            leaf.Session.Adapter.Bell += handler;
        }

        var removedLeaves = new List<LeafPane>();
        foreach (var pair in handlers)
        {
            bool isPresent = false;
            foreach (var leaf in leaves)
            {
                if (ReferenceEquals(leaf, pair.Key))
                {
                    isPresent = true;
                    break;
                }
            }

            if (!isPresent)
            {
                pair.Key.Session.Adapter.Bell -= pair.Value;
                removedLeaves.Add(pair.Key);
            }
        }

        foreach (var leaf in removedLeaves)
        {
            handlers.Remove(leaf);
        }
    }

    private void DetachBellHandlers(TerminalTab tab)
    {
        if (!_bellHandlers.Remove(tab, out var handlers))
        {
            return;
        }

        foreach (var pair in handlers)
        {
            pair.Key.Session.Adapter.Bell -= pair.Value;
        }
    }

    private void OnTabProcessExited(TerminalTab tab, LeafPane leaf, int exitCode)
    {
        if (!_isDisposed)
        {
            ProcessExited?.Invoke(tab, leaf, exitCode);
        }
    }

    private void OnTabTitleChanged(TerminalTab tab, string title)
    {
        TabTitleChanged?.Invoke(tab, title);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(TerminalTabManager));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _activeIndex = -1;
        var tabsToDispose = _tabs.ToArray();
        _tabs.Clear();

        foreach (var tab in tabsToDispose)
        {
            DetachBellHandlers(tab);
            if (_topologyHandlers.Remove(tab, out var topologyHandler))
            {
                tab.PaneTree.TopologyChanged -= topologyHandler;
            }

            if (_exitHandlers.Remove(tab, out var handler))
            {
                tab.ProcessExited -= handler;
            }

            tab.Dispose();
        }
    }
}
