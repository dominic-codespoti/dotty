namespace Dotty.App.Rendering;

public sealed class GlyphRasterizationOptions
{
    public bool IsAntialias { get; init; } = true;
    public bool IsLinearText { get; init; } = true;
    public bool SubpixelText { get; init; } = true;
    public bool IsAutohinted { get; init; } = true;
    public bool LcdRenderText { get; init; } = true;
}
