using System;
using System.Collections.Generic;
using Dotty.Runtime.Config;
using Dotty.Runtime.Hosting;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Dotty.Silk.Rendering;
using Silk.NET.Windowing;

namespace Dotty.Silk;

/// <summary>
/// Layout facts the bridge needs from the window host: the integer device-pixel cell (the exact
/// size the layout uses, reported to applications for CSI 14/16 t and kitty graphics), tab-bar
/// rows, device padding and the device-pixels-per-window-unit ratio for client sizes.
/// </summary>
internal readonly record struct VtHostLayout(
    int CellWidthPx,
    int CellHeightPx,
    int BarRows,
    DevicePadding Padding,
    float WindowUnitsToDevice,
    int Cols,
    int Rows);

/// <summary>
/// Glue between the VT adapters and the desktop window: feeds real pixel metrics and window
/// visibility into every adapter, and performs <c>CSI 8 t</c> resize requests. All decisions
/// live in <see cref="VtHostPolicy"/>; this class only reads window facts and applies results.
/// Every member except the adapter-event handler runs on the window/UI thread.
/// </summary>
internal static class VtHostBridge
{
    private const long SlowPollIntervalMs = 250;

    private readonly record struct Metrics(int CellWidth, int CellHeight, int PositionX, int PositionY, int ScreenWidth, int ScreenHeight);

    private static readonly VtResizeRequestQueue ResizeRequests = new();
    private static readonly HashSet<TerminalAdapter> Subscribed = new();
    private static readonly HashSet<TerminalAdapter> Scratch = new();
    private static readonly HashSet<TerminalAdapter> Stale = new();

    private static IWindow? _window;
    private static nint _nativeHandle;
    private static TerminalTabManager? _tabs;
    private static Func<VtHostLayout>? _layout;
    private static Action<int, int>? _setClientSize;
    private static bool _iconified;
    private static Metrics _metrics;
    private static bool _metricsValid;
    private static long _lastSlowPollTimestampMs = long.MinValue;
    private static int _positionX, _positionY, _screenWidth, _screenHeight;

    /// <summary>Starts bridging for this window. Call once after the window and tab manager exist.</summary>
    public static void Attach(IWindow window, TerminalTabManager tabs, Func<VtHostLayout> layout, Action<int, int> setClientSize)
    {
        Detach();
        _window = window;
        _nativeHandle = window.Native?.Glfw ?? 0;
        _tabs = tabs;
        _layout = layout;
        _setClientSize = setClientSize;
        _iconified = false;
        _metricsValid = false;
        _lastSlowPollTimestampMs = long.MinValue;
        ResizeRequests.Clear();
        OnSessionsChanged();
    }

    /// <summary>Stops bridging and unsubscribes from every adapter.</summary>
    public static void Detach()
    {
        foreach (var adapter in Subscribed)
            adapter.WindowResizeRequested -= OnResizeRequested;
        Subscribed.Clear();
        Scratch.Clear();
        ResizeRequests.Clear();
        _window = null;
        _nativeHandle = 0;
        _tabs = null;
        _layout = null;
        _setClientSize = null;
    }

    /// <summary>
    /// The set of sessions, the active tab or the pane topology changed: subscribe new adapters,
    /// drop stale ones, give new adapters the current metrics and refresh visibility.
    /// </summary>
    public static void OnSessionsChanged()
    {
        var tabs = _tabs;
        if (tabs is null)
            return;

        Scratch.Clear();
        var all = tabs.Tabs;
        for (int tabIndex = 0; tabIndex < all.Count; tabIndex++)
        {
            var leaves = all[tabIndex].PaneTree.Leaves;
            for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
            {
                var adapter = leaves[leafIndex].Session.Adapter;
                Scratch.Add(adapter);
                if (Subscribed.Add(adapter))
                {
                    adapter.WindowResizeRequested += OnResizeRequested;
                    if (_metricsValid)
                        PushMetrics(adapter, _metrics);
                }
            }
        }

        // Adapters of closed tabs/panes: unsubscribe (collected first; Subscribed is mutated).
        Stale.Clear();
        foreach (var adapter in Subscribed)
        {
            if (!Scratch.Contains(adapter))
                Stale.Add(adapter);
        }
        foreach (var adapter in Stale)
        {
            adapter.WindowResizeRequested -= OnResizeRequested;
            Subscribed.Remove(adapter);
        }
        Stale.Clear();

        VtHostPolicy.ApplyVisibility(tabs, _iconified);
    }

