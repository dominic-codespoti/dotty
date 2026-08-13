using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Globalization;
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
using Dotty.App.Controls.Canvas.Rendering;
using Dotty;
using Dotty.Abstractions.Config;
using Dotty.App.Configuration;
using Dotty.App.Services;
using Dotty.App.ViewModels;
using Dotty.Terminal.Adapter;

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
        private TabViewModel? _lastActiveTab;
        private int InactiveTabDestroyDelayMs => RuntimeSettings.Current.InactiveTabDestroyDelayMs ?? Generated.Config.InactiveTabDestroyDelayMs;
    private Grid? _contentContainer;
    private Control? _tabBar;
        private SolidColorBrush? _semiTransparentBrush;
        private bool _isHyprland = false;
        private TabViewModel? _windowTitleSubscribedTab;
        private bool _renderTelemetryEnabled = TerminalRenderTelemetry.DefaultEnabled;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Focus reporting (DEC 1004): forward window focus events to the PTY.
        Activated += (_, _) => SendFocusEventToActiveTab(true);
        Deactivated += (_, _) => SendFocusEventToActiveTab(false);

        // Window pixel size tracking for CSI 14 t queries.
        // ClientSize is in DIPs; the protocol replies in physical pixels, so
        // also re-broadcast on scale transitions (physical size changes even
        // when the DIP size does not).
        LayoutUpdated += (_, _) => BroadcastWindowPixelSize();
        ScalingChanged += (_, _) => BroadcastWindowPixelSize();

        UpdateWindowTitle();
        ConfigureTransparency();

        RuntimeSettings.Changed += OnRuntimeSettingsChanged;
        SgrColorArgb.AnsiPaletteChanged += OnAnsiPaletteChanged;
        OnRuntimeSettingsChanged(null, EventArgs.Empty); // apply current runtime settings

        KeyDown += OnWindowKeyDown;
        Closed += OnClosed;
        Opened += OnOpened;

        StartTestCommandListener();
    }

    private void BroadcastWindowPixelSize()
    {
        var size = ClientSize;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(size.Width * scale));
        int h = Math.Max(1, (int)Math.Round(size.Height * scale));
        foreach (var tab in _viewModel.Tabs)
        {
            if (tab.Session?.Adapter is Terminal.Adapter.TerminalAdapter a)
                a.SetWindowPixelSize(w, h);
        }
    }

    private void SendFocusEventToActiveTab(bool focused)
        {
            var tab = _viewModel.ActiveTab;
            if (tab?.Session?.Adapter is Terminal.Adapter.TerminalAdapter adapter && adapter.FocusReportingEnabled)
            {
                tab.Session.WriteInput(focused
                    ? new byte[] { 0x1b, (byte)'[', (byte)'I' }
                    : new byte[] { 0x1b, (byte)'[', (byte)'O' });
            }
        }

        private void OnRuntimeSettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ConfigureTransparency();
                ClearAllTabSnapshots();

                foreach (var view in _terminalViews.Values)
                {
                    try { view.ForceImmediateRender(); } catch { }
                }
            });
        }

        private void OnAnsiPaletteChanged(object? sender, AnsiPaletteChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var tab in _viewModel.Tabs)
                {
                    if (!tab.HasSession)
                    {
                        continue;
                    }

                    try
                    {
                        tab.Session.Adapter.Buffer.StyleSet.RemapAnsiPalette(e.PreviousPalette, e.CurrentPalette);
                    }
                    catch { }
                }

                foreach (var view in _terminalViews.Values)
                {
                    try { view.ForceImmediateRender(); } catch { }
                }
            });
        }

        private static uint GetRuntimeBackgroundArgb()
        {
            var background = RuntimeSettings.Current.Background;
            if (!string.IsNullOrWhiteSpace(background))
            {
                try { return ConfigBridge.FromHex(background); } catch { }
            }

            return Generated.Config.Background;
        }

        private static double GetRuntimeWindowOpacity()
        {
            return RuntimeSettings.GetWindowOpacity() / 100.0;
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
            var windowOpacity = GetRuntimeWindowOpacity();
            var isWayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";
            var transparency = RuntimeSettings.GetTransparency();
            var backgroundColor = ConfigBridge.ToColor(GetRuntimeBackgroundArgb());

            this.Opacity = 1.0;
            _semiTransparentBrush = null;
            TransparencyLevelHint = Array.Empty<WindowTransparencyLevel>();
            
            // Case 1: Hyprland - use compositor transparency
            if (DetectHyprland())
            {
                _isHyprland = true;
                Background = new SolidColorBrush(backgroundColor);
                SyncContentContainerBackground();
                return;
            }

            _isHyprland = false;
            
            // Case 2: Other Wayland + opacity - use brush alpha
            if (windowOpacity < 1.0 && isWayland)
            {
                byte alpha = (byte)(windowOpacity * 255);
                var transparentColor = new Color(alpha, backgroundColor.R, backgroundColor.G, backgroundColor.B);
                _semiTransparentBrush = new SolidColorBrush(transparentColor);
                Background = _semiTransparentBrush;
                
                // Set Transparent hint so Avalonia treats window as translucent
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                SyncContentContainerBackground();
                return;
            }
            
            // Case 3: X11/Windows/macOS + opacity - use window.Opacity
            if (windowOpacity < 1.0)
            {
                this.Opacity = windowOpacity;
                Background = Brushes.Transparent;
                SyncContentContainerBackground();
                return;
            }
            
            // Case 4: Full transparency modes - use Avalonia hints
            if (transparency != TransparencyLevel.None)
            {
                ApplyAvaloniaTransparency(transparency, backgroundColor);
                SyncContentContainerBackground();
                return;
            }
            
            // Case 5: Default - solid background
            Background = new SolidColorBrush(backgroundColor);
            SyncContentContainerBackground();
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
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Applies Avalonia's transparency settings for full transparency modes (Blur/Acrylic/Transparent).
        /// </summary>
        private void ApplyAvaloniaTransparency(TransparencyLevel transparency, Color backgroundColor)
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
                    Background = new SolidColorBrush(backgroundColor);
                    break;
            }
        }

        private void SyncContentContainerBackground()
        {
            if (_contentContainer == null || _isHyprland)
            {
                return;
            }

            if (_semiTransparentBrush != null)
            {
                _contentContainer.Background = _semiTransparentBrush;
            }
            else if (Background is IBrush brush)
            {
                _contentContainer.Background = brush;
            }
        }

        private void ClearAllTabSnapshots()
        {
            foreach (var snapshot in _tabSnapshots.Values)
            {
                try { snapshot.Dispose(); } catch { }
            }

            _tabSnapshots.Clear();
            RemoveTabSnapshotImmediate();
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
        SyncContentContainerBackground();
        
        // Initialize the first tab's content (lazy - only create when needed)
        if (_viewModel.ActiveTab != null)
        {
            SubscribeToActiveTabTitle(_viewModel.ActiveTab);
            ShowTab(_viewModel.ActiveTab);
            _lastActiveTab = _viewModel.ActiveTab;
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
                CancelInactiveTabTimer(tab);
                ClearTabSnapshot(tab);
                DestroyTerminalView(tab);

                if (ReferenceEquals(_lastActiveTab, tab))
                {
                    _lastActiveTab = null;
                }

                if (ReferenceEquals(_windowTitleSubscribedTab, tab))
                {
                    UnsubscribeFromActiveTabTitle(tab);
                }
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
        terminalView.RenderTelemetry.SetEnabled(_renderTelemetryEnabled);
        
        terminalView.NewTabRequested += OnNewTabRequested;
        
        _terminalViews[tab] = terminalView;

        // Configure the adapter: theme colors, terminal identity.
        if (tab.Session.Adapter is Terminal.Adapter.TerminalAdapter adapter)
        {
            adapter.SetDefaultColors(
                $"#{Dotty.Generated.Config.Colors.Foreground & 0xFFFFFF:X6}",
                $"#{Dotty.Generated.Config.Colors.Background & 0xFFFFFF:X6}");
            // DA2: Dotty 1.x.y => encoded as major*10000 + minor*100 + patch
            var v = VersionInfo.AssemblyVersion;
            var parts = v.Split('.');
            int da2Ver = 0;
            if (parts.Length >= 3 && int.TryParse(parts[0], out var maj) &&
                int.TryParse(parts[1], out var min) &&
                int.TryParse(parts[2], out var pat))
                da2Ver = maj * 10000 + min * 100 + pat;
            adapter.SetTerminalIdentity($"\u001b[>1;{da2Ver};0c");
        }

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
    /// We intentionally avoid snapshotting on every switch because the mounted view
    /// already gives us fast reactivation during the grace period.
    /// </summary>
    private void StartInactiveTabTimer(TabViewModel tab)
    {
        // Cancel any existing timer for this tab
        CancelInactiveTabTimer(tab);

        // Create a new timer that will destroy the view after delay
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
                // Win 3: Clear inactive tab caches - aggressively free memory while keeping session running
                // Clear scrollback buffer to free memory (this is the biggest win)
                try
                {
                    if (tab.Session?.Adapter?.Buffer is { } buffer)
                    {
                        buffer.TrimScrollback(100); // Keep only last 100 lines instead of full scrollback
                    }
                }
                catch { /* ignore scrollback clear errors */ }
                
                ClearTabSnapshot(tab);
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
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Round(view.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(view.Bounds.Height * scale)));
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

    private void RemoveTabSnapshotImmediate()
    {
        if (_contentContainer == null) return;

        var snapshotImages = _contentContainer.Children
            .OfType<Image>()
            .Where(i => i.Tag as string == "tab-snapshot")
            .ToList();

        foreach (var image in snapshotImages)
        {
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

        SubscribeToActiveTabTitle(activeTab);
        UpdateWindowTitle();

        var previousTab = _lastActiveTab;
        _lastActiveTab = activeTab;

        if (previousTab != null && !ReferenceEquals(previousTab, activeTab))
        {
            StartInactiveTabTimer(previousTab);
        }
        
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

        // Remove any previous snapshot overlays before deciding whether we need a new one.
        RemoveTabSnapshotImmediate();

        // STEP 1: Show snapshot instantly only when the target view is not already mounted.
        bool hasSnapshot = !_contentContainer.Children.Contains(newView) && ShowTabSnapshot(tab);

        foreach (var existingView in _terminalViews.Values)
        {
            if (_contentContainer.Children.Contains(existingView))
            {
                existingView.IsVisible = ReferenceEquals(existingView, newView);
            }
        }
        
        // STEP 3: Add the real view on top (will cover snapshot or fill empty space)
        // Keep views mounted once added so tab switches do not need to rebuild the tree.
        bool addedView = false;
        if (!_contentContainer.Children.Contains(newView))
        {
            _contentContainer.Children.Add(newView);
            addedView = true;
        }
        newView.IsVisible = true;
        
        // STEP 4: Force immediate render of the real view
        newView.ForceImmediateRender();
        
        // A full measure/arrange invalidation on every tab switch is expensive.
        // We only request a visual refresh, and only nudge the container when a new
        // view was actually inserted into the tree.
        newView.InvalidateVisual();
        if (addedView)
        {
            _contentContainer.InvalidateVisual();
        }
        
        // STEP 5: Remove the snapshot now that real view is rendered (or will be soon)
        // We do this after a brief moment to ensure the view has started rendering
        if (hasSnapshot)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RemoveTabSnapshot();
            }, DispatcherPriority.Render);
        }
        
        UpdateWindowTitle();
    }

    private void SubscribeToActiveTabTitle(TabViewModel tab)
    {
        if (ReferenceEquals(_windowTitleSubscribedTab, tab))
        {
            return;
        }

        if (_windowTitleSubscribedTab != null)
        {
            UnsubscribeFromActiveTabTitle(_windowTitleSubscribedTab);
        }

        tab.PropertyChanged += OnActiveTabPropertyChanged;
        _windowTitleSubscribedTab = tab;
    }

    private void UnsubscribeFromActiveTabTitle(TabViewModel tab)
    {
        tab.PropertyChanged -= OnActiveTabPropertyChanged;
        if (ReferenceEquals(_windowTitleSubscribedTab, tab))
        {
            _windowTitleSubscribedTab = null;
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Title) || string.IsNullOrEmpty(e.PropertyName))
        {
            Dispatcher.UIThread.Post(UpdateWindowTitle);
        }
    }

    private void UpdateWindowTitle()
    {
        var tabTitle = _viewModel.ActiveTab?.Title;
        Title = string.IsNullOrWhiteSpace(tabTitle)
            ? Generated.Config.WindowTitle
            : $"{tabTitle} - {Generated.Config.WindowTitle}";
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

    private string GetHarnessStatsJson()
    {
        int activeTabIndex = _viewModel.ActiveTab == null ? -1 : _viewModel.Tabs.IndexOf(_viewModel.ActiveTab);

        string scrollbackStats = "null";
        if (_viewModel.ActiveTab != null && _terminalViews.TryGetValue(_viewModel.ActiveTab, out var activeView))
        {
            scrollbackStats = activeView.GetScrollbackStats();
        }

        var output = new StringBuilder();
        output.Append('{')
            .Append("\"totalTabs\":").Append(_viewModel.Tabs.Count)
            .Append(",\"sessionsCreated\":").Append(_viewModel.Tabs.Count(tab => tab.HasSession))
            .Append(",\"sessionsStarted\":").Append(_viewModel.Tabs.Count(tab => tab.IsSessionStarted))
            .Append(",\"mountedViews\":").Append(_terminalViews.Count)
            .Append(",\"inactiveTimers\":").Append(_inactiveTabTimers.Count)
            .Append(",\"snapshots\":").Append(_tabSnapshots.Count)
            .Append(",\"activeTabIndex\":").Append(activeTabIndex)
            .Append(",\"scrollback\":").Append(scrollbackStats)
            .Append(",\"renderTelemetry\":").Append(GetRenderTelemetryJson())
            .Append('}');
        return output.ToString();
    }

    private void StartRenderTelemetry()
    {
        _renderTelemetryEnabled = true;
        foreach (var view in _terminalViews.Values)
        {
            view.RenderTelemetry.Start();
        }
    }

    private void StopRenderTelemetry()
    {
        _renderTelemetryEnabled = false;
        foreach (var view in _terminalViews.Values)
        {
            view.RenderTelemetry.Stop();
        }
    }

    private void ResetRenderTelemetry()
    {
        foreach (var view in _terminalViews.Values)
        {
            view.RenderTelemetry.Reset();
        }
    }

    private TerminalRenderTelemetrySnapshot[] CaptureRenderTelemetrySnapshots()
    {
        var snapshots = new TerminalRenderTelemetrySnapshot[_terminalViews.Count];
        int index = 0;
        foreach (var view in _terminalViews.Values)
        {
            snapshots[index++] = view.RenderTelemetry.Snapshot();
        }
        return snapshots;
    }

    private string GetRenderTelemetryJson()
    {
        var snapshots = CaptureRenderTelemetrySnapshots();
        var aggregate = TerminalRenderTelemetry.Aggregate(snapshots);
        var active = TerminalRenderTelemetrySnapshot.Empty;
        if (_viewModel.ActiveTab != null &&
            _terminalViews.TryGetValue(_viewModel.ActiveTab, out var activeView))
        {
            active = activeView.RenderTelemetry.Snapshot();
        }

        long heapSize = GC.GetGCMemoryInfo().HeapSizeBytes;
        var output = new StringBuilder(2_048);
        output.Append('{')
            .Append("\"fps\":");
        AppendJsonNumber(output, aggregate.RenderRate);
        output.Append(",\"fpsMin\":0,\"fpsMax\":");
        AppendJsonNumber(output, aggregate.RenderRate);
        output.Append(",\"fpsAvg\":");
        AppendJsonNumber(output, aggregate.RenderRate);
        output.Append(",\"frameTimeMin\":");
        AppendJsonNumber(output, aggregate.MinimumRenderMilliseconds);
        output.Append(",\"frameTimeMax\":");
        AppendJsonNumber(output, aggregate.MaximumRenderMilliseconds);
        output.Append(",\"frameTimeAvg\":");
        AppendJsonNumber(output, aggregate.AverageRenderMilliseconds);
        output.Append(",\"parserBytesPerSec\":0,\"parserSeqPerSec\":0")
            .Append(",\"totalBytes\":0,\"totalSequences\":0")
            .Append(",\"heapSize\":").Append(heapSize)
            .Append(",\"allocatedBytes\":").Append(aggregate.TotalRenderAllocatedBytes)
            .Append(",\"workingSet\":").Append(Environment.WorkingSet)
            .Append(",\"gen0\":").Append(GC.CollectionCount(0))
            .Append(",\"gen1\":").Append(GC.CollectionCount(1))
            .Append(",\"gen2\":").Append(GC.CollectionCount(2))
            .Append(",\"inputLatencyMin\":0,\"inputLatencyMax\":0,\"inputLatencyAvg\":0")
            .Append(",\"scrollLinesPerSec\":0,\"scrollTimeAvg\":0")
            .Append(",\"cellUpdatesPerSec\":0,\"totalCellsUpdated\":0")
            .Append(",\"rawCounters\":{")
            .Append("\"renderNotifications\":").Append(aggregate.RenderNotifications)
            .Append(",\"coalescedRenderNotifications\":").Append(aggregate.CoalescedRenderNotifications)
            .Append(",\"uiRenderUpdates\":").Append(aggregate.UiRenderUpdates)
            .Append(",\"frameRequests\":").Append(aggregate.FrameRequests)
            .Append(",\"renderCalls\":").Append(aggregate.RenderCalls)
            .Append(",\"contentRenderAttempts\":").Append(aggregate.ContentRenderAttempts)
            .Append(",\"contentFrames\":").Append(aggregate.ContentFrames)
            .Append(",\"bufferLockMisses\":").Append(aggregate.BufferLockMisses)
            .Append(",\"bitmapRecreations\":").Append(aggregate.BitmapRecreations)
            .Append(",\"renderP95UpperBoundMs\":");
        AppendJsonNumber(output, aggregate.P95RenderMilliseconds);
        output.Append(",\"averageRenderAllocatedBytes\":");
        AppendJsonNumber(output, aggregate.AverageRenderAllocatedBytes);
        output.Append(",\"maximumRenderAllocatedBytes\":").Append(aggregate.MaximumRenderAllocatedBytes)
            .Append("},\"active\":");
        AppendRenderTelemetrySnapshotJson(output, active);
        output.Append(",\"aggregate\":");
        AppendRenderTelemetrySnapshotJson(output, aggregate);
        output.Append('}');
        return output.ToString();
    }

    private static void AppendRenderTelemetrySnapshotJson(
        StringBuilder output,
        TerminalRenderTelemetrySnapshot snapshot)
    {
        output.Append('{')
            .Append("\"enabled\":").Append(snapshot.Enabled ? "true" : "false")
            .Append(",\"elapsedSeconds\":");
        AppendJsonNumber(output, snapshot.ElapsedSeconds);
        output.Append(",\"renderNotifications\":").Append(snapshot.RenderNotifications)
            .Append(",\"coalescedRenderNotifications\":").Append(snapshot.CoalescedRenderNotifications)
            .Append(",\"uiRenderUpdates\":").Append(snapshot.UiRenderUpdates)
            .Append(",\"frameRequests\":").Append(snapshot.FrameRequests)
            .Append(",\"renderCalls\":").Append(snapshot.RenderCalls)
            .Append(",\"contentRenderAttempts\":").Append(snapshot.ContentRenderAttempts)
            .Append(",\"contentFrames\":").Append(snapshot.ContentFrames)
            .Append(",\"bufferLockMisses\":").Append(snapshot.BufferLockMisses)
            .Append(",\"bitmapRecreations\":").Append(snapshot.BitmapRecreations)
            .Append(",\"renderRate\":");
        AppendJsonNumber(output, snapshot.RenderRate);
        output.Append(",\"renderMinMs\":");
        AppendJsonNumber(output, snapshot.MinimumRenderMilliseconds);
        output.Append(",\"renderMaxMs\":");
        AppendJsonNumber(output, snapshot.MaximumRenderMilliseconds);
        output.Append(",\"renderAvgMs\":");
        AppendJsonNumber(output, snapshot.AverageRenderMilliseconds);
        output.Append(",\"renderP95UpperBoundMs\":");
        AppendJsonNumber(output, snapshot.P95RenderMilliseconds);
        output.Append(",\"contentMaxMs\":");
        AppendJsonNumber(output, snapshot.MaximumContentMilliseconds);
        output.Append(",\"contentAvgMs\":");
        AppendJsonNumber(output, snapshot.AverageContentMilliseconds);
        output.Append(",\"renderAllocatedBytes\":").Append(snapshot.TotalRenderAllocatedBytes)
            .Append(",\"renderAllocatedBytesMax\":").Append(snapshot.MaximumRenderAllocatedBytes)
            .Append(",\"renderAllocatedBytesAvg\":");
        AppendJsonNumber(output, snapshot.AverageRenderAllocatedBytes);
        output.Append(",\"lastBufferGeneration\":").Append(snapshot.LastBufferGeneration)
            .Append(",\"lastRenderScale\":");
        AppendJsonNumber(output, snapshot.LastRenderScale);
        output.Append(",\"lastPixelWidth\":").Append(snapshot.LastPixelWidth)
            .Append(",\"lastPixelHeight\":").Append(snapshot.LastPixelHeight)
            .Append(",\"presentFrames\":").Append(snapshot.PresentFrames)
            .Append(",\"presentMinMs\":");
        AppendJsonNumber(output, snapshot.MinimumPresentMilliseconds);
        output.Append(",\"presentMaxMs\":");
        AppendJsonNumber(output, snapshot.MaximumPresentMilliseconds);
        output.Append(",\"presentAvgMs\":");
        AppendJsonNumber(output, snapshot.AveragePresentMilliseconds);
        output.Append(",\"renderDurationHistogram\":[");
        for (int i = 0; i < snapshot.RenderDurationHistogram.Length; i++)
        {
            if (i > 0)
            {
                output.Append(',');
            }
            output.Append(snapshot.RenderDurationHistogram[i]);
        }
        output.Append("]}");
    }

    private static void AppendJsonNumber(StringBuilder output, double value)
    {
        output.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }
    
    private string GetTerminalStateJson()
    {
        int cursorRow = 0, cursorCol = 0, rows = 24, cols = 80;
        int scrollbackLines = 0;
        bool isAlternate = false;
        string title = _viewModel.ActiveTab?.Title ?? "";
        
        var tab = _viewModel.ActiveTab;
        if (tab?.Session?.Adapter?.Buffer is TerminalBuffer buf)
        {
            cursorRow = buf.CursorRow;
            cursorCol = buf.CursorCol;
            rows = buf.Rows;
            cols = buf.Columns;
            scrollbackLines = buf.ScrollbackCount;
            isAlternate = buf.IsAlternateScreenActive;
        }
        
        var sb = new StringBuilder();
        sb.Append("{\"cursorRow\":").Append(cursorRow)
          .Append(",\"cursorCol\":").Append(cursorCol)
          .Append(",\"rows\":").Append(rows)
          .Append(",\"cols\":").Append(cols)
          .Append(",\"scrollbackLines\":").Append(scrollbackLines)
          .Append(",\"isAlternateScreen\":").Append(isAlternate ? "true" : "false")
          .Append(",\"title\":\"").Append(EscapeJson(title)).Append("\"}")
          ;
        return sb.ToString();
    }

    private string DumpTerminalScreen()
    {
        var tab = _viewModel.ActiveTab;
        if (tab?.Session?.Adapter?.Buffer is not TerminalBuffer buf)
            return "DUMP EMPTY";

        var output = new StringBuilder();
        output.Append("DUMP OK\n");
        output.Append("R=").Append(buf.Rows)
              .Append(" C=").Append(buf.Columns)
              .Append(" CUR=").Append(buf.CursorRow)
              .Append(',').Append(buf.CursorCol)
              .Append('\n');

        for (int r = 0; r < buf.Rows; r++)
        {
            AppendAnsiLine(output, buf, r);
            output.Append('\n');
        }

        output.Append("END");
        return output.ToString();
    }

    private static void AppendAnsiLine(StringBuilder output, TerminalBuffer buf, int row)
    {
        int prevFg = -1, prevBg = -1;
        bool prevBold = false, prevItalic = false, prevUnderline = false;
        bool prevInverse = false, prevStrikethrough = false, prevOverline = false;

        for (int c = 0; c < buf.Columns; c++)
        {
            var cell = buf.GetCell(row, c);
            var cold = buf.GetColdCell(row, c);

            if (cell.IsContinuation)
            {
                output.Append(' ');
                continue;
            }

            string? ch;
            if (cell.Rune == 0)
            {
                ch = " ";
            }
            else
            {
                ch = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
                ch ??= " ";
            }

            var style = buf.StyleSet.GetStyle(cell.StyleId);

            // Check if attributes changed from previous cell
            bool attrChanged = false;
            int fg = style.Foreground.IsEmpty ? -1 : (int)(style.Foreground.Argb & 0xFFFFFF);
            int bg = style.Background.IsEmpty ? -1 : (int)(style.Background.Argb & 0xFFFFFF);

            if (fg != prevFg || bg != prevBg ||
                style.Bold != prevBold || style.Italic != prevItalic ||
                style.Underline != prevUnderline || style.Inverse != prevInverse ||
                style.Strikethrough != prevStrikethrough || style.Overline != prevOverline)
            {
                attrChanged = true;
                prevFg = fg;
                prevBg = bg;
                prevBold = style.Bold;
                prevItalic = style.Italic;
                prevUnderline = style.Underline;
                prevInverse = style.Inverse;
                prevStrikethrough = style.Strikethrough;
                prevOverline = style.Overline;
            }

            if (attrChanged)
            {
                output.Append("\e[0m");
                if (style.Bold) output.Append("\e[1m");
                if (style.Faint) output.Append("\e[2m");
                if (style.Italic) output.Append("\e[3m");
                if (style.Underline) output.Append("\e[4m");
                if (style.SlowBlink) output.Append("\e[5m");
                if (style.Inverse) output.Append("\e[7m");
                if (style.Strikethrough) output.Append("\e[9m");
                if (style.Overline) output.Append("\e[53m");
                if (fg >= 0) output.Append("\e[38;2;").Append(style.Foreground.R).Append(';').Append(style.Foreground.G).Append(';').Append(style.Foreground.B).Append('m');
                if (bg >= 0) output.Append("\e[48;2;").Append(style.Background.R).Append(';').Append(style.Background.G).Append(';').Append(style.Background.B).Append('m');
            }

            // Escape special characters for safe display
            foreach (char chChar in ch)
            {
                if (chChar == '\n') output.Append("\\n");
                else if (chChar == '\r') output.Append("\\r");
                else if (chChar == '\t') output.Append("\\t");
                else if (chChar == '\e') output.Append("\\e");
                else if (chChar < 32) output.Append(' ');
                else output.Append(chChar);
            }
        }

        output.Append("\e[0m");
    }

    private static char? MapKeyToControl(string keyName)
    {
        return keyName.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => '\r',
            "TAB" => '\t',
            "ESCAPE" or "ESC" => '\x1b',
            "BACKSPACE" or "BS" => '\x7f',
            "CTRLC" or "CTRL_C" or "CTRL-C" => '\x03',
            "CTRLD" or "CTRL_D" or "CTRL-D" => '\x04',
            "CTRLZ" or "CTRL_Z" or "CTRL-Z" => '\x1a',
            "CTRLL" or "CTRL_L" or "CTRL-L" => '\x0c',
            "CTRLU" or "CTRL_U" or "CTRL-U" => '\x15',
            "DEL" or "DELETE" => '\x7f',
            "SPACE" => ' ',
            _ => null,
        };
    }

    private static string EscapeJson(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
    
    private async Task WaitForHarnessIdleAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                completion.TrySetResult(true);
                return;
            }

            topLevel.RequestAnimationFrame(_ =>
            {
                topLevel.RequestAnimationFrame(__ => completion.TrySetResult(true));
            });
        });

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private bool CaptureHarnessVisual(bool canvasOnly)
    {
        try
        {
            Visual target = this;
            if (canvasOnly &&
                _viewModel.ActiveTab != null &&
                _terminalViews.TryGetValue(_viewModel.ActiveTab, out var activeView))
            {
                target = activeView.RenderCaptureTarget;
            }

            var topLevel = TopLevel.GetTopLevel(target);
            double scale = topLevel?.RenderScaling ?? 1.0;
            int width = Math.Max(1, (int)Math.Ceiling(target.Bounds.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(target.Bounds.Height * scale));
            var pixelSize = new PixelSize(width, height);
            var dpi = new Vector(96.0 * scale, 96.0 * scale);
            using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
            bitmap.Render(target);

            string prefix = canvasOnly ? "dotty_canvas" : "dotty_avalonia";
            string path = Path.Combine(
                Path.GetTempPath(),
                $"{prefix}_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Harness capture failed: {ex.Message}");
            return false;
        }
    }

    private bool LoadHarnessRenderScenario()
    {
        var tab = _viewModel.ActiveTab;
        if (tab?.Session?.Adapter is not Terminal.Adapter.TerminalAdapter adapter)
        {
            return false;
        }

        const string scenario =
            "\u001b[2J\u001b[H" +
            "\u001b[1;38;2;123;211;255mDotty Avalonia render baseline\u001b[0m\r\n" +
            "\u001b[30;47m black \u001b[31m red \u001b[32m green \u001b[33m yellow " +
            "\u001b[34m blue \u001b[35m magenta \u001b[36m cyan \u001b[37m white \u001b[0m\r\n" +
            "\u001b[1mBold\u001b[0m  \u001b[3mItalic\u001b[0m  \u001b[4mUnderline\u001b[0m  " +
            "\u001b[9mStrike\u001b[0m  \u001b[53mOverline\u001b[0m\r\n" +
            "Ligatures: != == === => -> >= <=  |  combining: e\u0301 a\u0308\r\n" +
            "Wide: \u65e5\u672c\u8a9e \u4e2d\u6587 \ud55c\uad6d\uc5b4  Emoji: \ud83d\ude80 \ud83e\uddea \ud83d\udcbb \u2764\ufe0f\r\n" +
            "\u250c\u2500\u2500\u2500\u2500\u2500\u2500\u2510  \u2588\u2588\u2588 \u2593\u2592\u2591  \u2502 box and block geometry \u2502\r\n" +
            "\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2518  \u001b[38;2;255;170;80mTrueColor foreground\u001b[0m\r\n" +
            "\u001b]8;;https://example.com\u001b\\Hyperlink sample\u001b]8;;\u001b\\  " +
            "\u001b[48;2;45;55;72m rounded/background span \u001b[0m\r\n" +
            "Cursor baseline below; selection and search are separate scenarios.\r\n" +
            "\u001b[11;5H\u001b[6 q";

        byte[] bytes = Encoding.UTF8.GetBytes(scenario);
        lock (adapter.Buffer.SyncRoot)
        {
            tab.Session.Parser.Feed(bytes);
        }
        adapter.FlushRender();
        return true;
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
                
                // Set a reasonable backlog to prevent connection issues under load
                // Note: Start() already sets a default backlog, but we're being explicit
                
                while (!_testCommandCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _testCommandListener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleTestClient(client), _testCommandCts.Token);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Listener was stopped, exit gracefully
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Listener not started or was stopped
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Log and continue, don't let one bad connection kill the listener
                        System.Diagnostics.Debug.WriteLine($"Test listener accept error: {ex.Message}");
                        await Task.Delay(100, _testCommandCts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log startup errors
                System.Diagnostics.Debug.WriteLine($"Test command listener failed to start: {ex.Message}");
            }
        });
    }
    
    private async Task HandleTestClient(TcpClient client)
    {
        try
        {
            // Set socket timeouts
            client.ReceiveTimeout = 10000; // 10 seconds
            client.SendTimeout = 10000;
            
            using var stream = client.GetStream();
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            using var writer = new System.IO.StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            
            // Keep connection open for multiple commands (persistent connection)
            while (client.Connected && !_testCommandCts!.Token.IsCancellationRequested)
            {
                string? command;
                try
                {
                    command = await reader.ReadLineAsync(_testCommandCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation requested - exit gracefully
                    break;
                }
                catch (IOException ex) when (ex.Message.Contains("Operation canceled") || 
                                             ex.Message.Contains("timed out") ||
                                             ex.Message.Contains("Connection reset"))
                {
                    // Connection closed or timeout - exit gracefully
                    break;
                }
                
                if (string.IsNullOrEmpty(command))
                {
                    // Client closed the connection
                    break;
                }

                // Handle command and send response
                var responseText = await ProcessTestCommandAsync(command);
                
                try
                {
                    await writer.WriteLineAsync(responseText);
                }
                catch (Exception)
                {
                    // Client disconnected - exit
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - we don't want to crash the app due to test client errors
            System.Diagnostics.Debug.WriteLine($"Test client handler error: {ex.Message}");
        }
        finally
        {
            // Ensure client is closed
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }
    
    private async Task<string> ProcessTestCommandAsync(string command)
    {
        // Handle STATS command synchronously - it needs a response
        if (string.Equals(command.Trim(), "STATS", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var statsJson = await Dispatcher.UIThread.InvokeAsync(GetHarnessStatsJson);
                return statsJson;
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{ex.Message}\"}}";
            }
        }

        if (string.Equals(command.Trim(), "WAIT_FOR_IDLE", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await WaitForHarnessIdleAsync();
                return "OK";
            }
            catch (Exception ex)
            {
                return $"ERROR:{ex.Message}";
            }
        }
        
        // Handle other commands on UI thread
        var commandResult = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                switch (command.Trim().ToUpper())
                {
                    case "NEW_TAB":
                        _viewModel.AddNewTab();
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "NEW_TAB_BG":
                    case "NEW_TAB_BACKGROUND":
                        _viewModel.AddNewTab(activate: false);
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "CLOSE_TAB":
                        if (_viewModel.ActiveTab != null)
                            CloseTab(_viewModel.ActiveTab);
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "NEXT_TAB":
                        SwitchTab(1);
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "PREV_TAB":
                        SwitchTab(-1);
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "CAPTURE":
                        return CaptureHarnessVisual(canvasOnly: false)
                            ? (success: true, response: (string?)null, error: (string?)null)
                            : (success: false, response: (string?)null, error: "Window capture failed");
                    case "CAPTURE_CANVAS":
                        return CaptureHarnessVisual(canvasOnly: true)
                            ? (success: true, response: (string?)null, error: (string?)null)
                            : (success: false, response: (string?)null, error: "Canvas capture failed");
                    case "RENDER_SCENARIO":
                    case "RENDER_SCENARIO:CORE":
                        return LoadHarnessRenderScenario()
                            ? (success: true, response: (string?)null, error: (string?)null)
                            : (success: false, response: (string?)null, error: "No active terminal session");
                    case "SHUTDOWN":
                        // Close the application
                        Close();
                        return (success: true, response: (string?)null, error: (string?)null);
                    case var resizeCmd when resizeCmd.StartsWith("RESIZE:", StringComparison.OrdinalIgnoreCase):
                        var resizeParts = resizeCmd.Substring(7).Split(':');
                        if (resizeParts.Length >= 2 &&
                            int.TryParse(resizeParts[0], out int resizeCols) &&
                            int.TryParse(resizeParts[1], out int resizeRows))
                        {
                            var tab = _viewModel.ActiveTab;
                            if (tab?.Session != null)
                                tab.Session.Resize(resizeCols, resizeRows);
                            return (success: true, response: (string?)null, error: (string?)null);
                        }
                        return (success: false, response: (string?)null, error: "Invalid RESIZE format. Use RESIZE:cols:rows");
                    case "DUMP":
                        var dumpText = DumpTerminalScreen();
                        return (success: true, response: dumpText, error: (string?)null);
                    case "GET_STATE":
                        var stateJson = GetTerminalStateJson();
                        return (success: true, response: stateJson, error: (string?)null);
                    case "COPY":
                    case "PASTE":
                        // Clipboard operations not fully implemented in headless mode
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "SCREENSHOT":
                        // Legacy no-op kept for older harness clients; use CAPTURE/CAPTURE_CANVAS.
                        return (success: true, response: "0", error: (string?)null);
                    case "PERF:START":
                        StartRenderTelemetry();
                        return (success: true, response: (string?)null, error: (string?)null);
                    case "PERF:STOP":
                        StopRenderTelemetry();
                        return (success: true, response: GetRenderTelemetryJson(), error: (string?)null);
                    case "PERF:GET":
                    case "PERF:SNAPSHOT":
                        return (success: true, response: GetRenderTelemetryJson(), error: (string?)null);
                    case "PERF:RESET":
                        ResetRenderTelemetry();
                        return (success: true, response: (string?)null, error: (string?)null);
                    default:
                        // Handle TYPE:text - send text to active terminal
                        if (command.Trim().ToUpper().StartsWith("TYPE:"))
                        {
                            var text = command.Trim().Substring(5);
                            TypeTextToActiveTerminal(text);
                            return (success: true, response: (string?)null, error: (string?)null);
                        }
                        // Handle KEY:keyname - send control character
                        if (command.Trim().ToUpper().StartsWith("KEY:"))
                        {
                            var keyName = command.Trim().Substring(4).Trim();
                            var controlChar = MapKeyToControl(keyName);
                            if (controlChar.HasValue)
                            {
                                TypeTextToActiveTerminal(new string((char)controlChar.Value, 1));
                                return (success: true, response: (string?)null, error: (string?)null);
                            }
                            return (success: false, response: (string?)null, error: $"Unknown key: {keyName}");
                        }
                        return (success: false, response: (string?)null, error: $"Unknown command: {command}");
                }
            }
            catch (Exception ex)
            {
                return (success: false, response: (string?)null, error: ex.Message);
            }
        });
        
        // Build response string
        if (!commandResult.success)
            return $"ERROR:{commandResult.error}";
        else if (!string.IsNullOrEmpty(commandResult.response))
            return commandResult.response;
        else
            return "OK";
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

    private void OnClosed(object? sender, EventArgs e)
    {
        RuntimeSettings.Changed -= OnRuntimeSettingsChanged;
        SgrColorArgb.AnsiPaletteChanged -= OnAnsiPaletteChanged;

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
        if (_windowTitleSubscribedTab != null)
        {
            UnsubscribeFromActiveTabTitle(_windowTitleSubscribedTab);
        }
        
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
            _viewModel.DuplicateTab(tvm);
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
