using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    private string _title = "Terminal";
    private TerminalSession _session;

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

    public TabViewModel()
    {
        _session = new TerminalSession();
    }

    public string Title
    {
        get => _title;
        set { _title = value; RaisePropertyChanged(); }
    }

    public TerminalSession Session
    {
        get => _session;
        set { _session = value; RaisePropertyChanged(); }
    }

    public void Dispose()
    {
        _session.Dispose();
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

    public void AddNewTab()
    {
        var newTab = new TabViewModel();
        Tabs.Add(newTab);
        ActiveTab = newTab;
    }
}
