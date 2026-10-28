using System.Runtime.InteropServices;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Per-instance data for one textured image quad. Position and size are framebuffer pixels (top-left origin);
/// U0..V1 select the sub-rectangle of the texture (0..1, V0 at the top row of the image).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ImageQuadInstance
{
    public float X, Y, W, H, U0, V0, U1, V1, Alpha;
}
