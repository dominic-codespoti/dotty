using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// One-shot draw operation that classifies the render thread's Skia context:
/// software (no lease/GrContext), software-GL (llvmpipe/softpipe/SwiftShader),
/// or hardware GPU. TerminalCanvas draws this on the first frame when the quad
/// path is enabled; the result gates whether quad rendering stays active.
///
/// DrawVertices through a software rasterizer — pure Skia CPU pipeline or
/// llvmpipe — regresses ~20x vs the DrawText glyph cache (77ms vs 3.4ms per
/// content frame at 73x136, measured 2026-08-26 under Xvfb where Avalonia's
/// X11 backend initializes software GL and GrContext is non-null). Only a
/// hardware GPU makes the quad path worthwhile.
/// </summary>
internal sealed class GpuProbeDrawOperation : ICustomDrawOperation
{
    public Rect Bounds => default;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

    public void Render(ImmediateDrawingContext context)
    {
        try
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null)
            {
                TerminalCanvas.CompleteGpuProbe(GpuClass.Software);
                return;
            }

            using var lease = feature.Lease();
            if (lease.GrContext == null)
            {
                TerminalCanvas.CompleteGpuProbe(GpuClass.Software);
                return;
            }

            // GrContext exists but may be backed by a CPU rasterizer
            // (llvmpipe under Xvfb/headless). Classify by GL renderer string.
            var renderer = SafeGlRenderer();
            if (IsSoftwareRenderer(renderer))
                TerminalCanvas.CompleteGpuProbe(GpuClass.SoftwareGl);
            else
                TerminalCanvas.CompleteGpuProbe(GpuClass.Hardware);
        }
        catch
        {
            TerminalCanvas.CompleteGpuProbe(GpuClass.Software);
        }
    }

    [DllImport("libGL.so.1")]
    private static extern IntPtr glGetString(int name);

    private const int GL_RENDERER = 0x1F01;

    private static string? SafeGlRenderer()
    {
        try
        {
            var ptr = glGetString(GL_RENDERER);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSoftwareRenderer(string? renderer) =>
        renderer != null && (
            renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("SWR", StringComparison.OrdinalIgnoreCase) ||
            renderer.Contains("Software Rasterizer", StringComparison.OrdinalIgnoreCase));

    public void Dispose() { }
}

/// <summary>Classification of the Skia render surface backing.</summary>
internal enum GpuClass
{
    Software = 1,
    SoftwareGl = 3,
    Hardware = 2,
}
