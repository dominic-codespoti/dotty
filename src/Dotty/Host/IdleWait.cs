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
    bool SelectionAutoscrollActive);

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
        if (inputs.CursorBlinkActive)
            timeout = Math.Min(timeout, Math.Max(0, inputs.LastCursorBlinkMs + inputs.CursorBlinkIntervalMs - inputs.NowMs));
        timeout = Math.Min(timeout, Math.Max(0, inputs.LastLuaStatusRefreshMs + inputs.LuaStatusRefreshIntervalMs - inputs.NowMs));
        return (int)timeout;
    }
}
