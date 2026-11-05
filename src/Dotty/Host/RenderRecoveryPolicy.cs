namespace Dotty.Silk;

/// <summary>Where <see cref="RenderRecoveryPolicy"/> stands after the failures seen so far.</summary>
public enum RenderRecoveryState
{
    /// <summary>No recent failure.</summary>
    Healthy,
    /// <summary>One failure: the frame is retried as is.</summary>
    Retrying,
    /// <summary>A second failure: the GL objects were rebuilt.</summary>
    Rebuilt,
    /// <summary>Recovery is exhausted: the user is asked; shells keep running and the window stays open.</summary>
    Fatal,
    /// <summary>
    /// The driver reset the GPU and the context is lost for good (<c>GLFW_LOSE_CONTEXT_ON_RESET</c>): rendering
    /// stops until Dotty restarts; shells keep running and the window stays open. Terminal.
    /// </summary>
    ContextLost,
}

/// <summary>What the host does about the failure just reported.</summary>
public enum RenderRecoveryAction
{
    /// <summary>Requeue the frame and render again.</summary>
    Retry,
    /// <summary>Dispose and recreate the renderer (programs, buffers, atlas and image textures), then render again.</summary>
    Rebuild,
    /// <summary>Stop retrying; show the recovery prompt (once) and wait for new work instead of spinning.</summary>
    Fatal,
    /// <summary>
    /// Stop drawing for good: report the lost context once (stderr, crash log, window title) and wait for new work
    /// without touching GL. Nothing drawn through the lost context, a prompt included, can be seen.
    /// </summary>
    StopRendering,
}

/// <summary>
/// Render-failure recovery (plan R4, plan-11 WI-5a/5b): retry once, rebuild the GL objects once, then ask the user;
/// never close the window or end the shells on a failure. A failure more than <see cref="HealthyPeriodMs"/> after
/// the previous one starts over, so rare unrelated faults never accumulate into <see cref="RenderRecoveryState.Fatal"/>.
/// <see cref="RenderRecoveryState.Fatal"/> is left only through <see cref="Reset"/> (the prompt's "try again").
/// A lost context (a driver-reported reset, or <c>GL_CONTEXT_LOST</c> after a rebuild) cannot be rebuilt in place:
/// it moves to <see cref="RenderRecoveryState.ContextLost"/> from any state, and nothing leaves that state.
/// Pure state machine; the caller supplies a millisecond clock.
/// </summary>
public sealed class RenderRecoveryPolicy
{
    public const long HealthyPeriodMs = 10_000;

    private long _lastFailureMs;

    public RenderRecoveryState State { get; private set; }

    /// <summary>Failures reported since construction (exceptions and lost contexts), for diagnostics.</summary>
    public long FailureCount { get; private set; }

    /// <summary>A frame failed with an exception.</summary>
    public RenderRecoveryAction OnRenderFailure(long nowMs)
    {
        StartOverIfHealthy(nowMs);
        Record(nowMs);
        State = State switch
        {
            RenderRecoveryState.Healthy => RenderRecoveryState.Retrying,
            RenderRecoveryState.Retrying => RenderRecoveryState.Rebuilt,
            RenderRecoveryState.ContextLost => RenderRecoveryState.ContextLost,
            _ => RenderRecoveryState.Fatal,
        };
        return ActionFor(State);
    }

    /// <summary>The driver reported a graphics reset, or the context is still lost after a rebuild.</summary>
    public RenderRecoveryAction OnContextLost(long nowMs)
    {
        Record(nowMs);
        State = RenderRecoveryState.ContextLost;
        return ActionFor(State);
    }

    /// <summary>A frame was presented; ends a recovery once it has stayed quiet for <see cref="HealthyPeriodMs"/>.</summary>
    public void OnFramePresented(long nowMs) => StartOverIfHealthy(nowMs);

    /// <summary>The user asked to try again after <see cref="RenderRecoveryState.Fatal"/>; a lost context stays lost.</summary>
    public void Reset()
    {
        if (State != RenderRecoveryState.ContextLost)
            State = RenderRecoveryState.Healthy;
    }

    private void Record(long nowMs)
    {
        FailureCount++;
        _lastFailureMs = nowMs;
    }

    private void StartOverIfHealthy(long nowMs)
    {
        if (State is RenderRecoveryState.Retrying or RenderRecoveryState.Rebuilt &&
            nowMs - _lastFailureMs >= HealthyPeriodMs)
        {
            State = RenderRecoveryState.Healthy;
        }
    }

    private static RenderRecoveryAction ActionFor(RenderRecoveryState state) => state switch
    {
        RenderRecoveryState.Retrying => RenderRecoveryAction.Retry,
        RenderRecoveryState.Rebuilt => RenderRecoveryAction.Rebuild,
        RenderRecoveryState.ContextLost => RenderRecoveryAction.StopRendering,
        _ => RenderRecoveryAction.Fatal,
    };
}
