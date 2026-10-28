namespace Dotty.Silk;

internal readonly record struct IdleWaitInputs(
    long NowMs,
    bool RetryShortly,
    bool CursorBlinkActive,
    long LastCursorBlinkMs,
    int CursorBlinkIntervalMs,
    long LastLuaStatusRefreshMs,
    int LuaStatusRefreshIntervalMs,
    long CoalesceRemainingMs,
    long SyncHoldRemainingMs,
    bool SelectionAutoscrollActive,
    long ImageAnimationRemainingMs = 0);

internal static class IdleWait
{
    internal const int SafetyCapMs = 100;
    internal const int AutoscrollTickMs = 16;
    internal const int RetryMs = 1;

    internal static int Compute(in IdleWaitInputs inputs)
    {
        long timeout = SafetyCapMs;
        if (inputs.RetryShortly) timeout = Math.Min(timeout, RetryMs);
        if (inputs.SelectionAutoscrollActive) timeout = Math.Min(timeout, AutoscrollTickMs);
        if (inputs.CoalesceRemainingMs > 0) timeout = Math.Min(timeout, inputs.CoalesceRemainingMs);
        if (inputs.SyncHoldRemainingMs > 0) timeout = Math.Min(timeout, inputs.SyncHoldRemainingMs);
        if (inputs.ImageAnimationRemainingMs > 0) timeout = Math.Min(timeout, inputs.ImageAnimationRemainingMs);
        if (inputs.CursorBlinkActive)
            timeout = Math.Min(timeout, Math.Max(0, inputs.LastCursorBlinkMs + inputs.CursorBlinkIntervalMs - inputs.NowMs));
        timeout = Math.Min(timeout, Math.Max(0, inputs.LastLuaStatusRefreshMs + inputs.LuaStatusRefreshIntervalMs - inputs.NowMs));
        return (int)timeout;
    }

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
