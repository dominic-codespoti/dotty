using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Dotty.Runtime.Config;
using Dotty.Runtime.Panes;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Dotty.Silk;

/// <summary>
/// Inputs of <see cref="FramePlanner"/> and <see cref="IdleWait"/> that the host owns (plan-11 WI-3b/3d): monitor
/// refresh rate, iconified state, blink and Lua-status deadlines, consumer backlog. UI thread unless noted.
/// </summary>
internal static partial class DottyWindowHost
{
    /// <summary>
    /// <c>DOTTY_REFRESH_PACING=1</c> derives the content frame interval from the monitor refresh rate instead of the
    /// fixed 8 ms. Off by default until <c>STATS</c> frame timings show the change helps (plan-11 WI-3b).
    /// </summary>
    internal const string RefreshPacingEnvironmentVariable = "DOTTY_REFRESH_PACING";

    private static bool _refreshAwarePacing;
    private static int _monitorRefreshHz;
    private static bool _monitorRefreshHzStale = true;
    // Written on the UI thread, read by the system-theme poller.
    private static bool _windowIconified;

    private static void ConfigureFramePacing()
    {
        _refreshAwarePacing = string.Equals(
            Environment.GetEnvironmentVariable(RefreshPacingEnvironmentVariable), "1", StringComparison.Ordinal);
        _monitorRefreshHzStale = true;
        Volatile.Write(ref _windowIconified, _window.WindowState == WindowState.Minimized);
        _window.Move += OnWindowMoved;
        _window.StateChanged += OnWindowStateChanged;
    }

    private static void UnsubscribeFramePacing()
    {
        _window.Move -= OnWindowMoved;
        _window.StateChanged -= OnWindowStateChanged;
    }

    // The window may now be on another monitor; re-read lazily on the next rendered frame (the lookup allocates).
    private static void OnWindowMoved(Vector2D<int> position) => _monitorRefreshHzStale = true;

    private static void OnWindowStateChanged(WindowState state)
    {
        bool iconified = state == WindowState.Minimized;
        if (iconified == Volatile.Read(ref _windowIconified))
            return;

        Volatile.Write(ref _windowIconified, iconified);
        _monitorRefreshHzStale = true;
        // Nothing is drawn while iconified; present a fresh frame on restore.
        if (!iconified)
            WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
    }

    /// <summary>
    /// Refresh rate of the monitor Silk.NET associates with the window (the one containing it, else the primary),
    /// from its current video mode; 0 when unknown (<see cref="FramePlanner"/> then assumes 60 Hz).
    /// </summary>
    private static void RefreshMonitorRefreshRateIfStale()
    {
        if (!_monitorRefreshHzStale)
            return;

        _monitorRefreshHzStale = false;
        try
        {
            _monitorRefreshHz = _window.Monitor?.VideoMode.RefreshRate ?? 0;
        }
        catch (Exception)
        {
            // GLFW reports monitor errors through its error callback, which may throw (Windows); a missing
            // video mode only costs the 60 Hz fallback.
            _monitorRefreshHz = 0;
        }
    }

    /// <summary><c>STATS</c>: <c>,"refreshRateHz":n,"refreshAwarePacing":b,"contentFrameIntervalMs":n,"iconified":b</c>.</summary>
    private static void AppendFramePacingJson(StringBuilder json)
    {
        json.Append(",\"refreshRateHz\":").Append(_monitorRefreshHz.ToString(CultureInfo.InvariantCulture))
            .Append(",\"refreshAwarePacing\":").Append(_refreshAwarePacing ? "true" : "false")
            .Append(",\"contentFrameIntervalMs\":").Append(ContentFrameIntervalMs(anyBacklogged: false).ToString(CultureInfo.InvariantCulture))
            .Append(",\"iconified\":").Append(Volatile.Read(ref _windowIconified) ? "true" : "false");
    }

    private static int ContentFrameIntervalMs(bool anyBacklogged) =>
        FramePlanner.MinContentFrameIntervalMs(_monitorRefreshHz, anyBacklogged, _refreshAwarePacing, _graphicsClassification.IsSoftware);

    private static bool AnyVisibleLeafBacklogged()
    {
        LeafPane[] leaves = _visibleLeaves;
        for (int i = 0; i < _visibleLeafCount; i++)
        {
            if (leaves[i].Session.OutputBacklogged)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Absolute time of the next blink toggle from the shared <see cref="BlinkPolicy"/>, or
    /// <see cref="IdleWait.NoDeadline"/> (same sentinel as a steady policy): nothing blinks while iconified,
    /// unfocused or with blinking disabled.
    /// </summary>
    private static long NextBlinkToggleMs(bool includeBlink, long now) =>
        includeBlink && !Volatile.Read(ref _windowIconified)
            ? CurrentBlinkPolicy(_tabManager?.ActiveTab != null).NextToggleMs(now)
            : IdleWait.NoDeadline;

    /// <summary>
    /// The status text only changes with time when an <c>update_status</c> hook formats it; the built-in shell
    /// status ("running", "exit N") changes only with PTY output and is refreshed on dirty frames instead. Nothing
    /// is polled while iconified.
    /// </summary>
    private static bool LuaStatusPollActive() =>
        !Volatile.Read(ref _windowIconified) && _luaHost?.Hooks.HasStatusFormatter == true;

    private static long NextLuaStatusRefreshMs() =>
        LuaStatusPollActive() ? _lastLuaStatusRefreshTimestampMs + LuaStatusRefreshIntervalMs : IdleWait.NoDeadline;
}
