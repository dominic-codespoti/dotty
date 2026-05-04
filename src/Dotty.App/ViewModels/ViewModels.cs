using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace Dotty.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class TabViewModel : ViewModelBase, IDisposable
{
    private const string DefaultTitle = "Terminal";
    private string? _userTitleOverride;
    private string? _sessionTitle;
    private TerminalSession? _session;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; RaisePropertyChanged(); }
    }

    private bool _isEditingTitle;
    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        set { _isEditingTitle = value; RaisePropertyChanged(); }
    }

    public string Title
    {
        get => !string.IsNullOrWhiteSpace(_userTitleOverride)
            ? _userTitleOverride!
            : !string.IsNullOrWhiteSpace(_sessionTitle)
                ? _sessionTitle!
                : DefaultTitle;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(_userTitleOverride, normalized, StringComparison.Ordinal)) return;
            _userTitleOverride = normalized;
            RaisePropertyChanged();
        }
    }

    public TerminalSession Session
    {
        get => _session ??= CreateAndAttachSession();
        set
        {
            if (ReferenceEquals(_session, value)) return;
            DetachSession(_session);
            _session = value;
            AttachSession(_session);
            RaisePropertyChanged();
        }
    }

    public bool HasSession => _session != null;
    public bool IsSessionStarted => _session?.IsStarted == true;

    public void SetSessionTitle(string? title)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? null : title;
        if (string.Equals(_sessionTitle, normalized, StringComparison.Ordinal)) return;
        _sessionTitle = normalized;
        if (string.IsNullOrWhiteSpace(_userTitleOverride))
        {
            RaisePropertyChanged(nameof(Title));
        }
    }

    private TerminalSession CreateAndAttachSession()
    {
        var session = new TerminalSession();
        AttachSession(session);
        return session;
    }

    private void AttachSession(TerminalSession? session)
    {
        if (session == null) return;
        session.TitleChanged += OnSessionTitleChanged;
    }

    private void DetachSession(TerminalSession? session)
    {
        if (session == null) return;
        session.TitleChanged -= OnSessionTitleChanged;
    }

    private void OnSessionTitleChanged(string title)
    {
        Dispatcher.UIThread.Post(() => SetSessionTitle(title));
    }

    public void Dispose()
    {
        DetachSession(_session);
        _session?.Dispose();
    }
}

public class MainViewModel : ViewModelBase
{
    private TabViewModel? _activeTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public event EventHandler? ActiveTabChanged;

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab != null) _activeTab.IsActive = false;
            _activeTab = value;
            if (_activeTab != null) _activeTab.IsActive = true;
            RaisePropertyChanged();
            ActiveTabChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public MainViewModel()
    {
        var initialTab = new TabViewModel();
        Tabs.Add(initialTab);
        ActiveTab = initialTab;
    }

    public void AddNewTab(bool activate = true)
    {
        var newTab = CreateTab(sizeSourceTab: _activeTab);
        if (activate)
        {
            ActiveTab = newTab;
        }
    }

    public TabViewModel DuplicateTab(TabViewModel sourceTab, bool activate = true)
    {
        var newTab = CreateTab(sizeSourceTab: sourceTab);
        newTab.Title = sourceTab.Title + " (Copy)";
        if (activate)
        {
            ActiveTab = newTab;
        }

        return newTab;
    }

    private TabViewModel CreateTab(TabViewModel? sizeSourceTab)
    {
        var newTab = new TabViewModel();
        SeedTabStartupSize(newTab, sizeSourceTab);
        Tabs.Add(newTab);
        return newTab;
    }

    private static void SeedTabStartupSize(TabViewModel targetTab, TabViewModel? sourceTab)
    {
        if (sourceTab?.HasSession != true)
        {
            return;
        }

        var sourceBuffer = sourceTab.Session.Adapter?.Buffer;
        if (sourceBuffer == null || sourceBuffer.Columns <= 0 || sourceBuffer.Rows <= 0)
        {
            return;
        }

        targetTab.Session.Resize(sourceBuffer.Columns, sourceBuffer.Rows);
    }
}
