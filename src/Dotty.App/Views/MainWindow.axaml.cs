using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Dotty.Abstractions.Config;
using Dotty.App.ViewModels;
using Dotty.App.Controls.Canvas.Rendering;
using Dotty.App.Configuration;

namespace Dotty.App.Views;

    public partial class MainWindow : Window
    {
        private MainViewModel _viewModel;
        private TcpListener? _testCommandListener;
        private CancellationTokenSource? _testCommandCts;
        
        // Manual content management: Keep track of TerminalView instances per tab
        private readonly Dictionary<TabViewModel, TerminalView> _terminalViews = new();
        private readonly Dictionary<TabViewModel, DispatcherTimer> _inactiveTabTimers = new();
        private readonly Dictionary<TabViewModel, WriteableBitmap> _tabSnapshots = new();
        private int InactiveTabDestroyDelayMs => Generated.Config.InactiveTabDestroyDelayMs;
    private Grid? _contentContainer;
    private Control? _tabBar;
    private SolidColorBrush? _semiTransparentBrush;
    private bool _isHyprland = false;

        public MainWindow()
        {
            InitializeComponent();
            
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            
            Title = Generated.Config.WindowTitle;
            
            // Configure window transparency based on platform and user settings
            ConfigureTransparency();
            
            KeyDown += OnWindowKeyDown;
            Closed += OnClosed;
            Opened += OnOpened;
            
            // Start test command listener for automated testing
            StartTestCommandListener();
        }
        
        /// <summary>
        /// Configures window transparency based on platform detection and user settings.
        /// 
        /// TRANSPARENCY STRATEGY:
        /// ======================
        /// 
        /// 1. Hyprland (Wayland compositor):
        ///    - Use compositor-level transparency via windowrulev2
        ///    - Set solid background, let Hyprland handle opacity
        ///    - Most reliable method for this compositor
        /// 
        /// 2. Other Wayland + WindowOpacity (< 100):
        ///    - Avalonia's Opacity property doesn't work reliably on most Wayland compositors
        ///    - Use brush alpha with semi-transparent background color
        ///    - Set Transparent hint so Avalonia treats window as translucent
        /// 
        /// 3. X11/Windows/macOS + WindowOpacity (< 100):
        ///    - Use window.Opacity property (Avalonia handles this correctly)
        ///    - Set transparent background brush
        /// 
        /// 4. Full transparency modes (Blur/Acrylic/Transparent):
        ///    - Use Avalonia's TransparencyLevelHint system
        ///    - Enables native blur/acrylic effects where supported
        /// 
        /// 5. Default (no transparency):
        ///    - Solid background color from user config
        /// </summary>
        private void ConfigureTransparency()
        {
            var windowOpacity = global::Dotty.Generated.Config.WindowOpacity / 100.0;
            var isWayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";
            var transparency = global::Dotty.Generated.Config.Transparency;
            
            // Case 1: Hyprland - use compositor transparency
            if (DetectHyprland())
            {
                _isHyprland = true;
                Background = new SolidColorBrush(ConfigBridge.ToColor(Generated.Config.Background));
                Console.WriteLine("[MainWindow] Hyprland detected - using compositor transparency (add windowrulev2 to hyprland.conf)");
                return;
            }
            
            // Case 2: Other Wayland + opacity - use brush alpha
            if (windowOpacity < 1.0 && isWayland)
            {
                var baseColor = ConfigBridge.ToColor(Generated.Config.Background);
                byte alpha = (byte)(windowOpacity * 255);
                var transparentColor = new Color(alpha, baseColor.R, baseColor.G, baseColor.B);
                _semiTransparentBrush = new SolidColorBrush(transparentColor);
                Background = _semiTransparentBrush;
                
                // Set Transparent hint so Avalonia treats window as translucent
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                return;
            }
            
            // Case 3: X11/Windows/macOS + opacity - use window.Opacity
            if (windowOpacity < 1.0)
            {
                this.Opacity = windowOpacity;
                Background = Brushes.Transparent;
                return;
            }
            
            // Case 4: Full transparency modes - use Avalonia hints
            if (transparency != TransparencyLevel.None)
            {
                ApplyAvaloniaTransparency(transparency);
                return;
            }
            
            // Case 5: Default - solid background
            Background = new SolidColorBrush(ConfigBridge.ToColor(Generated.Config.Background));
        }
        
        /// <summary>
        /// Detects if running on Hyprland compositor.
        /// Returns true if Hyprland was detected.
        /// </summary>
        private bool DetectHyprland()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;
                
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            var hyprlandSig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
            
            if (desktop?.Contains("Hyprland") == true || hyprlandSig != null)
            {
                Console.WriteLine("[Compositor] Hyprland detected - configure opacity in ~/.config/hypr/hyprland.conf:");
                Console.WriteLine("[Compositor]   windowrulev2 = opacity 0.5,class:^Dotty$");
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Applies Avalonia's transparency settings for full transparency modes (Blur/Acrylic/Transparent).
        /// </summary>
        private void ApplyAvaloniaTransparency(TransparencyLevel transparency)
        {
            switch (transparency)
            {
                case TransparencyLevel.Blur:
                case TransparencyLevel.Acrylic:
                    Background = Brushes.Transparent;
                    this.Opacity = 0.95;
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
                    break;
                    
                case TransparencyLevel.Transparent:
                    Background = Brushes.Transparent;
                    this.Opacity = 0.95;
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                    break;
                    
                case TransparencyLevel.None:
                default:
                    Background = new SolidColorBrush(ConfigBridge.ToColor(Generated.Config.Background));
                    break;
            }
        }
    
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Get references to our manual container and tab bar
        _contentContainer = this.FindControl<Grid>("ContentContainer");
        _tabBar = this.FindControl<Control>("TabBar");
        
        // Sync ContentContainer background with window transparency settings
        // On Hyprland, ContentContainer stays solid (compositor handles transparency)
        // On other platforms with opacity, ContentContainer matches window background
        if (_contentContainer != null && !_isHyprland)
        {
            if (_semiTransparentBrush != null)
            {
                _contentContainer.Background = _semiTransparentBrush;
            }
            else if (Background is SolidColorBrush solidBrush)
            {
                _contentContainer.Background = solidBrush;
            }
        }
        
        // Initialize the first tab's content (lazy - only create when needed)
        if (_viewModel.ActiveTab != null)
        {
            ShowTab(_viewModel.ActiveTab);
        }
        
        // Listen for tab changes
        _viewModel.ActiveTabChanged += OnActiveTabChanged;
        
        // Listen for tab collection changes
        _viewModel.Tabs.CollectionChanged += OnTabsCollectionChanged;
    }
    
    private void OnTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Note: Views are created lazily when tabs become active, not immediately
        // This saves memory when user has many background tabs
        
        // Handle removed tabs - clean up their views immediately to free memory
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
        
        var terminalView = new TerminalView
        {
            DataContext = tab.Session,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        
        terminalView.NewTabRequested += OnNewTabRequested;
        
        _terminalViews[tab] = terminalView;
        
        // Note: Caller is responsible for showing the tab via ShowTab()
        // We don't call ShowTab here to avoid re-entrant calls
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
    
    /// <summary>
    /// Starts a timer to destroy an inactive tab's composition visual after a delay.
    /// Captures a snapshot immediately so fast tab switching shows instant feedback.
    /// </summary>
    private void StartInactiveTabTimer(TabViewModel tab)
    {
        // Cancel any existing timer for this tab
        CancelInactiveTabTimer(tab);
        
        // CAPTURE SNAPSHOT IMMEDIATELY when leaving tab
        // This ensures fast tab switching (click back within 5s) has a snapshot ready
        if (_terminalViews.TryGetValue(tab, out var view) && view != null)
        {
            CaptureTabSnapshot(tab);
        }
        
        // Create a new timer that will destroy the view after delay
        // The snapshot is already captured, so fast switching works instantly
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(InactiveTabDestroyDelayMs)
        };
        
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            _inactiveTabTimers.Remove(tab);
            
            // Only destroy if this tab is not currently active
            if (_viewModel.ActiveTab != tab && _terminalViews.TryGetValue(tab, out var viewToDestroy))
            {
                DestroyTerminalView(tab);
            }
        };
        
        _inactiveTabTimers[tab] = timer;
        timer.Start();
    }
    
    /// <summary>
    /// Cancels the inactive tab timer if one exists for the given tab.
    /// Call this when a tab becomes active again.
    /// </summary>
    private void CancelInactiveTabTimer(TabViewModel tab)
    {
        if (_inactiveTabTimers.TryGetValue(tab, out var timer))
        {
            timer.Stop();
            _inactiveTabTimers.Remove(tab);
        }
    }
    
    /// <summary>
    /// Captures a visual snapshot of the given tab's TerminalView.
    /// This is used to show instant feedback when switching back to the tab.
    /// Upserts (replaces) any existing snapshot for this tab.
    /// </summary>
    private void CaptureTabSnapshot(TabViewModel tab)
    {
        if (!_terminalViews.TryGetValue(tab, out var view)) return;
        if (_contentContainer == null) return;
        
        // Only capture if this view is currently visible
        if (!_contentContainer.Children.Contains(view)) return;
        
        try
        {
            var pixelSize = new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height);
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return;
            
            using var renderBitmap = new RenderTargetBitmap(pixelSize);
            renderBitmap.Render(view);
            
            // Convert to WriteableBitmap for display
            using var stream = new System.IO.MemoryStream();
            renderBitmap.Save(stream);
            stream.Position = 0;
            
            var snapshot = WriteableBitmap.Decode(stream);
            
            // UPSERT: Dispose old snapshot if exists, then store new one
            if (_tabSnapshots.TryGetValue(tab, out var oldSnapshot))
            {
                oldSnapshot.Dispose();
                _tabSnapshots.Remove(tab);
            }
            _tabSnapshots[tab] = snapshot;
        }
        catch (Exception)
        {
            // Snapshot capture failed, continue without it
        }
    }
    
    /// <summary>
    /// Displays a tab's snapshot as a placeholder while the real view loads.
    /// Returns true if a snapshot was shown, false otherwise.
    /// </summary>
    private bool ShowTabSnapshot(TabViewModel tab)
    {
        if (!_tabSnapshots.TryGetValue(tab, out var snapshot)) return false;
        if (_contentContainer == null) return false;
        
        try
        {
            var image = new Image
            {
                Source = snapshot,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            
            // Tag it so we can identify and remove it later
            image.Tag = "tab-snapshot";
            
            _contentContainer.Children.Add(image);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    /// <summary>
    /// Removes the snapshot placeholder from the content container with a fade-out animation.
    /// </summary>
    private async void RemoveTabSnapshot()
    {
        if (_contentContainer == null) return;
        
        var snapshotImages = _contentContainer.Children.OfType<Image>().Where(i => i.Tag as string == "tab-snapshot").ToList();
        foreach (var image in snapshotImages)
        {
            // Fade out over 100ms
            var fadeAnimation = new Avalonia.Animation.Animation
            {
                Duration = TimeSpan.FromMilliseconds(100),
                FillMode = Avalonia.Animation.FillMode.Forward,
                Children =
                {
                    new Avalonia.Animation.KeyFrame
                    {
                        Setters = { new Setter(Avalonia.Visual.OpacityProperty, 1.0) },
                        KeyTime = TimeSpan.FromMilliseconds(0)
                    },
                    new Avalonia.Animation.KeyFrame
                    {
                        Setters = { new Setter(Avalonia.Visual.OpacityProperty, 0.0) },
                        KeyTime = TimeSpan.FromMilliseconds(100)
                    }
                }
            };
            
            await fadeAnimation.RunAsync(image);
            
            _contentContainer.Children.Remove(image);
        }
    }
    
    /// <summary>
    /// Clears the snapshot for a specific tab, freeing its memory.
    /// </summary>
    private void ClearTabSnapshot(TabViewModel tab)
    {
        if (_tabSnapshots.TryGetValue(tab, out var snapshot))
        {
            snapshot.Dispose();
            _tabSnapshots.Remove(tab);
        }
    }
    
    private void OnActiveTabChanged(object? sender, EventArgs e)
    {
        var activeTab = _viewModel.ActiveTab;
        if (activeTab == null) return;
        
        // Cancel any pending destruction for the tab becoming active
        CancelInactiveTabTimer(activeTab);
        
        // Ensure we have a view for this tab (will create lazily if destroyed)
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
    /// Shows a tab's content using snapshot-based instant switching.
    /// 1. Shows snapshot instantly (if available)
    /// 2. Loads real TerminalView
    /// 3. Swaps snapshot for real view once ready
    /// </summary>
    private void ShowTab(TabViewModel tab)
    {
        if (_contentContainer == null) return;
        
        // Remove any existing views and snapshots
        _contentContainer.Children.Clear();
        
        // STEP 1: Show snapshot instantly if available (instant feedback)
        bool hasSnapshot = ShowTabSnapshot(tab);
        
        // STEP 2: Ensure we have a TerminalView for this tab
        if (!_terminalViews.TryGetValue(tab, out var newView)) 
        {
            CreateTerminalView(tab);
            
            if (!_terminalViews.TryGetValue(tab, out newView))
            {
                // Even if we failed to create view, we might have a snapshot
                return;
            }
        }
        
        // Ensure the new view has the correct DataContext
        newView.DataContext = tab.Session;
        
        // STEP 3: Add the real view on top (will cover snapshot or fill empty space)
        // Check if view is already in container to avoid "already has visual parent" error
        if (!_contentContainer.Children.Contains(newView))
        {
            _contentContainer.Children.Add(newView);
        }
        
        // STEP 4: Force immediate render of the real view
        newView.ForceImmediateRender();
        
        // Force layout and render
        newView.InvalidateVisual();
        newView.InvalidateMeasure();
        newView.InvalidateArrange();
        _contentContainer.InvalidateVisual();
        _contentContainer.InvalidateMeasure();
        _contentContainer.InvalidateArrange();
        
        // STEP 5: Remove the snapshot now that real view is rendered (or will be soon)
        // We do this after a brief moment to ensure the view has started rendering
        if (hasSnapshot)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RemoveTabSnapshot();
            }, DispatcherPriority.Render);
        }
        
        // STEP 6: Start the session
        tab.Session?.Start();
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
                
                while (!_testCommandCts.Token.IsCancellationRequested)
                {
                    var client = await _testCommandListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTestClient(client));
                }
            }
            catch (Exception)
            {
                // Test listener error, ignore
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
                catch (Exception)
                {
                    // Command error, ignore
                }
            });
            
            var response = Encoding.UTF8.GetBytes("OK\n");
            await stream.WriteAsync(response, 0, response.Length);
        }
        catch (Exception)
        {
            // Client handler error, ignore
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
        }
        catch (Exception)
        {
            // Auto-capture enable error, ignore
        }
    }

    private void CaptureCanvasScreenshot()
    {
        try
        {
            TerminalVisualHandler.CaptureScreenshot();
        }
        catch (Exception)
        {
            // Canvas screenshot trigger error, ignore
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
            }
            catch (Exception)
            {
                // Screenshot error, ignore
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Stop test command listener
        _testCommandCts?.Cancel();
        try { _testCommandListener?.Stop(); } catch { }
        
        // Cancel all inactive tab timers
        foreach (var timer in _inactiveTabTimers.Values)
        {
            timer.Stop();
        }
        _inactiveTabTimers.Clear();
        
        // Dispose all snapshots
        foreach (var snapshot in _tabSnapshots.Values)
        {
            snapshot.Dispose();
        }
        _tabSnapshots.Clear();
        
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
        if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Escape)
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
        // Cancel any pending destruction timer
        CancelInactiveTabTimer(tab);
        
        // Clear snapshot to free memory
        ClearTabSnapshot(tab);
        
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
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            if (e.Key == Avalonia.Input.Key.T)
            {
                _viewModel.AddNewTab();
                e.Handled = true;
                return;
            }
            else if (e.Key == Avalonia.Input.Key.W)
            {
                if (_viewModel.ActiveTab != null)
                {
                    CloseTab(_viewModel.ActiveTab);
                }
                e.Handled = true;
                return;
            }
        }
        
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.Key == Avalonia.Input.Key.Tab)
        {
            if (_viewModel.Tabs.Count > 1)
            {
                var currentIndex = _viewModel.Tabs.IndexOf(_viewModel.ActiveTab!);
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
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
