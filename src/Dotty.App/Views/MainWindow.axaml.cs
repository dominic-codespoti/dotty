using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dotty.App.ViewModels;
using Dotty.App.Controls.Canvas.Rendering;

namespace Dotty.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel;
    private TcpListener? _testCommandListener;
    private CancellationTokenSource? _testCommandCts;
    
    // Manual content management: Keep track of TerminalView instances per tab
    private readonly Dictionary<TabViewModel, TerminalView> _terminalViews = new();
    private Grid? _contentContainer;
    private Control? _tabBar;

    public MainWindow()
    {
        InitializeComponent();
        
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        
        Title = "Dotty";
        Background = new SolidColorBrush(Color.Parse(Services.Defaults.DefaultBackground));
        
        KeyDown += OnWindowKeyDown;
        Closed += OnClosed;
        Opened += OnOpened;
        
        // Start test command listener for automated testing
        StartTestCommandListener();
    }
    
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Get references to our manual container and tab bar
        _contentContainer = this.FindControl<Grid>("ContentContainer");
        _tabBar = this.FindControl<Control>("TabBar");
        
        // Initialize the first tab's content
        if (_viewModel.ActiveTab != null)
        {
            // Ensure view exists for the active tab
            if (!_terminalViews.ContainsKey(_viewModel.ActiveTab))
            {
                CreateTerminalView(_viewModel.ActiveTab);
            }
            ShowTab(_viewModel.ActiveTab);
        }
        
        // Create views for any existing tabs (from before we subscribed to CollectionChanged)
        foreach (var tab in _viewModel.Tabs)
        {
            if (!_terminalViews.ContainsKey(tab))
            {
                CreateTerminalView(tab);
            }
        }
        
        // Listen for tab changes
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        
        // Listen for tab collection changes
        _viewModel.Tabs.CollectionChanged += OnTabsCollectionChanged;
    }
    
    private void OnTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Handle new tabs - pre-create their views
        if (e.NewItems != null)
        {
            foreach (TabViewModel tab in e.NewItems)
            {
                // Create the TerminalView for this tab (but don't show it yet)
                CreateTerminalView(tab);
            }
        }
        
        // Handle removed tabs - clean up their views
        if (e.OldItems != null)
        {
            foreach (TabViewModel tab in e.OldItems)
            {
                DestroyTerminalView(tab);
            }
        }
    }
    
    private void CreateTerminalView(TabViewModel tab)
    {
        if (_terminalViews.ContainsKey(tab)) return;
        
        Console.WriteLine($"[MainWindow] Creating TerminalView for tab: {tab.Title}");
        
        var terminalView = new TerminalView
        {
            DataContext = tab.Session,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        
        Console.WriteLine($"[MainWindow] TerminalView created, Session={tab.Session != null}");
        
        terminalView.NewTabRequested += OnNewTabRequested;
        
        _terminalViews[tab] = terminalView;
        
        // If this is the active tab, show it immediately
        if (_viewModel.ActiveTab == tab)
        {
            ShowTab(tab);
        }
    }
    
    private void DestroyTerminalView(TabViewModel tab)
    {
        if (!_terminalViews.TryGetValue(tab, out var view)) return;
        
        // Remove from visual tree if currently shown
        if (_contentContainer?.Children.Contains(view) == true)
        {
            _contentContainer.Children.Remove(view);
        }
        
        // Clean up event handlers
        view.NewTabRequested -= OnNewTabRequested;
        
        // Remove from dictionary
        _terminalViews.Remove(tab);
        
        // Force disposal
        view.DataContext = null;
    }
    
    private void OnActiveTabChanged(object? sender, EventArgs e)
    {
        var activeTab = _viewModel.ActiveTab;
        if (activeTab == null) return;
        
        // Ensure we have a view for this tab
        if (!_terminalViews.ContainsKey(activeTab))
        {
            CreateTerminalView(activeTab);
        }
        
        // Show the new tab with explicit cleanup
        Dispatcher.UIThread.Post(() =>
        {
            ShowTab(activeTab);
            FocusActiveTerminal();
        }, DispatcherPriority.Render);
    }
    
    /// <summary>
    /// Shows a tab's content with complete visual tree isolation.
    /// This method ensures the old content is fully removed before showing new content.
    /// </summary>
    private void ShowTab(TabViewModel tab)
    {
        Console.WriteLine($"[MainWindow] ShowTab called for: {tab.Title}");
        
        if (_contentContainer == null) 
        {
            Console.WriteLine("[MainWindow] ShowTab: ContentContainer is null");
            return;
        }
        
        if (!_terminalViews.TryGetValue(tab, out var newView)) 
        {
            Console.WriteLine("[MainWindow] ShowTab: TerminalView not found in dictionary");
            return;
        }
        
        Console.WriteLine($"[MainWindow] ShowTab: Found TerminalView, Session={newView.Session != null}");
        
        // STEP 1: Get the current view (if any)
        var oldView = _contentContainer.Children.OfType<TerminalView>().FirstOrDefault();
        Console.WriteLine($"[MainWindow] ShowTab: Current view count: {_contentContainer.Children.Count}");
        
        // STEP 2: Clear the container completely
        _contentContainer.Children.Clear();
        
        // STEP 3: Force layout update to ensure visual tree is detached
        _contentContainer.InvalidateVisual();
        _contentContainer.InvalidateMeasure();
        _contentContainer.InvalidateArrange();
        
        // STEP 4: If there was an old view, we keep its DataContext
        // Don't clear it - we want to preserve the session connection
        // The TerminalCanvas handles IsVisible changes properly
        
        // STEP 5: Ensure the new view has the correct DataContext
        newView.DataContext = tab.Session;
        
        // STEP 6: Add the new view
        _contentContainer.Children.Add(newView);
        Console.WriteLine("[MainWindow] ShowTab: Added new view to container");
        
        // STEP 7: Force layout update for new content
        _contentContainer.InvalidateVisual();
        _contentContainer.InvalidateMeasure();
        _contentContainer.InvalidateArrange();
        
        // STEP 8: Ensure the session is started (critical for rendering)
        if (tab.Session != null)
        {
            Console.WriteLine("[MainWindow] ShowTab: Starting session");
            tab.Session.Start();
        }
        
        Console.WriteLine($"[MainWindow] Switched to tab: {tab.Title}");
    }
    
    private void OnOpened(object? sender, EventArgs e)
    {
        FocusActiveTerminal();
    }
    
    private void FocusActiveTerminal()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var activeTab = _viewModel.ActiveTab;
            if (activeTab != null && _terminalViews.TryGetValue(activeTab, out var view))
            {
                view.FocusInput();
            }
        });
    }
    
    private void TypeTextToActiveTerminal(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var activeTab = _viewModel.ActiveTab;
            if (activeTab == null) return;
            
            if (_terminalViews.TryGetValue(activeTab, out var view))
            {
                view.FocusInput();
                // Send the text to the terminal
                view.SendRawInput(text);
                Console.WriteLine($"[Test] Typed text: {text}");
            }
        });
    }
    
    private void StartTestCommandListener()
    {
        var portStr = Environment.GetEnvironmentVariable("DOTTY_TEST_PORT");
        if (string.IsNullOrEmpty(portStr)) return;
        
        if (!int.TryParse(portStr, out int port)) return;
        
        _testCommandCts = new CancellationTokenSource();
        
        Task.Run(async () =>
        {
            try
            {
                _testCommandListener = new TcpListener(IPAddress.Loopback, port);
                _testCommandListener.Start();
                
                Console.WriteLine($"[Test] Command listener started on port {port}");
                
                while (!_testCommandCts.Token.IsCancellationRequested)
                {
                    var client = await _testCommandListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTestClient(client));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Test] Listener error: {ex.Message}");
            }
        });
    }
    
    private async Task HandleTestClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            
            var command = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(command)) return;
            
            Console.WriteLine($"[Test] Received command: {command}");
            
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    switch (command.Trim().ToUpper())
                    {
                        case "NEW_TAB":
                            _viewModel.AddNewTab();
                            break;
                        case "NEXT_TAB":
                            SwitchTab(1);
                            break;
                        case "PREV_TAB":
                            SwitchTab(-1);
                            break;
                        case "CAPTURE":
                            CaptureScreenshot();
                            break;
                        case "CAPTURE_CANVAS":
                            CaptureCanvasScreenshot();
                            break;
                        default:
                            // Handle CAPTURE_AUTO:<frame_count>
                            if (command.Trim().ToUpper().StartsWith("CAPTURE_AUTO:"))
                            {
                                var parts = command.Trim().Split(':');
                                if (parts.Length == 2 && int.TryParse(parts[1], out int frameCount))
                                {
                                    EnableAutoCapture(frameCount);
                                }
                            }
                            // Handle TYPE:text - send text to active terminal
                            else if (command.Trim().ToUpper().StartsWith("TYPE:"))
                            {
                                var text = command.Trim().Substring(5);
                                TypeTextToActiveTerminal(text);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Test] Command error: {ex.Message}");
                }
            });
            
            var response = Encoding.UTF8.GetBytes("OK\n");
            await stream.WriteAsync(response, 0, response.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Test] Client handler error: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }
    
    private void SwitchTab(int direction)
    {
        if (_viewModel.Tabs.Count <= 1) return;
        
        var currentIndex = _viewModel.Tabs.IndexOf(_viewModel.ActiveTab!);
        if (currentIndex < 0) return;
        
        var newIndex = direction > 0 
            ? (currentIndex + 1) % _viewModel.Tabs.Count
            : (currentIndex - 1 + _viewModel.Tabs.Count) % _viewModel.Tabs.Count;
            
        _viewModel.ActiveTab = _viewModel.Tabs[newIndex];
    }

    private void EnableAutoCapture(int frameCount)
    {
        try
        {
            TerminalVisualHandler.EnableAutoCapture(frameCount);
            Console.WriteLine($"[Test] Auto-capture enabled for {frameCount} frames");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Test] Auto-capture enable error: {ex.Message}");
        }
    }

    private void CaptureCanvasScreenshot()
    {
        try
        {
            TerminalVisualHandler.CaptureScreenshot();
            Console.WriteLine("[Test] Canvas screenshot capture triggered - will capture next frame");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Test] Canvas screenshot trigger error: {ex.Message}");
        }
    }

    private void CaptureScreenshot()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var pixelSize = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var renderBitmap = new RenderTargetBitmap(pixelSize);
                
                renderBitmap.Render(this);
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var filename = $"/tmp/dotty_avalonia_{timestamp}.png";
                
                using var stream = System.IO.File.OpenWrite(filename);
                renderBitmap.Save(stream);
                
                Console.WriteLine($"[Test] Screenshot saved to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Test] Screenshot error: {ex.Message}");
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Stop test command listener
        _testCommandCts?.Cancel();
        try { _testCommandListener?.Stop(); } catch { }
        
        // Clean up all views
        foreach (var view in _terminalViews.Values)
        {
            view.DataContext = null;
        }
        _terminalViews.Clear();
        
        // Dispose all tabs
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

    private void OnDuplicateTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is TabViewModel tvm)
        {
            var newTab = new TabViewModel();
            newTab.Title = tvm.Title + " (Copy)";
            _viewModel.Tabs.Add(newTab);
            _viewModel.ActiveTab = newTab;
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is TabViewModel tvm)
        {
            CloseTab(tvm);
        }
    }

    private void OnCloseOtherTabsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is TabViewModel tvm)
        {
            var tabsToClose = _viewModel.Tabs.Where(t => t != tvm).ToList();
            foreach (var tab in tabsToClose)
            {
                CloseTab(tab);
            }
        }
    }

    private void CloseTab(TabViewModel tab)
    {
        DestroyTerminalView(tab);
        tab.Dispose();
        _viewModel.Tabs.Remove(tab);
        if (_viewModel.Tabs.Count > 0)
        {
            if (_viewModel.ActiveTab == tab)
                _viewModel.ActiveTab = _viewModel.Tabs[0];
        }
        else
        {
            Close();
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
                return;
            }
            else if (e.Key == Key.W)
            {
                if (_viewModel.ActiveTab != null)
                {
                    CloseTab(_viewModel.ActiveTab);
                }
                e.Handled = true;
                return;
            }
        }
        
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Tab)
        {
            if (_viewModel.Tabs.Count > 1)
            {
                var currentIndex = _viewModel.Tabs.IndexOf(_viewModel.ActiveTab!);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    currentIndex--;
                    if (currentIndex < 0) currentIndex = _viewModel.Tabs.Count - 1;
                }
                else
                {
                    currentIndex++;
                    if (currentIndex >= _viewModel.Tabs.Count) currentIndex = 0;
                }
                _viewModel.ActiveTab = _viewModel.Tabs[currentIndex];
            }
            e.Handled = true;
            return;
        }
    }
}
