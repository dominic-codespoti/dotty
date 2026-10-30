using System;

namespace Dotty.Silk;

/// <summary>Window-level effects of one accepted terminal bell.</summary>
[Flags]
internal enum BellEffects
{
    None = 0,
    /// <summary>Request window attention (<c>glfwRequestWindowAttention</c>).</summary>
    Attention = 1 << 0,
    /// <summary>Start the visual flash overlay.</summary>
    Flash = 1 << 1,
}

/// <summary>
/// Pure bell decisions for the host: rate limiting, which effects a bell produces, and the
/// visual-flash alpha envelope and frame pacing. Times are host clock milliseconds; nothing here
/// reads a clock or allocates, so every rule is testable with plain numbers.
/// </summary>
internal static class BellPolicy
{
    /// <summary>Sentinel for "no bell yet" / "no flash running".</summary>
    internal const long Never = long.MinValue;

    /// <summary>Bells closer together than this produce effects only once (a BEL flood flashes once per interval).</summary>
    internal const long MinIntervalMs = 150;

    /// <summary>Length of the flash fade from <see cref="FlashPeakAlpha"/> to transparent.</summary>
    internal const long FlashDurationMs = 150;

    /// <summary>Overlay opacity at the start of the flash.</summary>
    internal const float FlashPeakAlpha = 0.35f;

    /// <summary>Spacing of flash frames, about 60 Hz.</summary>
    internal const long FlashFrameIntervalMs = 16;

    /// <summary>
    /// Rate limiter: true when a bell at <paramref name="nowMs"/> may produce effects, in which case
    /// <paramref name="lastAcceptedMs"/> becomes <paramref name="nowMs"/>. Pass <see cref="Never"/> before the first bell.
    /// A clock that steps backwards accepts the bell rather than suppressing bells until it catches up.
    /// </summary>
    internal static bool TryAccept(ref long lastAcceptedMs, long nowMs)
    {
        if (lastAcceptedMs != Never)
        {
            long elapsed = nowMs - lastAcceptedMs;
            if (elapsed >= 0 && elapsed < MinIntervalMs)
                return false;
        }

        lastAcceptedMs = nowMs;
        return true;
    }

    /// <summary>Effects of one accepted bell. Attention is requested only while the window is unfocused.</summary>
    internal static BellEffects Decide(bool attention, bool visual, bool windowFocused)
    {
        BellEffects effects = BellEffects.None;
        if (attention && !windowFocused)
            effects |= BellEffects.Attention;
        if (visual)
            effects |= BellEffects.Flash;
        return effects;
    }

    /// <summary>
    /// Flash overlay opacity <paramref name="elapsedMs"/> after the flash started: <see cref="FlashPeakAlpha"/> at 0,
    /// easing out quadratically to 0 at <see cref="FlashDurationMs"/>; 0 outside that window.
    /// </summary>
    internal static float FlashAlpha(long elapsedMs)
    {
        if (elapsedMs < 0 || elapsedMs >= FlashDurationMs)
            return 0f;

        float remaining = 1f - (float)elapsedMs / FlashDurationMs;
        return FlashPeakAlpha * remaining * remaining;
    }

    /// <summary>
    /// Deadline of the next flash frame after <paramref name="nowMs"/> for a flash started at <paramref name="startMs"/>:
    /// the next multiple of <see cref="FlashFrameIntervalMs"/>, capped at the end of the flash so the frame that
    /// clears the overlay is never skipped. Returns <see cref="Never"/> once the flash has ended.
    /// </summary>
    internal static long NextFlashFrameMs(long startMs, long nowMs)
    {
        long endMs = startMs + FlashDurationMs;
        if (nowMs >= endMs)
            return Never;

        long elapsed = Math.Max(0L, nowMs - startMs);
        long next = startMs + (elapsed / FlashFrameIntervalMs + 1) * FlashFrameIntervalMs;
        return Math.Min(next, endMs);
    }
}
