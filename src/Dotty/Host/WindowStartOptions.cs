using System;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Config;
using Dotty.Silk.Rendering;

namespace Dotty.Silk;

/// <summary>Window state applied once, when the window is first shown.</summary>
public enum WindowStartState
{
    Normal,
    Maximized,
    Fullscreen,
}

/// <summary>
/// Startup window options resolved from <c>window.*</c> configuration and the theme opacity.
/// Pure: the host reads it before window creation (alpha framebuffer, title, floating) and when loading
/// (initial grid, first-show state).
/// </summary>
internal readonly record struct WindowStartOptions(
    string Title,
    int Columns,
    int Rows,
    WindowStartState StartState,
    bool Floating,
    bool TransparentFramebuffer)
{
    /// <summary>Desktop identity: app/bundle id, Wayland <c>app_id</c>, X11 class and desktop-file name.</summary>
    public const string AppId = "io.github.dominic_codespoti.Dotty";

    /// <summary>X11 instance name (<c>WM_CLASS</c> res_name); the class is <see cref="AppId"/>.</summary>
    public const string X11InstanceName = "dotty";

    /// <summary>Largest accepted initial grid dimension; larger values are clamped.</summary>
    public const int MaxGridCells = 1000;

    /// <summary>Window-icon sizes embedded from <c>assets/icons</c> (GLFW picks the closest per use).</summary>
    public static ReadOnlySpan<int> IconSizes => [16, 32, 48, 64, 128, 256];

    /// <summary>Manifest resource name of the embedded <paramref name="size"/> px icon PNG.</summary>
    public static string IconResourceName(int size) => $"Dotty.Icon.{size}.png";

    /// <summary>
    /// <c>glfwSetWindowIcon</c> works on X11 and Windows; Wayland and macOS take the icon from the desktop
    /// entry or the app bundle and report <c>GLFW_FEATURE_UNAVAILABLE</c>.
    /// </summary>
    public static bool SupportsWindowIcon(DisplayPlatform platform) =>
        platform is DisplayPlatform.X11 or DisplayPlatform.Win32;

    public static WindowStartOptions Resolve(WindowUserConfig window, byte themeOpacity)
    {
        ArgumentNullException.ThrowIfNull(window);
        string title = string.IsNullOrWhiteSpace(window.Title) ? DottyDefaults.WindowTitle : window.Title;
        WindowStartState state = window.StartFullscreen ? WindowStartState.Fullscreen
            : window.StartMaximized ? WindowStartState.Maximized
            : WindowStartState.Normal;
        return new WindowStartOptions(
            title,
            GridDimension(window.Columns, DottyDefaults.WindowColumns),
            GridDimension(window.Rows, DottyDefaults.WindowRows),
            state,
            window.Floating,
            BackgroundOpacity.RequiresAlphaFramebuffer(window.Opacity, themeOpacity));
    }

    /// <summary>Configured cell count; zero or negative uses <paramref name="fallback"/>, large values are clamped.</summary>
    public static int GridDimension(int configured, int fallback) =>
        configured <= 0 ? fallback : Math.Min(configured, MaxGridCells);
}
