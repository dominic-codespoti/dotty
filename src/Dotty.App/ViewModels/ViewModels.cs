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

public abstract class SplitNodeViewModel : ViewModelBase
{
}

public class TerminalViewModel : SplitNodeViewModel, IDisposable
{
    public TerminalSession Session { get; }

    public TerminalViewModel()
    {
        Session = new TerminalSession();
    }

    public void Dispose()
    {
        Session.Dispose();
    }
}

public class SplitContainerViewModel : SplitNodeViewModel
{
    private SplitNodeViewModel _firstChild = null!;
    private SplitNodeViewModel _secondChild = null!;
    private bool _isHorizontal;

    public SplitNodeViewModel FirstChild
    {
        get => _firstChild;
        set { _firstChild = value; RaisePropertyChanged(); }
    }

    public SplitNodeViewModel SecondChild
    {
        get => _secondChild;
        set { _secondChild = value; RaisePropertyChanged(); }
    }

    public bool IsHorizontal
    {
        get => _isHorizontal;
        set { _isHorizontal = value; RaisePropertyChanged(); }
    }
}

public class TabViewModel : ViewModelBase, IDisposable
{
    private string _title = "Terminal";
    private SplitNodeViewModel _rootNode;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; RaisePropertyChanged(); }
    }

    public TabViewModel()
    {
        _rootNode = new TerminalViewModel();
    }

    public string Title
    {
        get => _title;
        set { _title = value; RaisePropertyChanged(); }
    }

    public SplitNodeViewModel RootNode
    {
        get => _rootNode;
        set { _rootNode = value; RaisePropertyChanged(); }
    }

    public void Dispose()
    {
        DisposeNode(RootNode);
    }

    private void DisposeNode(SplitNodeViewModel node)
    {
        if (node is TerminalViewModel tvm)
            tvm.Dispose();
        else if (node is SplitContainerViewModel scvm)
        {
            DisposeNode(scvm.FirstChild);
            DisposeNode(scvm.SecondChild);
        }
    }
}

public class MainViewModel : ViewModelBase
{
    private TabViewModel? _activeTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab != null) _activeTab.IsActive = false;
            _activeTab = value;
            if (_activeTab != null) _activeTab.IsActive = true;
            RaisePropertyChanged();
        }
    }

    public MainViewModel()
    {
        var initialTab = new TabViewModel();
        Tabs.Add(initialTab);
        ActiveTab = initialTab;
    }
}