    /// <summary>Per-frame work on the UI thread: visibility edge, metrics push, queued resize.</summary>
    public static void Tick(long nowMs)
    {
        var window = _window;
        if (window is null || _tabs is null)
            return;

        bool iconified = IsIconified(window);
        if (iconified != _iconified)
        {
            _iconified = iconified;
            VtHostPolicy.ApplyVisibility(_tabs, iconified);
        }

        PollMetrics(window, nowMs);
        DrainResizeRequest(window);
    }

    // Runs on the parser thread, possibly under the buffer lock: only enqueue.
    private static void OnResizeRequested(int rows, int cols)
    {
        ResizeRequests.Post(rows, cols);
        HostWake.Request();
    }

    private static bool IsIconified(IWindow window)
    {
        if (NativeGlfwMetadata.IsWindowIconified(_nativeHandle))
            return true;
        var framebuffer = window.FramebufferSize;
        return framebuffer.X <= 0 || framebuffer.Y <= 0;
    }

    private static void PollMetrics(IWindow window, long nowMs)
    {
        var layout = _layout;
        if (layout is null)
            return;

        var facts = layout();
        // Window position / monitor size need native round trips on X11, so they are polled slowly.
        if (!_metricsValid || nowMs - _lastSlowPollTimestampMs >= SlowPollIntervalMs)
        {
            _lastSlowPollTimestampMs = nowMs;
            if (NativeGlfwMetadata.TryGetWindowPosition(_nativeHandle, out int x, out int y))
            {
                _positionX = x;
                _positionY = y;
            }
            if (NativeGlfwMetadata.TryGetPrimaryMonitorSize(out int screenWidth, out int screenHeight))
            {
                _screenWidth = screenWidth;
                _screenHeight = screenHeight;
            }
        }

        // The exact layout cell: kitty graphics and CSI 14/16 t must agree with what is drawn.
        var metrics = new Metrics(
            Math.Max(1, facts.CellWidthPx),
            Math.Max(1, facts.CellHeightPx),
            _positionX,
            _positionY,
            _screenWidth,
            _screenHeight);
        if (_metricsValid && metrics == _metrics)
            return;

        _metrics = metrics;
        _metricsValid = true;
        foreach (var adapter in Subscribed)
            PushMetrics(adapter, metrics);
    }

    private static void PushMetrics(TerminalAdapter adapter, Metrics metrics)
    {
        adapter.SetCellPixelSize(metrics.CellWidth, metrics.CellHeight);
        adapter.SetWindowPosition(metrics.PositionX, metrics.PositionY);
        if (metrics.ScreenWidth > 0 && metrics.ScreenHeight > 0)
            adapter.SetScreenPixelSize(metrics.ScreenWidth, metrics.ScreenHeight);
    }

    private static void DrainResizeRequest(IWindow window)
    {
        if (!ResizeRequests.TryTake(out int rows, out int cols))
            return;

        var tabs = _tabs;
        var layout = _layout;
        var setClientSize = _setClientSize;
        var active = tabs?.ActiveTab;
        if (tabs is null || layout is null || setClientSize is null || active is null)
            return;

        var config = UserConfigService.Current;
        var facts = layout();
        if (!VtHostPolicy.TryComputeResize(
                config.Vt.WindowResizeAllowed,
                tabs.Count,
                active.PaneTree.Leaves.Count,
                MapWindowState(window.WindowState),
                rows,
                cols,
                facts.Rows,
                facts.Cols,
                out int newRows,
                out int newCols))
        {
            return;
        }

        var (width, height) = GridFit.ClientSize(
            newCols, newRows, facts.CellWidthPx, facts.CellHeightPx, facts.BarRows, facts.Padding, facts.WindowUnitsToDevice);
        setClientSize(width, height);
    }

    private static VtWindowState MapWindowState(WindowState state) => state switch
    {
        WindowState.Normal => VtWindowState.Normal,
        WindowState.Maximized => VtWindowState.Maximized,
        WindowState.Fullscreen => VtWindowState.Fullscreen,
        _ => VtWindowState.Minimized,
    };
}
