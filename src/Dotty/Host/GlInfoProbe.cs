using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Dotty.Silk;

/// <summary>Strings reported by the OpenGL driver for the context Dotty would create.</summary>
internal readonly record struct GlInfo(string? Vendor, string? Renderer, string? Version);

/// <summary>
/// Creates a hidden window with the same OpenGL 3.3 core request as <see cref="DottyWindowHost"/> and
/// reads the driver strings (<c>dotty +diagnose --gl</c>). Needs a display; throws when the context fails.
/// </summary>
internal static class GlInfoProbe
{
    public static GlInfo Query()
    {
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        WindowOptions options = WindowOptions.Default with
        {
            Title = "Dotty OpenGL probe",
            Size = new Vector2D<int>(64, 64),
            IsVisible = false,
            VSync = false,
            ShouldSwapAutomatically = false,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };

        IWindow window = Window.Create(options);
        try
        {
            window.Initialize();
            using GL gl = window.CreateOpenGL();
            return new GlInfo(
                gl.GetStringS(StringName.Vendor),
                gl.GetStringS(StringName.Renderer),
                gl.GetStringS(StringName.Version));
        }
        finally
        {
            window.Reset();
        }
    }
}
