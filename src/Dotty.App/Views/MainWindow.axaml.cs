using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Dotty.App.ViewModels;

namespace Dotty.App;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        
        Title = "Dotty";
        Background = new SolidColorBrush(Color.Parse(Services.Defaults.DefaultBackground));
        
        KeyDown += OnWindowKeyDown;
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach(var tab in _viewModel.Tabs)
            tab.Dispose();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.T)
            {
                var newTab = new TabViewModel();
                _viewModel.Tabs.Add(newTab);
                _viewModel.ActiveTab = newTab;
                e.Handled = true;
            }
            else if (e.Key == Key.W)
            {
                if (_viewModel.ActiveTab != null)
                {
                    _viewModel.ActiveTab.Dispose();
                    _viewModel.Tabs.Remove(_viewModel.ActiveTab);
                    if (_viewModel.Tabs.Count > 0)
                        _viewModel.ActiveTab = _viewModel.Tabs[0];
                    else
                        Close();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.D)
            {
                if (_viewModel.ActiveTab != null)
                {
                    SplitPane(false);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.E)
            {
                if (_viewModel.ActiveTab != null)
                {
                    SplitPane(true);
                }
                e.Handled = true;
            }
        }
    }

    private void SplitPane(bool isHorizontal)
    {
        var activeTab = _viewModel.ActiveTab;
        if (activeTab == null) return;
        
        var newTerminal = new TerminalViewModel();
        var newSplit = new SplitContainerViewModel
        {
            IsHorizontal = isHorizontal,
            FirstChild = activeTab.RootNode, // In a real app we'd target the focused pane, here we wrap root
            SecondChild = newTerminal
        };
        
        activeTab.RootNode = newSplit;
    }
}
