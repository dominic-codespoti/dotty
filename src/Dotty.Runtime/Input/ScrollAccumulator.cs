using System;

namespace Dotty.Runtime.Input;

/// <summary>
/// Whole scroll steps produced by one wheel event, signed like the host deltas: positive <see cref="Vertical"/> is
/// up (towards history, wheel button 64), positive <see cref="Horizontal"/> is left (wheel button 66).
/// </summary>
public readonly record struct ScrollSteps(int Horizontal, int Vertical)
{
    public bool IsEmpty => Horizontal == 0 && Vertical == 0;
}

/// <summary>
/// Converts wheel deltas into whole steps with fractional carry, per axis. A discrete wheel notch (delta 1) at an
/// integral multiplier yields exactly <c>multiplier</c> steps; precise devices (touchpads, delta &lt; 1) accumulate until
/// a whole step is reached instead of rounding every event up. Reversing direction drops the opposite remainder so a
/// stale fraction never produces a step the wrong way. The sub-step remainder stays readable for smooth scrolling.
/// </summary>
public struct ScrollAccumulator
{
    /// <summary>Upper bound on the steps one event may produce (guards against absurd deltas).</summary>
    public const int MaxStepsPerEvent = 10_000;

    // Absorbs binary rounding of decimal deltas: 30 x 0.1 x 3 must give exactly 9 steps.
    private const double Epsilon = 1e-6;

    private double _horizontal;
    private double _vertical;

    /// <summary>Sub-step horizontal remainder carried to the next event.</summary>
    public readonly double HorizontalRemainder => _horizontal;

    /// <summary>Sub-step vertical remainder carried to the next event.</summary>
    public readonly double VerticalRemainder => _vertical;

    /// <summary>Adds one event's deltas scaled by <paramref name="multiplier"/> (steps per unit delta) and returns the whole steps.</summary>
    public ScrollSteps Add(double deltaX, double deltaY, double multiplier)
    {
        if (!(multiplier > 0) || !double.IsFinite(multiplier))
            return default;
        return new ScrollSteps(Step(ref _horizontal, deltaX, multiplier), Step(ref _vertical, deltaY, multiplier));
    }

    /// <summary>Drops both remainders (the scrolled pane or the scroll target changed).</summary>
    public void Reset()
    {
        _horizontal = 0;
        _vertical = 0;
    }

    private static int Step(ref double carry, double delta, double multiplier)
    {
        double scaled = delta * multiplier;
        if (scaled == 0 || !double.IsFinite(scaled))
            return 0;
        if (carry != 0 && Math.Sign(carry) != Math.Sign(scaled))
            carry = 0;

        carry += scaled;
        double whole = carry > 0 ? Math.Floor(carry + Epsilon) : Math.Ceiling(carry - Epsilon);
        carry -= whole;
        if (Math.Abs(carry) < Epsilon)
            carry = 0;
        return (int)Math.Clamp(whole, -MaxStepsPerEvent, MaxStepsPerEvent);
    }
}
