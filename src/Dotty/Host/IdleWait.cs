namespace Dotty.Silk;

/// <summary>Inputs of <see cref="IdleWait.Compute"/>; all times share one millisecond clock.</summary>
/// <param name="NextBlinkToggleMs">Absolute time of the next cursor/text blink toggle; <see cref="IdleWait.NoDeadline"/> when nothing blinks.</param>
/// <param name="NextLuaStatusRefreshMs">Absolute time of the next Lua status poll; <see cref="IdleWait.NoDeadline"/> when no poll runs.</param>
/// <param name="ImageAnimationRemainingMs">Relative; 0 = none (see <see cref="IdleWait.ImageAnimationRemainingMs"/>).</param>
/// <param name="ControlWaitRemainingMs">Relative; 0 = no parked <c>WAIT_FOR_IDLE</c>.</param>
/// <param name="BellFlashRemainingMs">Relative; 0 = no visual-bell frame due.</param>
internal readonly record struct IdleWaitInputs(
    long NowMs,
    bool RetryShortly,
    long NextBlinkToggleMs,
    long NextLuaStatusRefreshMs,
    long CoalesceRemainingMs,
    long SyncHoldRemainingMs,
    bool SelectionAutoscrollActive,
    long ImageAnimationRemainingMs = 0,
    long ControlWaitRemainingMs = 0,
    long BellFlashRemainingMs = 0);

/// <summary>
/// How long the idle UI loop may block in <c>glfwWaitEventsTimeout</c>: the nearest deadline, bounded by
/// <see cref="SafetyCapMs"/>. Input events and <see cref="HostWake.Request"/> end the wait early, so the cap only
/// bounds the lag of a wake that was never requested; <c>HostWakeCoverageTests</c> guards that every host queue
/// producer requests one.
/// </summary>
internal static class IdleWait
{
    internal const int SafetyCapMs = 1000;
    internal const int AutoscrollTickMs = 16;
    internal const int RetryMs = 1;
    internal const long NoDeadline = long.MaxValue;

    internal static int Compute(in IdleWaitInputs inputs)
    {
        long timeout = SafetyCapMs;
        if (inputs.RetryShortly) timeout = Math.Min(timeout, RetryMs);
        if (inputs.SelectionAutoscrollActive) timeout = Math.Min(timeout, AutoscrollTickMs);
        if (inputs.CoalesceRemainingMs > 0) timeout = Math.Min(timeout, inputs.CoalesceRemainingMs);
        if (inputs.SyncHoldRemainingMs > 0) timeout = Math.Min(timeout, inputs.SyncHoldRemainingMs);
        if (inputs.ImageAnimationRemainingMs > 0) timeout = Math.Min(timeout, inputs.ImageAnimationRemainingMs);
        if (inputs.ControlWaitRemainingMs > 0) timeout = Math.Min(timeout, inputs.ControlWaitRemainingMs);
        if (inputs.BellFlashRemainingMs > 0) timeout = Math.Min(timeout, inputs.BellFlashRemainingMs);
        timeout = Math.Min(timeout, UntilDeadline(inputs.NextBlinkToggleMs, inputs.NowMs));
        timeout = Math.Min(timeout, UntilDeadline(inputs.NextLuaStatusRefreshMs, inputs.NowMs));
        return (int)timeout;
    }

    /// <summary>Milliseconds until an absolute deadline: 0 when due or overdue, <see cref="SafetyCapMs"/> for none.</summary>
    private static long UntilDeadline(long deadlineMs, long nowMs) =>
        deadlineMs == NoDeadline ? SafetyCapMs : Math.Max(0L, deadlineMs - nowMs);

    /// <summary>
    /// Milliseconds until an image animation deadline, for <see cref="IdleWaitInputs.ImageAnimationRemainingMs"/>:
    /// 0 when there is no deadline (<paramref name="deadlineTicks"/> == 0), otherwise at least 1 (a due or overdue
    /// deadline must still yield a positive wait so the input is not mistaken for "none"), rounded up so the wake
    /// never fires before the deadline. Both tick values must come from the same clock (<see cref="TimeProvider.System"/>
    /// timestamps, the clock behind <c>IKittyGraphicsHost.NowTicks</c>); <paramref name="ticksPerSecond"/> is its frequency.
    /// </summary>
    internal static long ImageAnimationRemainingMs(long deadlineTicks, long nowTicks, long ticksPerSecond)
    {
        if (deadlineTicks == 0 || ticksPerSecond <= 0)
            return 0;
        long remaining = deadlineTicks - nowTicks;
        if (remaining <= 0)
            return 1;
        long wholeSeconds = remaining / ticksPerSecond;
        if (wholeSeconds >= int.MaxValue / 1000)
            return int.MaxValue;
        long fractionMs = ((remaining % ticksPerSecond) * 1000 + ticksPerSecond - 1) / ticksPerSecond;
        return Math.Max(1, wholeSeconds * 1000 + fractionMs);
    }
}
