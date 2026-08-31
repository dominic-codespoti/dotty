using System;
using System.Collections.Generic;

namespace Dotty.Runtime.Tabs;

public sealed class TerminalTabManager : IDisposable
{
    private readonly List<TerminalTab> _tabs = new();
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

    public TerminalTab CreateTab(int cols = 80, int rows = 24, string? workingDirectory = null, string? shell = null)
    {
        ThrowIfDisposed();

        var tab = new TerminalTab(
            workingDirectory: workingDirectory,
            rows: rows,
            columns: cols);

        tab.TitleChanged += title => OnTabTitleChanged(tab, title);
        tab.Session.Adapter.Bell += () =>
        {
            if (ActiveTab != tab)
            {
                tab.HasBellAlert = true;
            }
        };
        _tabs.Add(tab);
        TabAdded?.Invoke(tab);

        SelectTab(tab);

        tab.Session.StartWithOptions(
            shell: shell,
            workingDirectory: workingDirectory);

        return tab;
    }

    public bool CloseTab(TerminalTab tab)
    {
        ThrowIfDisposed();
        if (tab == null) return false;

        var index = _tabs.IndexOf(tab);
        if (index < 0) return false;

        return CloseTabAt(index);
    }

    public bool CloseTabAt(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _tabs.Count) return false;

        var tabToClose = _tabs[index];
        bool wasActive = (_activeIndex == index);

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
        tabToClose.Dispose();

        return true;
    }

    public void SelectTab(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _tabs.Count) return;
        if (_activeIndex == index && _tabs[index].IsActive) return;

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
        if (tab == null) return;

        var index = _tabs.IndexOf(tab);
        if (index >= 0)
        {
            SelectTab(index);
        }
    }

    public void SelectNextTab()
    {
        ThrowIfDisposed();
        if (_tabs.Count <= 1) return;

        var nextIndex = (_activeIndex + 1) % _tabs.Count;
        SelectTab(nextIndex);
    }

    public void SelectPreviousTab()
    {
        ThrowIfDisposed();
        if (_tabs.Count <= 1) return;

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
        if (_isDisposed) return;
        _isDisposed = true;

        _activeIndex = -1;
        var tabsToDispose = _tabs.ToArray();
        _tabs.Clear();

        foreach (var tab in tabsToDispose)
        {
            tab.Dispose();
        }
    }
}
