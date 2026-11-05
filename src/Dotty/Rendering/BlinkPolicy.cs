using System;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Pure, allocation-free blink phase shared by the cursor (DECSCUSR odd values, <c>cursor.blink</c>) and SGR 5/6
/// text blink. The phase is on during <c>[origin + 2k·interval, origin + (2k+1)·interval)</c> and off in between;
/// times before the origin are on. <see cref="Steady"/> (the default value) never blinks.
/// </summary>
public readonly struct BlinkPolicy : IEquatable<BlinkPolicy>
{
    /// <summary>A phase that is always on and never toggles.</summary>
    public static BlinkPolicy Steady => default;

    /// <summary>Start of an on segment, in the same millisecond clock as the queries.</summary>
    public long PhaseOriginMs { get; }

    /// <summary>Length of one on (or off) segment in milliseconds; 0 when <see cref="IsBlinking"/> is false.</summary>
    public int IntervalMs { get; }

    /// <summary>False for <see cref="Steady"/>: <see cref="IsOn"/> is always true and nothing is scheduled.</summary>
    public bool IsBlinking => IntervalMs > 0;

    /// <param name="phaseOriginMs">Time at which an on segment starts.</param>
    /// <param name="intervalMs">Segment length; values below 1 are raised to 1.</param>
    public BlinkPolicy(long phaseOriginMs, int intervalMs)
    {
        PhaseOriginMs = phaseOriginMs;
        IntervalMs = Math.Max(1, intervalMs);
    }

    /// <summary>Whether the blinking element is visible at <paramref name="nowMs"/>.</summary>
    public bool IsOn(long nowMs)
    {
        if (!IsBlinking || nowMs < PhaseOriginMs)
            return true;
        return ((nowMs - PhaseOriginMs) / IntervalMs & 1) == 0;
    }

    /// <summary>
    /// The first time strictly after <paramref name="nowMs"/> at which <see cref="IsOn"/> changes;
    /// <see cref="long.MaxValue"/> for a steady policy.
    /// </summary>
    public long NextToggleMs(long nowMs)
    {
        if (!IsBlinking)
            return long.MaxValue;
        if (nowMs < PhaseOriginMs)
            return PhaseOriginMs + IntervalMs;
        long segment = (nowMs - PhaseOriginMs) / IntervalMs;
        return PhaseOriginMs + (segment + 1) * IntervalMs;
    }

    public bool Equals(BlinkPolicy other) => PhaseOriginMs == other.PhaseOriginMs && IntervalMs == other.IntervalMs;

    public override bool Equals(object? obj) => obj is BlinkPolicy other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PhaseOriginMs, IntervalMs);

    public static bool operator ==(BlinkPolicy left, BlinkPolicy right) => left.Equals(right);

    public static bool operator !=(BlinkPolicy left, BlinkPolicy right) => !left.Equals(right);
}
