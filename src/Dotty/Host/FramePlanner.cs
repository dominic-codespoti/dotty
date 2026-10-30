using System;

namespace Dotty.Silk;

/// <summary>Whether a frame iteration may look at the scene at all.</summary>
public enum FrameGate
{
    /// <summary>The window can present: go on to <see cref="FramePlanner.Decide"/>.</summary>
    Proceed,
    /// <summary>A synchronized update (CSI ? 2026) withholds presentation: wait for its end or failsafe.</summary>
    SyncHold,
    /// <summary>Nothing visible to draw into (iconified, or a zero-sized framebuffer): wait without blink work.</summary>
    NoSurface,
}

/// <summary>What a presentable frame iteration does.</summary>
public enum FrameAction
{
    /// <summary>Nothing changed: keep the front buffer and wait for the next deadline.</summary>
    Idle,
    /// <summary>Only streaming content changed and the last present was too recent: wait <see cref="FramePlan.HoldMs"/>.</summary>
    Hold,
    /// <summary>Compose, render and swap.</summary>
    Render,
}

internal readonly record struct FramePlan(FrameAction Action, long HoldMs);

/// <summary>Inputs of <see cref="FramePlanner.Decide"/>; all times share one millisecond clock.</summary>
/// <param name="PendingReasons">Invalidation reasons not yet consumed.</param>
/// <param name="StateDirty">A visible pane's buffer generation, the framebuffer size or the atlas version differs from the last present.</param>
/// <param name="LastPresentMs">Time of the last swap; 0 before the first one.</param>
/// <param name="LastInteractionMs">Time of the last input event.</param>
/// <param name="ContentFrameIntervalMs">Minimum spacing of content-only frames (<see cref="FramePlanner.MinContentFrameIntervalMs"/>).</param>
internal readonly record struct FramePlanInputs(
    long NowMs,
    WindowFrameReason PendingReasons,
    bool StateDirty,
    long LastPresentMs,
    long LastInteractionMs,
    int ContentFrameIntervalMs);

/// <summary>
/// The host's per-frame presentation decisions as pure functions (plan-11 WI-3a/3b): whether the window can present,
/// whether anything changed, how long streaming content is held back, and the content frame interval for a refresh
/// rate. <c>DottyWindowHost.OnRenderCore</c> gathers the inputs and acts on the result; nothing here touches GL,
/// GLFW or shared state.
/// </summary>
internal static class FramePlanner
{
    /// <summary>Content-only frames are at least this far apart by default (about 125 frames/s).</summary>
    internal const int FixedContentFrameMinIntervalMs = 8;
    /// <summary>Content-only frames while a PTY consumer is behind, so rendering yields the buffer lock to parsing.</summary>
    internal const int BackloggedFrameIntervalMs = 30;
    /// <summary>Frames this soon after input are never held back.</summary>
    internal const int InteractiveWindowMs = 250;
    /// <summary>Refresh rate assumed when the monitor does not report a usable one.</summary>
    internal const int FallbackRefreshHz = 60;
    internal const int MinRefreshHz = 30;
    internal const int MaxRefreshHz = 240;
    /// <summary>Reported rates above this are treated as bogus (fallback), not clamped.</summary>
    internal const int MaxPlausibleRefreshHz = 1000;

    internal static FrameGate Gate(bool synchronizedUpdateHeld, int framebufferWidth, int framebufferHeight, bool iconified)
    {
        if (synchronizedUpdateHeld)
            return FrameGate.SyncHold;
        if (iconified || framebufferWidth <= 0 || framebufferHeight <= 0)
            return FrameGate.NoSurface;
        return FrameGate.Proceed;
    }

    internal static FramePlan Decide(in FramePlanInputs inputs)
    {
        if (inputs.PendingReasons == WindowFrameReason.None && !inputs.StateDirty)
            return new FramePlan(FrameAction.Idle, 0);

        // Anything but streaming content (input, resize, overlays, blink, ...) presents now.
        bool contentOnly = (inputs.PendingReasons & ~WindowFrameReason.Content) == WindowFrameReason.None;
        if (contentOnly)
        {
            long holdMs = CoalesceRemainingMs(inputs.NowMs, inputs.LastPresentMs, inputs.LastInteractionMs, inputs.ContentFrameIntervalMs);
            if (holdMs > 0)
                return new FramePlan(FrameAction.Hold, holdMs);
        }

        return new FramePlan(FrameAction.Render, 0);
    }

    /// <summary>
    /// Milliseconds a content-only frame should still be held back; 0 means present now. Recent interaction and the
    /// first frame (no present yet) are never held back.
    /// </summary>
    internal static long CoalesceRemainingMs(long now, long lastPresent, long lastInteraction, int intervalMs)
    {
        if (lastPresent == 0 || now - lastInteraction < InteractiveWindowMs)
            return 0;

        return Math.Max(0L, intervalMs - (now - lastPresent));
    }

    /// <summary>
    /// Minimum spacing of content-only frames. With <paramref name="refreshAware"/> off (the default until frame
    /// timings justify the change) this is <see cref="FixedContentFrameMinIntervalMs"/>, or
    /// <see cref="BackloggedFrameIntervalMs"/> while a consumer is backlogged. With it on, it is one refresh period
    /// (rounded down, so no refresh is skipped) of <paramref name="refreshHz"/> clamped to
    /// [<see cref="MinRefreshHz"/>, <see cref="MaxRefreshHz"/>]; unknown, non-positive or implausible rates use
    /// <see cref="FallbackRefreshHz"/>; backlogged frames stay at least <see cref="BackloggedFrameIntervalMs"/> apart.
    /// </summary>
    internal static int MinContentFrameIntervalMs(int refreshHz, bool backlogged, bool refreshAware)
    {
        if (!refreshAware)
            return backlogged ? BackloggedFrameIntervalMs : FixedContentFrameMinIntervalMs;

        int periodMs = 1000 / EffectiveRefreshHz(refreshHz);
        return backlogged ? Math.Max(BackloggedFrameIntervalMs, periodMs) : periodMs;
    }

    internal static int EffectiveRefreshHz(int refreshHz) =>
        refreshHz <= 0 || refreshHz > MaxPlausibleRefreshHz
            ? FallbackRefreshHz
            : Math.Clamp(refreshHz, MinRefreshHz, MaxRefreshHz);
}
