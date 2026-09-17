namespace Dotty.Abstractions.Config;

/// <summary>
/// Padding or inset expressed in device-independent units.
/// </summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    /// <summary>
    /// Creates a uniform thickness applied to every edge.
    /// </summary>
    public Thickness(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    /// <summary>
    /// Total horizontal inset.
    /// </summary>
    public double Horizontal => Left + Right;

    /// <summary>
    /// Total vertical inset.
    /// </summary>
    public double Vertical => Top + Bottom;
}
