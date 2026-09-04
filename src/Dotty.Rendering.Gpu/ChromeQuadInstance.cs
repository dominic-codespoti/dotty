using System.Runtime.InteropServices;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Per-instance data for one pixel-precise rounded-rect "chrome" quad: tab
/// pills, buttons, hover highlights, and soft shadows drawn on top of the
/// character-grid cell pipeline (<see cref="CellInstance"/>).
/// Unlike <see cref="CellInstance"/>, position and size are arbitrary
/// framebuffer pixels rather than grid cells — that sub-cell precision is
/// what makes real rounded corners and floating pill shapes possible.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChromeQuadInstance
{
    /// <summary>Top-left X of the nominal rect, in framebuffer pixels.</summary>
    public float X;

    /// <summary>Top-left Y of the nominal rect, in framebuffer pixels.</summary>
    public float Y;

    /// <summary>Width of the nominal rect, in framebuffer pixels.</summary>
    public float W;

    /// <summary>Height of the nominal rect, in framebuffer pixels.</summary>
    public float H;

    /// <summary>Corner radius, in pixels.</summary>
    public float Radius;

    /// <summary>
    /// Edge softness, in pixels. 0 gives a crisp anti-aliased edge; larger
    /// values blur the edge outward, used for drop shadows.
    /// </summary>
    public float Blur;

    /// <summary>Fill color at the top edge (straight alpha), red channel.</summary>
    public float TopR;
    public float TopG;
    public float TopB;
    public float TopA;

    /// <summary>Fill color at the bottom edge (straight alpha), red channel.</summary>
    public float BottomR;
    public float BottomG;
    public float BottomB;
    public float BottomA;
}
