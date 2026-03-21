using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Dotty.App.ViewModels;

namespace Dotty.App;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        
        _viewModel = new MainViewModel();
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        DataContext = _viewModel;
        
        Title = "Dotty";
        Background = new SolidColorBrush(Color.Parse(Services.Defaults.DefaultBackground));
        
        KeyDown += OnWindowKeyDown;
        Closed += OnClosed;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        FocusActiveTerminal();
    }

    private void OnActiveTabChanged(object? sender, EventArgs e)
    {
        FocusActiveTerminal();
    }

    private void FocusActiveTerminal()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var activeTab = _viewModel.ActiveTab;
            if (activeTab != null && activeTab.IsActive)
            {
                // Find and focus the active terminal view
                // Since Avalonia's focusing can be tricky with generated items, 
                // we iterate over focusing logic
                FocusManager?.ClearFocus();
                
                var itemsControl = this.FindControl<ItemsControl>("PART_ItemsControl");
                if (itemsControl != null)
                {
                    foreach (var item in _viewModel.Tabs)
                    {
                        if (item == activeTab)
                        {
                            var container = itemsControl.ContainerFromItem(item) as Control;
                            if (container != null)
                            {
                                // The layout structure is Border -> TerminalView
                                var terminalView = FindTerminalView(container);
                                if (terminalView != null)
                                {
                                    terminalView.FocusInput();
                                }
                            }
                        }
                    }
                }
            }
        });
    }

    private Dotty.App.Views.TerminalView? FindTerminalView(Control parent)
    {
        if (parent is Dotty.App.Views.TerminalView terminalView)
            return terminalView;

        if (parent is Border border && border.Child != null)
            return FindTerminalView(border.Child);
        
        if (parent is ContentPresenter cp && cp.Child != null)
            return FindTerminalView(cp.Child);
            
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach(var tab in _viewModel.Tabs)
            tab.Dispose();
    }

    private void OnRenameTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is TabViewModel tvm)
            tvm.IsEditingTitle = true;
    }

    private void OnRenameTextBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            if (sender is TextBox tb && tb.DataContext is TabViewModel tvm)
                tvm.IsEditingTitle = false;
        }
    }

    private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TabViewModel tvm)
            tvm.IsEditingTitle = false;
    }

    private void OnRenameTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            if (sender is TextBox tb && tb.IsVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    tb.Focus();
                    tb.SelectAll();
                });
            }
        }
    }

    private void OnNewTabRequested(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddNewTab();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.T)
            {
                _viewModel.AddNewTab();
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
        }
    }
}
