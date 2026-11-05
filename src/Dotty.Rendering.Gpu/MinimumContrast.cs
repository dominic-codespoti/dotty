using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// <c>text.minimumContrast</c>: raises a foreground's WCAG 2 contrast ratio against its visible background to a floor
/// by mixing it toward white or black (whichever the foreground is already heading for, unless only the other can
/// reach the floor). The smallest mix that meets the floor wins, so hue survives where possible. Pure and
/// allocation-free; a floor of 1 (the default) is off.
/// </summary>
public static class MinimumContrast
{
    /// <summary>The <c>text.minimumContrast</c> value that disables the adjustment.</summary>
    public const float Off = 1f;

    /// <summary>The largest WCAG ratio (white on black).</summary>
    public const float MaxRatio = 21f;

    private const int SearchSteps = 16;

    private static readonly double[] LinearChannel = BuildLinearTable();

    /// <summary>Clamps the configured floor to 1-21; non-finite values are <see cref="Off"/>.</summary>
    public static float Normalize(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, Off, MaxRatio) : Off;

    /// <summary>WCAG 2 relative luminance of an sRGB colour (alpha ignored).</summary>
    public static double RelativeLuminance(SgrColorArgb color) =>
        0.2126 * LinearChannel[color.R] + 0.7152 * LinearChannel[color.G] + 0.0722 * LinearChannel[color.B];

    /// <summary>WCAG 2 contrast ratio, 1-21, symmetric in its arguments.</summary>
    public static double Ratio(SgrColorArgb a, SgrColorArgb b) => Ratio(RelativeLuminance(a), RelativeLuminance(b));

    private static double Ratio(double la, double lb) =>
        la >= lb ? (la + 0.05) / (lb + 0.05) : (lb + 0.05) / (la + 0.05);

    /// <summary>
    /// <paramref name="foreground"/> unchanged when its ratio against opaque <paramref name="background"/> already meets
    /// <paramref name="minimumRatio"/> (or the floor is off); otherwise the closest mix toward white or black that meets
    /// it, or the full extreme when neither can. The foreground alpha is kept.
    /// </summary>
    public static SgrColorArgb Apply(SgrColorArgb foreground, SgrColorArgb background, float minimumRatio)
    {
        if (!(minimumRatio > Off))
            return foreground;
        double min = Math.Min(minimumRatio, MaxRatio);
        double lb = RelativeLuminance(background);
        double lf = RelativeLuminance(foreground);
        if (Ratio(lf, lb) >= min)
            return foreground;

        double whiteRatio = Ratio(1.0, lb);
        double blackRatio = Ratio(0.0, lb);
        bool towardWhite = lf >= lb;
        double preferred = towardWhite ? whiteRatio : blackRatio;
        double other = towardWhite ? blackRatio : whiteRatio;
        if (preferred < min && other > preferred)
            towardWhite = !towardWhite;
        byte target = towardWhite ? (byte)255 : (byte)0;

        SgrColorArgb extreme = Mix(foreground, target, 1.0);
        if (Ratio(RelativeLuminance(extreme), lb) < min)
            return extreme;

        // Ratio grows monotonically with the mix toward the chosen extreme: bisect for the smallest mix that meets it.
        double lo = 0.0, hi = 1.0;
        SgrColorArgb best = extreme;
        for (int i = 0; i < SearchSteps; i++)
        {
            double mid = (lo + hi) * 0.5;
            SgrColorArgb candidate = Mix(foreground, target, mid);
            if (Ratio(RelativeLuminance(candidate), lb) >= min)
            {
                best = candidate;
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }
        return best;
    }

    private static SgrColorArgb Mix(SgrColorArgb color, byte target, double t)
    {
        uint r = MixChannel(color.R, target, t);
        uint g = MixChannel(color.G, target, t);
        uint b = MixChannel(color.B, target, t);
        return new SgrColorArgb(((uint)color.A << 24) | (r << 16) | (g << 8) | b);
    }

    private static uint MixChannel(byte channel, byte target, double t) =>
        (uint)Math.Round(channel + (target - channel) * t, MidpointRounding.AwayFromZero);

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (int i = 0; i < table.Length; i++)
        {
            double c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return table;
    }
}
