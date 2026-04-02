namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record representing padding thickness (Left, Top, Right, Bottom).
/// </summary>
public record ThicknessModel
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }

    public ThicknessModel() { }

    public ThicknessModel(double uniform) : this(uniform, uniform, uniform, uniform) { }

    public ThicknessModel(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical) { }

    public ThicknessModel(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>
    /// Default thickness (all zeros).
    /// </summary>
    public static ThicknessModel Default => new(0, 0, 0, 0);
}
